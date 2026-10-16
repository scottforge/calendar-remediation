using System.Security.Claims;
using System.Security.Cryptography;
using CalendarRemediation.Core.Discovery;
using Microsoft.Identity.Client;

namespace CalendarRemediation.Host.Authentication;

public sealed record ConnectionTicket(string Id, string Version, VerifiedMailbox? Mailbox);

// One local owner, one account generation, and one serialized MSAL/Graph work lane.
// Blazor callers must pass their original principal on every call, never a cached state DTO.
public sealed class LocalConnectionService(ILocalIdentityClient identity, ILocalCalendarClient calendar) : IAsyncDisposable
{
    public const string SessionClaim = "calendar_connection";
    private readonly object _sync = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly List<Task> _tasks = [];
    private Session? _session;
    private bool _disposed;
    private bool _clearing;

    public static IReadOnlyDictionary<string, string[]> LocationZones { get; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["BC"] = ["America/Vancouver", "America/Edmonton", "America/Dawson_Creek", "America/Fort_Nelson", "America/Creston"],
            ["AB"] = ["America/Edmonton"],
            ["SK"] = ["America/Regina", "America/Swift_Current"],
            ["MB"] = ["America/Winnipeg"]
        };

    public ConnectionTicket Bootstrap(ClaimsPrincipal principal)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_clearing) { throw new ConnectionException(new("busy", "Account sign-out is still finishing. Try again shortly.")); }
            if (Find(principal) is { } existing) { return Ticket(existing); }
            Invalidate();
            _session = new();
            return Ticket(_session);
        }
    }

    public bool IsCurrent(ClaimsPrincipal principal)
    {
        lock (_sync) { return !_disposed && Find(principal) is not null; }
    }

    public async Task<ConnectionState> GetStateAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        Session session;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Find(principal) is not { } current)
            {
                return new("", identity.Unavailable is null ? ConnectionPhase.SignedOut : ConnectionPhase.Disabled,
                    null, null, null, TimeZoneInfo.Local.Id, null, identity.Unavailable);
            }
            session = current;
            if (identity.Unavailable is not null || session.Phase != ConnectionPhase.SignedOut) { return State(session); }
        }
        await _work.WaitAsync(cancellationToken);
        try
        {
            lock (_sync) { RequireCurrent(session); }
            var cached = await identity.GetCachedAccountAsync(cancellationToken);
            lock (_sync) { RequireCurrent(session); session.CachedAccount = cached; }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            lock (_sync) { RequireCurrent(session); session.Problem = Problem(ex); }
        }
        finally { _work.Release(); }
        lock (_sync) { RequireCurrent(session); return State(session); }
    }

    public ConnectionTicket StartSignIn(ClaimsPrincipal principal, string version, bool interactive)
    {
        lock (_sync)
        {
            Require(principal, version);
            if (_clearing) { throw new ConnectionException(new("busy", "Account sign-out is still finishing. Try again shortly.")); }
            if (identity.Unavailable is { } unavailable) { throw new ConnectionException(unavailable); }
            Invalidate();
            var session = new Session { Phase = ConnectionPhase.SigningIn };
            _session = session;
            Track(session, ct => SignInAsync(session, interactive, ct));
            return Ticket(session);
        }
    }

    public ConnectionTicket Complete(ClaimsPrincipal principal, string version)
    {
        lock (_sync)
        {
            var session = Require(principal, version);
            if (session.Phase != ConnectionPhase.AwaitingCompletion || session.Mailbox is null || session.Account is null)
            {
                throw new ConnectionException(new("not-ready", "Sign-in has not completed identity verification."));
            }
            session.Id = NewId();
            session.Version = NewId();
            session.Phase = ConnectionPhase.Connected;
            return Ticket(session);
        }
    }

    public void ConfirmLocation(ClaimsPrincipal principal, string version, ConfirmedLocation location)
    {
        lock (_sync)
        {
            var session = RequireConnected(principal, version);
            if (!LocationZones.TryGetValue(location.Province, out var zones) || !zones.Contains(location.TimeZoneId, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(location.Community) || location.Community.Length > 100 || location.Community.Any(char.IsControl))
            {
                throw new ConnectionException(new("invalid-location", "Choose BC, AB, SK or MB, enter a community, and confirm one of its listed time zones."));
            }
            session.Operation?.Cancel();
            session.Location = location with { Community = location.Community.Trim() };
            session.Discovery = null;
            session.Problem = null;
            session.Phase = ConnectionPhase.Connected;
            // A completed old discovery cannot publish into this location generation.
            session.Revision++;
        }
    }

    public void StartDiscovery(ClaimsPrincipal principal, string version)
    {
        lock (_sync)
        {
            var session = RequireConnected(principal, version);
            if (session.Location is null) { throw new ConnectionException(new("location-required", "Confirm your location before reading calendar data.")); }
            if (session.Phase == ConnectionPhase.Discovering) { throw new ConnectionException(new("busy", "A calendar read is already running.")); }
            session.Phase = ConnectionPhase.Discovering;
            session.Discovery = null;
            session.Problem = null;
            Track(session, ct => DiscoverAsync(session, session.Revision, ct));
        }
    }

    public void Cancel(ClaimsPrincipal principal, string version)
    {
        lock (_sync)
        {
            var session = Require(principal, version);
            session.Revision++;
            session.Operation?.Cancel();
            session.Discovery = null;
            session.Problem = new("cancelled", "The operation was cancelled.");
            if (session.Phase is ConnectionPhase.SigningIn or ConnectionPhase.AwaitingCompletion)
            {
                session.Account = null;
                session.Mailbox = null;
                session.Phase = ConnectionPhase.SignedOut;
            }
            else if (session.Phase == ConnectionPhase.Discovering) { session.Phase = ConnectionPhase.Connected; }
        }
    }

    public async Task SignOutAsync(ClaimsPrincipal principal, string version, CancellationToken cancellationToken = default)
    {
        lock (_sync) { Require(principal, version); Invalidate(); _clearing = true; }
        // Invalidation is immediate, even when MSAL is still unwinding browser cancellation.
        await _work.WaitAsync(CancellationToken.None);
        try { if (identity.Unavailable is null) { await identity.ClearAsync(); } }
        finally
        {
            lock (_sync) { _clearing = false; }
            _work.Release();
        }
    }

    private async Task SignInAsync(Session session, bool interactive, CancellationToken ct)
    {
        var account = await identity.AuthenticateAsync(interactive, ct);
        var mailbox = await calendar.VerifyAsync(account, tokenCt => AcquireTokenAsync(session, account, tokenCt), ct);
        lock (_sync)
        {
            RequireCurrent(session);
            ct.ThrowIfCancellationRequested();
            session.Account = account;
            session.Mailbox = mailbox;
            session.CachedAccount = mailbox.Address;
            session.Phase = ConnectionPhase.AwaitingCompletion;
        }
    }

    private async Task DiscoverAsync(Session session, int revision, CancellationToken ct)
    {
        SignInObservation account;
        VerifiedMailbox mailbox;
        lock (_sync)
        {
            RequireCurrent(session);
            if (session.Revision != revision || session.Location is null || session.Account is null || session.Mailbox is null)
            {
                throw new OperationCanceledException(ct);
            }
            account = session.Account;
            mailbox = session.Mailbox;
        }
        var start = DateTimeOffset.UtcNow;
        var scope = new DiscoveryScope(mailbox.Identity, start, start.AddDays(90), start,
            new(500, 10, 500, TimeSpan.FromMinutes(2)));
        var result = await calendar.DiscoverAsync(scope, tokenCt => AcquireTokenAsync(session, account, tokenCt), ct);
        lock (_sync)
        {
            RequireCurrent(session);
            ct.ThrowIfCancellationRequested();
            if (session.Revision != revision) { return; }
            session.Discovery = new(start, scope.End, result.Complete, result.Events.Length,
                [.. result.Events.Where(e => e.IsOrganizer == true).Take(500)
                    .Select(e => new ConnectionMeeting(e.Id, e.Start, e.End, e.Organizer, e.Kind, e.Attendees.Length))], result.Holds);
            session.Phase = ConnectionPhase.Connected;
        }
    }

    private async Task<string> AcquireTokenAsync(Session session, SignInObservation account, CancellationToken ct)
    {
        lock (_sync) { RequireCurrent(session); ct.ThrowIfCancellationRequested(); }
        var token = await identity.AcquireReadTokenAsync(account, ct);
        lock (_sync) { RequireCurrent(session); ct.ThrowIfCancellationRequested(); }
        return token;
    }

    private void Track(Session session, Func<CancellationToken, Task> action)
    {
        session.Operation?.Cancel();
        var source = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        session.Operation = source;
        var revision = session.Revision;
        _tasks.RemoveAll(task => task.IsCompleted);
        _tasks.Add(Task.Run(async () =>
        {
            var entered = false;
            try
            {
                await _work.WaitAsync(source.Token);
                entered = true;
                lock (_sync) { RequireCurrent(session); source.Token.ThrowIfCancellationRequested(); }
                await action(source.Token);
            }
            catch (Exception ex)
            {
                // Observe every tracked task, and expose only typed, non-sensitive errors.
                lock (_sync)
                {
                    if (ReferenceEquals(_session, session) && session.Revision == revision && !_disposed)
                    {
                        session.Problem = Problem(ex);
                        session.Phase = session.Account is null ? ConnectionPhase.Failed : ConnectionPhase.Connected;
                        session.Discovery = null;
                    }
                }
            }
            finally
            {
                if (entered) { _work.Release(); }
                lock (_sync) { if (ReferenceEquals(session.Operation, source)) { session.Operation = null; } }
                source.Dispose();
            }
        }));
    }

    private Session RequireConnected(ClaimsPrincipal principal, string version)
    {
        var session = Require(principal, version);
        if (session.Account is null || session.Mailbox is null || session.Phase is not (ConnectionPhase.Connected or ConnectionPhase.Discovering)
            || principal.FindFirstValue("connection_verified") != "true")
        {
            throw new ConnectionException(new("sign-in-required", "Complete Microsoft 365 sign-in before continuing."));
        }
        return session;
    }

    private Session Require(ClaimsPrincipal principal, string version)
    {
        ThrowIfDisposed();
        var session = Find(principal);
        if (session is null || session.Version != version) { throw Stale(); }
        return session;
    }

    private Session? Find(ClaimsPrincipal principal) => _session is { } session && session.Expires > DateTimeOffset.UtcNow
        && principal.FindFirstValue(SessionClaim) == session.Id ? session : null;

    private void RequireCurrent(Session session)
    {
        if (_disposed || !ReferenceEquals(_session, session) || session.Expires <= DateTimeOffset.UtcNow) { throw Stale(); }
    }

    private ConnectionState State(Session session) => new(session.Version,
        identity.Unavailable is null ? session.Phase : ConnectionPhase.Disabled, session.CachedAccount,
        session.Mailbox, session.Location, TimeZoneInfo.Local.Id, session.Discovery, identity.Unavailable ?? session.Problem);

    private static ConnectionTicket Ticket(Session session) => new(session.Id, session.Version,
        session.Phase is ConnectionPhase.Connected or ConnectionPhase.Discovering ? session.Mailbox : null);

    private void Invalidate()
    {
        _session?.Operation?.Cancel();
        _session = null;
    }

    private static ConnectionException Stale() => new(new("stale-session", "This connection session expired or changed. Open Connect again."));
    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    private static bool IsRecoverable(Exception ex) => ex is MsalException or IOException or UnauthorizedAccessException
        or System.Security.Cryptography.CryptographicException or ConnectionException or InvalidOperationException;

    private static ConnectionProblem Problem(Exception ex) => ex switch
    {
        ConnectionException connection => connection.Problem,
        OperationCanceledException => new("cancelled", "The operation was cancelled or timed out. You can try again."),
        MsalUiRequiredException => new("interaction-required", "Your cached account needs browser sign-in. Choose Sign in with Microsoft 365."),
        MsalException => new("sign-in-failed", "Microsoft 365 sign-in did not complete. Try again or choose another account."),
        IOException or UnauthorizedAccessException or CryptographicException => new("protected-cache-unavailable", "Protected account storage is unavailable or in use by another app instance. Close the other instance and retry."),
        _ => new("connection-failed", "The connection could not be verified. Try signing in again.")
    };

    public async ValueTask DisposeAsync()
    {
        Task[] tasks;
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            Invalidate();
            tasks = [.. _tasks];
        }
        await Task.WhenAll(tasks);
        // DI disposes the identity and Graph clients after the service has drained its work.
        _work.Dispose();
    }

    private sealed class Session
    {
        public string Id { get; set; } = NewId();
        public string Version { get; set; } = NewId();
        public DateTimeOffset Expires { get; } = DateTimeOffset.UtcNow.AddHours(8);
        public ConnectionPhase Phase { get; set; } = ConnectionPhase.SignedOut;
        public string? CachedAccount { get; set; }
        public SignInObservation? Account { get; set; }
        public VerifiedMailbox? Mailbox { get; set; }
        public ConfirmedLocation? Location { get; set; }
        public ConnectionDiscovery? Discovery { get; set; }
        public ConnectionProblem? Problem { get; set; }
        public CancellationTokenSource? Operation { get; set; }
        public int Revision { get; set; }
    }
}
