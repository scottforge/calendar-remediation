using System.Security.Claims;
using System.Security.Cryptography;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;
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

    public ConnectionTicket StartSignIn(ClaimsPrincipal principal, string version)
    {
        lock (_sync)
        {
            Require(principal, version);
            return BeginSignIn(null);
        }
    }

    public ConnectionTicket StartBrowserSignIn(ClaimsPrincipal principal, string version, Uri returnUri)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            if (Find(principal) is { } existing)
            {
                Require(principal, version);
                if (existing.Phase is not (ConnectionPhase.SignedOut or ConnectionPhase.Failed)) { throw Stale(); }
            }
            else if (!string.IsNullOrEmpty(version) || principal.FindFirstValue(SessionClaim) is not null)
            {
                throw Stale();
            }
            return BeginSignIn(returnUri);
        }
    }

    private ConnectionTicket BeginSignIn(Uri? returnUri)
    {
        if (_clearing) { throw new ConnectionException(new("busy", "Account sign-out is still finishing. Try again shortly.")); }
        if (identity.Unavailable is { } unavailable) { throw new ConnectionException(unavailable); }
        Invalidate();
        var session = new Session { Phase = ConnectionPhase.SigningIn };
        _session = session;
        Track(session, ct => SignInAsync(session, returnUri, ct));
        return Ticket(session);
    }

    public async Task<Uri?> WaitForBrowserSignInAsync(ConnectionTicket ticket, CancellationToken cancellationToken = default)
    {
        Session session;
        CancellationToken operationToken;
        lock (_sync)
        {
            session = _session ?? throw Stale();
            RequireCurrent(session);
            if (session.Id != ticket.Id || session.Version != ticket.Version) { throw Stale(); }
            operationToken = session.Operation?.Token ?? CancellationToken.None;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationToken);
        try
        {
            var uri = await session.AuthorizationUri.Task.WaitAsync(TimeSpan.FromSeconds(15), linked.Token);
            lock (_sync)
            {
                RequireCurrent(session);
                if (session.Id != ticket.Id || session.Version != ticket.Version) { throw Stale(); }
                cancellationToken.ThrowIfCancellationRequested();
                return !operationToken.IsCancellationRequested
                    && session.Phase is ConnectionPhase.SigningIn or ConnectionPhase.AwaitingCompletion ? uri : null;
            }
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            lock (_sync)
            {
                if (ReferenceEquals(_session, session) && session.Phase == ConnectionPhase.SigningIn)
                {
                    session.Revision++;
                    session.Problem = Problem(new OperationCanceledException());
                    session.Phase = ConnectionPhase.Failed;
                    session.Operation?.Cancel();
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync) { RequireCurrent(session); }
            return null;
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
            location = ValidateLocation(location);
            session.Operation?.Cancel();
            session.Discovery = null;
            session.DiscoveryResult = null;
            session.Preview = null;
            session.Problem = null;
            session.Phase = ConnectionPhase.Connected;
            // A completed old discovery cannot publish into this location generation.
            session.Revision++;
            session.Location = location;
        }
    }

    private static ConfirmedLocation ValidateLocation(ConfirmedLocation location)
    {
        if (!LocationZones.TryGetValue(location.Province, out var zones) || !zones.Contains(location.TimeZoneId, StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(location.Community) || location.Community.Length > 100 || location.Community.Any(char.IsControl))
        {
            throw new ConnectionException(new("invalid-location", "Choose BC, AB, SK or MB, enter a community, and confirm one of its listed time zones."));
        }

        return location with { Community = location.Community.Trim() };
    }

    public void StartDiscovery(ClaimsPrincipal principal, string version)
    {
        lock (_sync)
        {
            var session = RequireConnected(principal, version);
            session.Preview = null;
            if (session.Location is null) { throw new ConnectionException(new("location-required", "Confirm your location before reading calendar data.")); }
            if (session.Phase == ConnectionPhase.Discovering) { throw new ConnectionException(new("busy", "A calendar read is already running.")); }
            session.Phase = ConnectionPhase.Discovering;
            session.Discovery = null;
            session.DiscoveryResult = null;
            session.Problem = null;
            Track(session, ct => DiscoverAsync(session, session.Revision, ct));
        }
    }

    internal void ClearPreview(ClaimsPrincipal principal, string version)
    {
        lock (_sync) { Require(principal, version).Preview = null; }
    }

    public void PreviewMeeting(ClaimsPrincipal principal, string version, string eventId,
        DateTime intendedLocalStart, string timeZoneId, bool preserveInstant)
    {
        lock (_sync)
        {
            var session = Require(principal, version);
            session.Preview = null;
            RequireConnected(principal, version);
            if (session.Phase != ConnectionPhase.Connected || session.DiscoveryResult is not { Complete: true } discovery
                || session.DiscoveryRevision != session.Revision)
            {
                throw new ConnectionException(new("discovery-required", "Complete a current calendar read before previewing a meeting."));
            }
            if (session.Location is not { } location || !string.Equals(location.TimeZoneId, timeZoneId, StringComparison.Ordinal))
            {
                throw new ConnectionException(new("invalid-zone", "Use the time zone from your confirmed location."));
            }
            var matches = discovery.Events.Where(e => string.Equals(e.Id, eventId, StringComparison.Ordinal)).Take(2).ToArray();
            var mailbox = session.Mailbox!;
            if (string.IsNullOrWhiteSpace(eventId) || matches.Length != 1 || matches[0].IsOrganizer != true
                || matches[0].Identity != mailbox.Identity || discovery.Scope.Identity != mailbox.Identity
                || matches[0].Start is not { } start || start < discovery.Scope.Start || start >= discovery.Scope.End)
            {
                throw new ConnectionException(new("invalid-meeting", "Choose one organizer-owned meeting from the current calendar read."));
            }
            TimeZoneInfo zone;
            try { zone = TimeZoneInfo.FindSystemTimeZoneById(location.TimeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new ConnectionException(new("zone-unavailable", "The confirmed time zone is unavailable on this machine. Choose another location time zone."));
            }
            session.Preview = ReadOnlyMeetingPreview.Calculate(matches[0], intendedLocalStart, zone, preserveInstant, DateTimeOffset.UtcNow);
            session.Problem = null;
        }
    }

    public void Cancel(ClaimsPrincipal principal, string version)
    {
        lock (_sync)
        {
            var session = Require(principal, version);
            session.Revision++;
            session.Discovery = null;
            session.DiscoveryResult = null;
            session.Preview = null;
            session.Problem = new("cancelled", "The operation was cancelled.");
            if (session.Phase is ConnectionPhase.SigningIn or ConnectionPhase.AwaitingCompletion)
            {
                session.Account = null;
                session.Mailbox = null;
                session.Phase = ConnectionPhase.SignedOut;
            }
            else if (session.Phase == ConnectionPhase.Discovering) { session.Phase = ConnectionPhase.Connected; }
            // Cancellation can synchronously resume a waiter and re-enter this lock.
            session.Operation?.Cancel();
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

    private async Task SignInAsync(Session session, Uri? returnUri, CancellationToken ct)
    {
        var browser = returnUri is null ? null : new BrowserSignInContext(returnUri, uri =>
        {
            lock (_sync)
            {
                RequireCurrent(session);
                ct.ThrowIfCancellationRequested();
                if (session.Phase != ConnectionPhase.SigningIn) { throw Stale(); }
                session.AuthorizationUri.TrySetResult(uri);
            }
            return Task.CompletedTask;
        });
        var account = await identity.AuthenticateAsync(browser is not null, browser, ct);
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
            session.DiscoveryResult = result;
            session.DiscoveryRevision = revision;
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
                        session.DiscoveryResult = null;
                        session.Preview = null;
                    }
                }
            }
            finally
            {
                session.AuthorizationUri.TrySetResult(null);
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
        session.Mailbox, session.Location, TimeZoneInfo.Local.Id, session.Discovery, identity.Unavailable ?? session.Problem, session.Preview);

    private static ConnectionTicket Ticket(Session session) => new(session.Id, session.Version,
        session.Phase is ConnectionPhase.Connected or ConnectionPhase.Discovering ? session.Mailbox : null);

    private void Invalidate()
    {
        var session = _session;
        _session = null;
        session?.Operation?.Cancel();
        session?.AuthorizationUri.TrySetResult(null);
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
        IOException or UnauthorizedAccessException or CryptographicException => new("account-cache-unavailable", "The account cache is unavailable or in use by another app instance. Close the other instance and retry."),
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
        public DiscoveryResult? DiscoveryResult { get; set; }
        public int DiscoveryRevision { get; set; }
        public MeetingTimePreview? Preview { get; set; }
        public ConnectionProblem? Problem { get; set; }
        public CancellationTokenSource? Operation { get; set; }
        public TaskCompletionSource<Uri?> AuthorizationUri { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Revision { get; set; }
    }
}
