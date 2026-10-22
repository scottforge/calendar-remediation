using CalendarRemediation.Host.Authentication;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class BrowserSignInServiceTests
{
    [Fact]
    public async Task AuthorizationHandoffPrecedesCompletionAndPreservesReturnUri()
    {
        await using var h = new ConnectionHarness();
        var gate = new Gate<SignInObservation>();
        h.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        var returnUri = new Uri("http://127.0.0.1:5011/connect");
        var ticket = h.Service.StartBrowserSignIn(new(), "", returnUri);

        Assert.Equal(AuthorizationUri, await h.Service.WaitForBrowserSignInAsync(ticket).WaitAsync(TimeSpan.FromSeconds(10)));
        await gate.WaitForEntryAsync();
        Assert.Equal(returnUri, Assert.Single(h.Identity.Browsers)!.ReturnUri);
        Assert.Equal(ConnectionPhase.SigningIn, (await h.Service.GetStateAsync(Principal(ticket))).Phase);
        Assert.Equal(0, h.Calendar.Verifications);
        gate.Result.SetResult(Account);
        await h.WaitForPhaseAsync(Principal(ticket), ConnectionPhase.AwaitingCompletion);
        Assert.Equal(AuthorizationUri, await h.Service.WaitForBrowserSignInAsync(ticket));
    }

    [Fact]
    public async Task ReplacementRejectsOldWaiterAndLateBrowserCallback()
    {
        await using var h = new ConnectionHarness();
        h.Identity.OpenBrowser = false;
        var gate = new Gate<SignInObservation>();
        h.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        var old = h.Service.StartBrowserSignIn(new(), "", ReturnUri);
        await gate.WaitForEntryAsync();
        var browser = Assert.Single(h.Identity.Browsers)!;
        var waiting = h.Service.WaitForBrowserSignInAsync(old);
        h.Identity.Authenticate = (_, _) => Task.FromResult(Account);

        var replacement = h.Service.StartSignIn(Principal(old), old.Version);

        await gate.WaitForCancellationAsync();
        Assert.Equal("stale-session", (await Assert.ThrowsAsync<ConnectionException>(() => waiting)).Problem.Code);
        Assert.Equal("stale-session", (await Assert.ThrowsAsync<ConnectionException>(() => browser.OpenBrowserAsync(AuthorizationUri))).Problem.Code);
        Assert.Equal("stale-session", (await Assert.ThrowsAsync<ConnectionException>(() => h.Service.WaitForBrowserSignInAsync(old))).Problem.Code);
        await h.WaitForPhaseAsync(Principal(replacement), ConnectionPhase.AwaitingCompletion);
        Assert.Equal([true, false], h.Identity.Modes);
        Assert.Null(h.Identity.Browsers[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeHandoffStopsIdentityWork(bool cancelRequest)
    {
        await using var h = new ConnectionHarness();
        h.Identity.OpenBrowser = false;
        var gate = new Gate<SignInObservation>();
        h.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        var ticket = h.Service.StartBrowserSignIn(new(), "", ReturnUri);
        await gate.WaitForEntryAsync();
        using var cancellation = new CancellationTokenSource();
        var waiting = h.Service.WaitForBrowserSignInAsync(ticket, cancellation.Token);

        if (cancelRequest)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        else
        {
            var operationRevision = (await h.Service.GetStateAsync(Principal(ticket))).OperationRevision;
            h.Service.Cancel(Principal(ticket), ticket.Version, operationRevision);
            Assert.Null(await waiting.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        await gate.WaitForCancellationAsync();
        var state = await h.Service.GetStateAsync(Principal(ticket));
        Assert.Equal(cancelRequest ? ConnectionPhase.Failed : ConnectionPhase.SignedOut, state.Phase);
        Assert.Equal("cancelled", state.Problem?.Code);
        Assert.Null(state.Mailbox);
        Assert.Equal(0, h.Calendar.Verifications);
    }

    [Fact]
    public async Task MissingAuthorizationCallbackHasBoundedWaitAndCancelsProvider()
    {
        await using var h = new ConnectionHarness();
        h.Identity.OpenBrowser = false;
        var gate = new Gate<SignInObservation>();
        h.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        var ticket = h.Service.StartBrowserSignIn(new(), "", ReturnUri);
        await gate.WaitForEntryAsync();

        Assert.Null(await h.Service.WaitForBrowserSignInAsync(ticket).WaitAsync(TimeSpan.FromSeconds(20)));

        await gate.WaitForCancellationAsync();
        var state = await h.Service.GetStateAsync(Principal(ticket));
        Assert.Equal(ConnectionPhase.Failed, state.Phase);
        Assert.Equal("cancelled", state.Problem?.Code);
        Assert.Equal(0, h.Calendar.Verifications);
    }

    [Theory]
    [InlineData("/connect")]
    [InlineData("https://localhost:5009/connect")]
    [InlineData("http://evil.example:5009/connect")]
    [InlineData("http://localhost.evil.example:5009/connect")]
    [InlineData("http://user@localhost:5009/connect")]
    [InlineData("http://localhost:5009/connection/state")]
    [InlineData("http://localhost:5009/connect?next=evil")]
    [InlineData("http://localhost:5009/connect#fragment")]
    public async Task RealAdapterRejectsBadReturnUriBeforeOpeningBrowser(string value)
    {
        var directory = Path.Combine(Path.GetTempPath(), "signin-return-" + Guid.NewGuid());
        var opened = false;
        try
        {
            using var identity = new TenantSignIn(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "", directory));
            var browser = new BrowserSignInContext(new Uri(value, UriKind.RelativeOrAbsolute), _ =>
            {
                opened = true;
                return Task.CompletedTask;
            });

            await Assert.ThrowsAsync<ArgumentException>(() => identity.SignInAsync(false, browser));

            Assert.False(opened);
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, true); }
        }
    }
}
