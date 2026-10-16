using System.Security.Claims;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Host.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class LocalConnectionServiceTests
{
    [Fact]
    public async Task BootstrapStartPollCompleteRotatesTicketsAndRequiresVerifiedPrincipalAndLocation()
    {
        await using var h = new ConnectionHarness();
        var gate = new Gate<SignInObservation>();
        h.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        var bootstrap = h.Bootstrap();
        var bootstrapPrincipal = Principal(bootstrap);
        Assert.Equal(bootstrap, h.Service.Bootstrap(bootstrapPrincipal));
        Assert.Null(bootstrap.Mailbox);
        AssertProblem("sign-in-required", () => h.Service.StartDiscovery(bootstrapPrincipal, bootstrap.Version));
        var pending = h.Service.StartSignIn(bootstrapPrincipal, bootstrap.Version, true);
        var waitingPrincipal = Principal(pending);
        await gate.WaitForEntryAsync();
        Assert.False(h.Service.IsCurrent(bootstrapPrincipal));
        Assert.NotEqual(bootstrap.Version, pending.Version);
        Assert.Equal(ConnectionPhase.SigningIn, (await h.Service.GetStateAsync(waitingPrincipal)).Phase);
        AssertProblem("not-ready", () => h.Service.Complete(waitingPrincipal, pending.Version));
        AssertProblem("sign-in-required", () => h.Service.StartDiscovery(waitingPrincipal, pending.Version));
        gate.Result.SetResult(Account);
        var ready = await h.WaitForPhaseAsync(waitingPrincipal, ConnectionPhase.AwaitingCompletion);
        Assert.Equal(Mailbox, ready.Mailbox);
        AssertProblem("sign-in-required", () => h.Service.ConfirmLocation(waitingPrincipal, ready.Version, Location));
        AssertProblem("sign-in-required", () => h.Service.StartDiscovery(waitingPrincipal, ready.Version));
        Assert.Equal(0, h.Calendar.Discoveries);
        var complete = h.Service.Complete(waitingPrincipal, ready.Version);
        Assert.NotEqual(pending.Id, complete.Id);
        Assert.NotEqual(pending.Version, complete.Version);
        Assert.Equal(Mailbox, complete.Mailbox);
        Assert.False(h.Service.IsCurrent(waitingPrincipal));
        var stale = await h.Service.GetStateAsync(waitingPrincipal);
        Assert.Equal("", stale.Version);
        Assert.Null(stale.Mailbox);
        AssertProblem("stale-session", () => h.Service.Complete(waitingPrincipal, pending.Version));
        AssertProblem("sign-in-required", () => h.Service.StartDiscovery(Principal(complete, false), complete.Version));
        AssertProblem("location-required", () => h.Service.StartDiscovery(Principal(complete), complete.Version));
        AssertProblem("stale-session", () => h.Service.StartDiscovery(Principal(complete), pending.Version));
        Assert.Equal(0, h.Calendar.Discoveries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CachedHintIsNotIdentityAndRequestedAuthenticationPathIsPreserved(bool interactive)
    {
        await using var h = new ConnectionHarness();
        h.Identity.CachedAccount = "hint@example.test";
        var bootstrap = h.Bootstrap();
        var state = await h.Service.GetStateAsync(Principal(bootstrap));
        Assert.Equal("hint@example.test", state.CachedAccount);
        Assert.Null(state.Mailbox);
        AssertProblem("sign-in-required", () => h.Service.StartDiscovery(Principal(bootstrap), bootstrap.Version));
        var pending = h.Service.StartSignIn(Principal(bootstrap), state.Version, interactive);
        var ready = await h.WaitForPhaseAsync(Principal(pending), ConnectionPhase.AwaitingCompletion);
        Assert.Equal(new[] { interactive }, h.Identity.Modes);
        Assert.Equal(Mailbox.Address, ready.CachedAccount);
        Assert.Equal(Mailbox, ready.Mailbox);
    }

    [Fact]
    public async Task MissingCacheContinuationFailsWithoutGraphAndInteractiveRetryRecovers()
    {
        await using var h = new ConnectionHarness();
        h.Identity.Authenticate = (interactive, _) => interactive ? Task.FromResult(Account)
            : Task.FromException<SignInObservation>(new MsalUiRequiredException("no-cache", "synthetic private diagnostic"));
        var bootstrap = h.Bootstrap();
        Assert.Null((await h.Service.GetStateAsync(Principal(bootstrap))).CachedAccount);
        var pending = h.Service.StartSignIn(Principal(bootstrap), bootstrap.Version, false);
        var failed = await h.WaitForPhaseAsync(Principal(pending), ConnectionPhase.Failed);
        Assert.Equal("interaction-required", failed.Problem?.Code);
        Assert.DoesNotContain("private diagnostic", failed.Problem!.Message);
        Assert.Null(failed.Mailbox);
        Assert.Equal(0, h.Calendar.Verifications);
        var retry = h.Service.StartSignIn(Principal(pending), failed.Version, true);
        await h.WaitForPhaseAsync(Principal(retry), ConnectionPhase.AwaitingCompletion);
        Assert.NotNull(h.Service.Complete(Principal(retry), retry.Version).Mailbox);
        Assert.Collection(h.Identity.Modes, mode => Assert.False(mode), mode => Assert.True(mode));
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("account")]
    [InlineData("identity-mismatch")]
    [InlineData("calendar-mismatch")]
    public async Task IdentityOrCalendarBoundaryRejectionNeverAuthorizesReads(string rejection)
    {
        await using var h = new ConnectionHarness();
        // The identity adapter owns tenant/account validation, and Graph owns mailbox/owner
        // validation. These faults exercise service behavior when either boundary rejects.
        if (rejection is "tenant" or "account")
        {
            h.Identity.Authenticate = (_, _) => Task.FromException<SignInObservation>(new InvalidOperationException("Unexpected " + rejection));
        }
        else
        {
            h.Calendar.Verify = (_, _) => Task.FromException<VerifiedMailbox>(new ConnectionException(new(rejection, "Rejected synthetic identity")));
        }
        var bootstrap = h.Bootstrap();
        var pending = h.Service.StartSignIn(Principal(bootstrap), bootstrap.Version, true);
        var failed = await h.WaitForPhaseAsync(Principal(pending), ConnectionPhase.Failed);
        Assert.Equal(rejection is "tenant" or "account" ? "connection-failed" : rejection, failed.Problem?.Code);
        Assert.Null(failed.Mailbox);
        Assert.Null(failed.Location);
        Assert.Null(failed.Discovery);
        AssertProblem("not-ready", () => h.Service.Complete(Principal(pending), failed.Version));
        AssertProblem("sign-in-required", () => h.Service.StartDiscovery(Principal(pending), failed.Version));
        Assert.Equal(0, h.Calendar.Discoveries);
        Assert.Equal(rejection is "tenant" or "account" ? 0 : 1, h.Calendar.Verifications);
    }

    [Theory]
    [InlineData("ON", "Toronto", "America/Toronto")]
    [InlineData("bc", "Vancouver", "America/Vancouver")]
    [InlineData("BC", "Vancouver", "America/Regina")]
    [InlineData("BC", " ", "America/Vancouver")]
    [InlineData("BC", "City\nInjected", "America/Vancouver")]
    [InlineData("BC", "City", "america/vancouver")]
    public async Task InvalidLocationDoesNotEnableDiscovery(string province, string community, string zone)
    {
        await using var h = new ConnectionHarness();
        var ticket = await h.ConnectAsync();
        AssertProblem("invalid-location", () => h.Service.ConfirmLocation(Principal(ticket), ticket.Version, new(province, community, zone)));
        AssertProblem("invalid-location", () => h.Service.ConfirmLocation(Principal(ticket), ticket.Version, Location with { Community = new string('x', 101) }));
        Assert.Null((await h.Service.GetStateAsync(Principal(ticket))).Location);
        Assert.Equal(0, h.Calendar.Discoveries);
    }

    [Theory]
    [InlineData("BC", "America/Dawson_Creek")]
    [InlineData("BC", "America/Fort_Nelson")]
    [InlineData("BC", "America/Creston")]
    [InlineData("AB", "America/Edmonton")]
    [InlineData("SK", "America/Swift_Current")]
    [InlineData("MB", "America/Winnipeg")]
    public async Task ExplicitProvinceZoneAndTrimmedCommunityArePreserved(string province, string zone)
    {
        await using var h = new ConnectionHarness();
        var ticket = await h.ConnectAsync();
        h.Service.ConfirmLocation(Principal(ticket), ticket.Version, new(province, "  Community  ", zone));
        Assert.Equal(new(province, "Community", zone), (await h.Service.GetStateAsync(Principal(ticket))).Location);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoveryIsIdentityBoundBoundedAndPreservesCompletenessAndHolds(bool complete)
    {
        await using var h = new ConnectionHarness();
        var gate = new Gate<DiscoveryResult>();
        h.Calendar.Discover = (scope, ct) => gate.RunAsync(ct);
        var ticket = await h.ConnectAsync();
        var principal = Principal(ticket);
        h.Service.ConfirmLocation(principal, ticket.Version, Location);
        var before = DateTimeOffset.UtcNow;
        h.Service.StartDiscovery(principal, ticket.Version);
        await gate.WaitForEntryAsync();
        Assert.Equal(ConnectionPhase.Discovering, (await h.Service.GetStateAsync(principal)).Phase);
        AssertProblem("busy", () => h.Service.StartDiscovery(principal, ticket.Version));
        var scope = Assert.IsType<DiscoveryScope>(h.Calendar.Scope);
        Assert.Equal(Mailbox.Identity, scope.Identity);
        Assert.InRange(scope.Start, before, DateTimeOffset.UtcNow);
        Assert.Equal(TimeSpan.FromDays(90), scope.End - scope.Start);
        Assert.Equal(TimeSpan.Zero, scope.Start.Offset);
        Assert.Equal(scope.Start, scope.ExecutionCutoff);
        Assert.Equal(new DiscoveryBudget(500, 10, 500, TimeSpan.FromMinutes(2)), scope.Budget);
        Assert.Equal("synthetic-token", await h.Calendar.DiscoveryToken!(CancellationToken.None));
        Assert.Same(Account, Assert.Single(h.Identity.TokenAccounts));
        var organized = Snapshot(scope.Identity, "organized", true);
        gate.Result.SetResult(new(scope, complete, [organized, Snapshot(scope.Identity, "invited", false), Snapshot(scope.Identity, "unknown", null)],
            [], complete ? [] : ["page-budget"], 1, 1));
        var result = (await h.WaitForPhaseAsync(principal, ConnectionPhase.Connected)).Discovery;
        Assert.NotNull(result);
        Assert.Equal(complete, result.Complete);
        Assert.Equal(3, result.ObservedEvents);
        var meeting = Assert.Single(result.OrganizerMeetings);
        Assert.Equal("organized", meeting.Id);
        Assert.Equal(organized.Start, meeting.Start);
        Assert.Equal(organized.End, meeting.End);
        Assert.Equal(organized.Organizer, meeting.Organizer);
        Assert.Equal(EventKind.SingleInstance, meeting.Kind);
        Assert.Equal(1, meeting.AttendeeCount);
        if (complete) { Assert.Empty(result.Holds); }
        else { Assert.Equal("page-budget", Assert.Single(result.Holds)); }
    }

    [Fact]
    public async Task LocationChangeCancelsInflightReadAndDiscardsOldGeneration()
    {
        await using var h = new ConnectionHarness();
        var gate = new Gate<DiscoveryResult>();
        h.Calendar.Discover = (_, ct) => gate.RunAsync(ct);
        var ticket = await h.ConnectAsync();
        var principal = Principal(ticket);
        h.Service.ConfirmLocation(principal, ticket.Version, Location);
        h.Service.StartDiscovery(principal, ticket.Version);
        await gate.WaitForEntryAsync();
        var replacement = new ConfirmedLocation("AB", "Calgary", "America/Edmonton");
        h.Service.ConfirmLocation(principal, ticket.Version, replacement);
        await gate.WaitForCancellationAsync();
        h.Calendar.Discover = (scope, _) => Task.FromResult(new DiscoveryResult(scope, true, [], [], [], 1, 0));
        h.Service.StartDiscovery(principal, ticket.Version);
        var state = await h.WaitForPhaseAsync(principal, ConnectionPhase.Connected);
        Assert.Equal(replacement, state.Location);
        Assert.True(state.Discovery?.Complete);
        Assert.Null(state.Problem);
        Assert.Equal(2, h.Calendar.Discoveries);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelSigninCancelsWorkAndPreventsCompletionThenRetrySucceeds(bool inlineCompletion)
    {
        await using var h = new ConnectionHarness();
        // Some providers complete cancellation inline. This deliberately uses synchronous
        // continuations so Cancel's state transition must survive reentrant worker cleanup.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<SignInObservation>();
        CancellationTokenRegistration registration = default;
        h.Identity.Authenticate = (_, ct) =>
        {
            registration = ct.Register(() =>
            {
                if (inlineCompletion) { cancelled.TrySetCanceled(ct); }
            });
            entered.SetResult();
            return cancelled.Task;
        };
        var bootstrap = h.Bootstrap();
        var pending = h.Service.StartSignIn(Principal(bootstrap), bootstrap.Version, true);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var cancellationRegistration = registration;
        h.Service.Cancel(Principal(pending), pending.Version);
        if (!inlineCompletion) { cancelled.TrySetCanceled(); }
        Assert.True(cancelled.Task.IsCanceled);
        var state = await h.Service.GetStateAsync(Principal(pending));
        Assert.Equal(ConnectionPhase.SignedOut, state.Phase);
        Assert.Equal("cancelled", state.Problem?.Code);
        Assert.Null(state.Mailbox);
        AssertProblem("not-ready", () => h.Service.Complete(Principal(pending), pending.Version));
        Assert.Equal(0, h.Calendar.Verifications);
        h.Identity.Authenticate = (_, _) => Task.FromResult(Account);
        var retry = h.Service.StartSignIn(Principal(pending), pending.Version, true);
        await h.WaitForPhaseAsync(Principal(retry), ConnectionPhase.AwaitingCompletion);
        Assert.NotNull(h.Service.Complete(Principal(retry), retry.Version).Mailbox);
    }

    [Fact]
    public async Task AccountSwitchInvalidatesOldPrincipalDataAndTokenDelegates()
    {
        await using var h = new ConnectionHarness();
        var ticket = await h.ConnectAsync();
        var old = Principal(ticket);
        var oldToken = h.Calendar.VerificationToken!;
        h.Service.ConfirmLocation(old, ticket.Version, Location);
        var gate = new Gate<SignInObservation>();
        h.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        var switched = h.Service.StartSignIn(old, ticket.Version, true);
        await gate.WaitForEntryAsync();
        Assert.False(h.Service.IsCurrent(old));
        var stale = await h.Service.GetStateAsync(old);
        Assert.Equal("", stale.Version);
        Assert.Null(stale.Mailbox);
        Assert.Null(stale.Location);
        Assert.Null(stale.Discovery);
        var ex = await Assert.ThrowsAsync<ConnectionException>(() => oldToken(CancellationToken.None));
        Assert.Equal("stale-session", ex.Problem.Code);
        Assert.Empty(h.Identity.TokenAccounts);
        var next = Account with { MailboxObjectId = "mailbox-b", HomeAccountId = "home-b" };
        var nextMailbox = Mailbox with { Identity = new("tenant-a", "mailbox-b", "calendar-b"), Address = "second@example.test" };
        h.Calendar.Verify = (account, _) => { Assert.Same(next, account); return Task.FromResult(nextMailbox); };
        gate.Result.SetResult(next);
        await h.WaitForPhaseAsync(Principal(switched), ConnectionPhase.AwaitingCompletion);
        var complete = h.Service.Complete(Principal(switched), switched.Version);
        Assert.Equal(nextMailbox, complete.Mailbox);
        await h.Calendar.VerificationToken!(CancellationToken.None);
        Assert.Same(next, Assert.Single(h.Identity.TokenAccounts));
    }

    [Fact]
    public async Task TokenFinishingAfterSignoutCannotEscapeAndCacheClearBlocksNewBootstrap()
    {
        await using var h = new ConnectionHarness();
        var ticket = await h.ConnectAsync();
        var tokenGate = new Gate<string>();
        h.Identity.Acquire = (_, ct) => tokenGate.RunAsync(ct);
        var tokenTask = h.Calendar.VerificationToken!(CancellationToken.None);
        await tokenGate.WaitForEntryAsync();
        var clearGate = new Gate<bool>();
        h.Identity.Clear = async () => { await clearGate.RunAsync(CancellationToken.None); };
        var signout = h.Service.SignOutAsync(Principal(ticket), ticket.Version);
        await clearGate.WaitForEntryAsync();
        Assert.False(signout.IsCompleted);
        Assert.False(h.Service.IsCurrent(Principal(ticket)));
        AssertProblem("busy", () => h.Bootstrap());
        tokenGate.Result.SetResult("must-not-escape");
        Assert.Equal("stale-session", (await Assert.ThrowsAsync<ConnectionException>(() => tokenTask)).Problem.Code);
        clearGate.Result.SetResult(true);
        await signout;
        Assert.Equal(1, h.Identity.Clears);
        Assert.NotEqual(ticket.Id, h.Bootstrap().Id);
    }

    [Fact]
    public async Task SignoutCancelsDiscoveryInvalidatesEveryOldPrincipalAndClearsCache()
    {
        await using var h = new ConnectionHarness();
        h.Identity.CachedAccount = Mailbox.Address;
        var ticket = await h.ConnectAsync();
        var gate = new Gate<DiscoveryResult>();
        h.Calendar.Discover = (_, ct) => gate.RunAsync(ct);
        h.Service.ConfirmLocation(Principal(ticket), ticket.Version, Location);
        h.Service.StartDiscovery(Principal(ticket), ticket.Version);
        await gate.WaitForEntryAsync();
        var signout = h.Service.SignOutAsync(Principal(ticket), ticket.Version);
        await gate.WaitForCancellationAsync();
        Assert.False(h.Service.IsCurrent(Principal(ticket)));
        await signout;
        Assert.Equal(1, h.Identity.Clears);
        Assert.Null(h.Identity.CachedAccount);
        Assert.Equal("", (await h.Service.GetStateAsync(Principal(ticket))).Version);
        AssertProblem("stale-session", () => h.Service.StartDiscovery(Principal(ticket), ticket.Version));
    }

    [Fact]
    public async Task CacheClearFailureStillInvalidatesSessionAndAllowsCleanRecovery()
    {
        await using var h = new ConnectionHarness();
        var ticket = await h.ConnectAsync();
        h.Identity.Clear = () => Task.FromException(new IOException("synthetic cache lock"));
        await Assert.ThrowsAsync<IOException>(() => h.Service.SignOutAsync(Principal(ticket), ticket.Version));
        Assert.False(h.Service.IsCurrent(Principal(ticket)));
        h.Identity.Clear = () => Task.CompletedTask;
        var recovered = await h.ConnectAsync();
        Assert.NotEqual(ticket.Id, recovered.Id);
        Assert.Equal(Mailbox, recovered.Mailbox);
    }

    [Fact]
    public async Task DiscoveryFailurePreservesVerifiedIdentityAndRetryClearsProblem()
    {
        await using var h = new ConnectionHarness();
        var gate = new Gate<DiscoveryResult>();
        h.Calendar.Discover = (_, ct) => gate.RunAsync(ct);
        var ticket = await h.ConnectAsync();
        h.Service.ConfirmLocation(Principal(ticket), ticket.Version, Location);
        h.Service.StartDiscovery(Principal(ticket), ticket.Version);
        await gate.WaitForEntryAsync();
        gate.Result.SetException(new InvalidOperationException("secret provider diagnostic"));
        var failed = await h.WaitForPhaseAsync(Principal(ticket), ConnectionPhase.Connected);
        Assert.Equal("connection-failed", failed.Problem?.Code);
        Assert.DoesNotContain("secret", failed.Problem!.Message);
        Assert.Null(failed.Discovery);
        Assert.Equal(Mailbox, failed.Mailbox);
        Assert.Equal(Location, failed.Location);
        h.Calendar.Discover = (scope, _) => Task.FromResult(new DiscoveryResult(scope, true, [], [], [], 1, 0));
        h.Service.StartDiscovery(Principal(ticket), ticket.Version);
        var recovered = await h.WaitForPhaseAsync(Principal(ticket), ConnectionPhase.Connected);
        Assert.Null(recovered.Problem);
        Assert.NotNull(recovered.Discovery);
    }

    [Fact]
    public async Task CancelDiscoveryPreservesConnectionAndAllowsAnotherRead()
    {
        await using var h = new ConnectionHarness();
        var gate = new Gate<DiscoveryResult>();
        h.Calendar.Discover = (_, ct) => gate.RunAsync(ct);
        var ticket = await h.ConnectAsync();
        var principal = Principal(ticket);
        h.Service.ConfirmLocation(principal, ticket.Version, Location);
        h.Service.StartDiscovery(principal, ticket.Version);
        await gate.WaitForEntryAsync();
        h.Service.Cancel(principal, ticket.Version);
        await gate.WaitForCancellationAsync();
        var state = await h.Service.GetStateAsync(principal);
        Assert.Equal(ConnectionPhase.Connected, state.Phase);
        Assert.Equal(Mailbox, state.Mailbox);
        Assert.Equal(Location, state.Location);
        Assert.Null(state.Discovery);
        Assert.Equal("cancelled", state.Problem?.Code);
        h.Calendar.Discover = (scope, _) => Task.FromResult(new DiscoveryResult(scope, true, [], [], [], 1, 0));
        h.Service.StartDiscovery(principal, ticket.Version);
        var recovered = await h.WaitForPhaseAsync(principal, ConnectionPhase.Connected);
        Assert.NotNull(recovered.Discovery);
        Assert.Null(recovered.Problem);
    }

    [Fact]
    public async Task LateNoncooperativeDiscoveryCannotPublishAfterLocationChange()
    {
        await using var h = new ConnectionHarness();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<DiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Calendar.Discover = (_, _) => { entered.SetResult(); return late.Task; };
        var ticket = await h.ConnectAsync();
        var principal = Principal(ticket);
        h.Service.ConfirmLocation(principal, ticket.Version, Location);
        h.Service.StartDiscovery(principal, ticket.Version);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var oldScope = h.Calendar.Scope!;
        var replacement = new ConfirmedLocation("MB", "Winnipeg", "America/Winnipeg");
        try
        {
            h.Service.ConfirmLocation(principal, ticket.Version, replacement);
            h.Calendar.Discover = (scope, _) => Task.FromResult(new DiscoveryResult(scope, true, [], [], [], 1, 0));
            h.Service.StartDiscovery(principal, ticket.Version);
        }
        finally
        {
            // Always release a deliberately noncooperative provider, even on assertion failure.
            late.TrySetResult(new(oldScope, false, [Snapshot(oldScope.Identity, "obsolete", true)], [], ["obsolete-hold"], 1, 0));
        }
        var state = await h.WaitForPhaseAsync(principal, ConnectionPhase.Connected);
        Assert.Equal(replacement, state.Location);
        Assert.NotNull(state.Discovery);
        Assert.True(state.Discovery.Complete);
        Assert.Empty(state.Discovery.OrganizerMeetings);
        Assert.Empty(state.Discovery.Holds);
        Assert.Equal(2, h.Calendar.Discoveries);
    }

    [Fact]
    public async Task BoundTokenHonorsCancellationWithoutCallingIdentityClient()
    {
        await using var h = new ConnectionHarness();
        await h.ConnectAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Calendar.VerificationToken!(cancellation.Token));
        Assert.Empty(h.Identity.TokenAccounts);
    }

    [Fact]
    public async Task UnavailableIdentityIsDisabledWithoutCacheOrGraphAccess()
    {
        await using var h = new ConnectionHarness();
        h.Identity.Unavailable = new("configuration-required", "Synthetic disabled client");
        var bootstrap = h.Bootstrap();
        var state = await h.Service.GetStateAsync(Principal(bootstrap));
        Assert.Equal(ConnectionPhase.Disabled, state.Phase);
        Assert.Equal(h.Identity.Unavailable, state.Problem);
        AssertProblem("configuration-required", () => h.Service.StartSignIn(Principal(bootstrap), bootstrap.Version, true));
        await h.Service.SignOutAsync(Principal(bootstrap), bootstrap.Version);
        Assert.Equal(0, h.Identity.CacheReads);
        Assert.Equal(0, h.Identity.Clears);
        Assert.Empty(h.Identity.Modes);
        Assert.Equal(0, h.Calendar.Verifications);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("not-a-tenant", "11111111-1111-1111-1111-111111111111")]
    [InlineData("11111111-1111-1111-1111-111111111111", "not-a-client")]
    public void RealAdapterFailsClosedWhenConfigurationIsUnsupported(string tenantId, string clientId)
    {
        using var identity = new LocalIdentityClient(Options.Create(new LocalConnectionOptions
        {
            TenantId = tenantId,
            ClientId = clientId
        }));
        Assert.Equal("configuration-required", identity.Unavailable?.Code);
        Assert.Collection(TenantSignIn.Scopes(false), scope => Assert.Equal("Calendars.Read", scope), scope => Assert.Equal("User.Read", scope));
    }

    [Fact]
    public async Task RealAdapterWithValidConfigurationEnablesSessionOnlyCacheOnNonWindows()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var path = Path.Combine(Path.GetTempPath(), "identity-session-only-" + Guid.NewGuid());
        using (var identity = new LocalIdentityClient(Options.Create(new LocalConnectionOptions
        {
            TenantId = Guid.NewGuid().ToString(),
            ClientId = Guid.NewGuid().ToString(),
            CacheDirectory = path
        })))
        {
            Assert.Null(identity.Unavailable);
            Assert.Null(await identity.GetCachedAccountAsync(CancellationToken.None));
            await identity.ClearAsync();
            Assert.Null(await identity.GetCachedAccountAsync(CancellationToken.None));
        }
        Assert.False(Directory.Exists(path));
    }

    private static SourceSnapshot Snapshot(CalendarIdentity identity, string id, bool? organizer) => new(identity, id, "v1", "etag",
        new DateTimeOffset(2026, 1, 2, 10, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 2, 11, 0, 0, TimeSpan.Zero),
        new("2026-01-02T10:00:00", "UTC"), new("2026-01-02T11:00:00", "UTC"), organizer, "owner@example.test",
        [new("attendee@example.test", AttendeeKind.Internal)], false, false, EventKind.SingleInstance, null,
        new("UTC", "UTC", "UTC"), []);

    private static void AssertProblem(string code, Action action) => Assert.Equal(code, Assert.Throws<ConnectionException>(action).Problem.Code);
}
