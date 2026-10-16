using System.Security.Claims;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Host.Authentication;

namespace CalendarRemediation.Host.Tests.Authentication;

internal sealed class ConnectionHarness : IAsyncDisposable
{
    public static readonly SignInObservation Account = new("tenant-a", "client-a", "mailbox-a", "home-a",
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "synthetic", "synthetic", ["Calendars.Read", "User.Read"]);
    public static readonly VerifiedMailbox Mailbox = new(new("tenant-a", "mailbox-a", "calendar-a"), "owner@example.test", "Owner");
    public static readonly ConfirmedLocation Location = new("BC", "Vancouver", "America/Vancouver");
    public FakeIdentity Identity { get; } = new();
    public FakeCalendar Calendar { get; } = new();
    public LocalConnectionService Service { get; }

    public ConnectionHarness() => Service = new(Identity, Calendar);

    public static ClaimsPrincipal Principal(ConnectionTicket ticket, bool? verified = null) => new(new ClaimsIdentity(
        [new(LocalConnectionService.SessionClaim, ticket.Id),
         new(ClaimTypes.NameIdentifier, ticket.Mailbox?.Identity.MailboxId ?? ticket.Id),
         new("connection_verified", (verified ?? ticket.Mailbox is not null) ? "true" : "false")], ConnectionEndpoints.Scheme));

    public ConnectionTicket Bootstrap() => Service.Bootstrap(new());

    public async Task<ConnectionTicket> ConnectAsync(bool interactive = true)
    {
        var bootstrap = Bootstrap();
        var pending = Service.StartSignIn(Principal(bootstrap), bootstrap.Version, interactive);
        await WaitForPhaseAsync(Principal(pending), ConnectionPhase.AwaitingCompletion);
        return Service.Complete(Principal(pending), pending.Version);
    }

    public async Task<ConnectionState> WaitForPhaseAsync(ClaimsPrincipal principal, ConnectionPhase phase)
    {
        // The public contract is polling. Yield to the tracked worker, with a deadline only
        // to diagnose a stuck operation. Ordering is controlled by gates, never sleeps.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var state = await Service.GetStateAsync(principal, deadline.Token);
            if (state.Phase == phase) { return state; }
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync() => Service.DisposeAsync();

    internal sealed class Gate<T>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<T> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> RunAsync(CancellationToken ct)
        {
            Entered.TrySetResult();
            try { return await Result.Task.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }
        public Task WaitForEntryAsync() => Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        public Task WaitForCancellationAsync() => Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal sealed class FakeIdentity : ILocalIdentityClient
    {
        public ConnectionProblem? Unavailable { get; set; }
        public string? CachedAccount { get; set; }
        public int CacheReads { get; private set; }
        public int Clears { get; private set; }
        public List<bool> Modes { get; } = [];
        public List<SignInObservation> TokenAccounts { get; } = [];
        public Func<bool, CancellationToken, Task<SignInObservation>> Authenticate { get; set; } = (_, _) => Task.FromResult(Account);
        public Func<SignInObservation, CancellationToken, Task<string>> Acquire { get; set; } = (_, _) => Task.FromResult("synthetic-token");
        public Func<Task> Clear { get; set; } = () => Task.CompletedTask;
        public Task<string?> GetCachedAccountAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CacheReads++;
            return Task.FromResult(CachedAccount);
        }
        public Task<SignInObservation> AuthenticateAsync(bool interactive, CancellationToken cancellationToken)
        {
            Modes.Add(interactive);
            return Authenticate(interactive, cancellationToken);
        }
        public Task<string> AcquireReadTokenAsync(SignInObservation account, CancellationToken cancellationToken)
        {
            TokenAccounts.Add(account);
            return Acquire(account, cancellationToken);
        }
        public async Task ClearAsync()
        {
            Clears++;
            await Clear();
            CachedAccount = null;
        }
        public void Dispose() { }
    }

    internal sealed class FakeCalendar : ILocalCalendarClient
    {
        public int Verifications { get; private set; }
        public int Discoveries { get; private set; }
        public Func<CancellationToken, Task<string>>? VerificationToken { get; private set; }
        public Func<CancellationToken, Task<string>>? DiscoveryToken { get; private set; }
        public DiscoveryScope? Scope { get; private set; }
        public Func<SignInObservation, CancellationToken, Task<VerifiedMailbox>> Verify { get; set; } = (_, _) => Task.FromResult(Mailbox);
        public Func<DiscoveryScope, CancellationToken, Task<DiscoveryResult>> Discover { get; set; } = (scope, _) =>
            Task.FromResult(new DiscoveryResult(scope, true, [], [], [], 1, 0));
        public Task<VerifiedMailbox> VerifyAsync(SignInObservation account, Func<CancellationToken, Task<string>> token, CancellationToken cancellationToken)
        {
            Verifications++;
            VerificationToken = token;
            return Verify(account, cancellationToken);
        }
        public Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, Func<CancellationToken, Task<string>> token, CancellationToken cancellationToken)
        {
            Discoveries++;
            Scope = scope;
            DiscoveryToken = token;
            return Discover(scope, cancellationToken);
        }
    }
}
