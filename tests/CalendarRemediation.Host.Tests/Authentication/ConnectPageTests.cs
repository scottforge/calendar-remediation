using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;
using CalendarRemediation.Host.Authentication;
using CalendarRemediation.Host.Review;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class ConnectPageTests
{
    [Fact]
    public async Task SignedOutSessionOffersDirectBrowserSignInAndDisconnect()
    {
        await using var harness = new ConnectionHarness();
        var principal = Principal(harness.Bootstrap());
        Assert.NotEmpty((await harness.Service.GetStateAsync(principal)).Version);

        var html = await RenderAsync(harness, principal);

        var form = AssertPostForm(html, "/connection/signin");
        Assert.Contains("Sign in with Microsoft", form);
        Assert.DoesNotContain("target=", form);
        Assert.DoesNotContain("disabled", form);
        AssertDisconnectForm(html);
        AssertNoBootstrapGate(html);
        Assert.Equal(0, harness.Calendar.Verifications);
        Assert.Equal(0, harness.Calendar.Discoveries);
    }

    [Fact]
    public async Task DisabledConfigurationPointsToConfigurationAndDisablesSignIn()
    {
        await using var harness = new ConnectionHarness();
        harness.Identity.Unavailable = new("configuration", "Microsoft 365 connection is not configured.");

        var html = await RenderAsync(harness, new ClaimsPrincipal(new ClaimsIdentity()));

        Assert.Contains("configuration", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Developer configuration", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unavailable in this build", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LocalConnection__TenantId", html);
        Assert.Contains("LocalConnection__ClientId", html);
        Assert.Contains("work/school", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("personal Microsoft accounts", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("api.requestedAccessTokenVersion", html);
        Assert.Contains("tenant GUID", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http://localhost", html);
        Assert.Contains("No client secret", html, StringComparison.OrdinalIgnoreCase);
        var signIn = Assert.Single(Regex.Matches(html, @"<button\b[^>]*>\s*Sign in with Microsoft\s*</button>")
            .Cast<Match>());
        Assert.Contains("disabled", signIn.Value);
        AssertNoBootstrapGate(html);
        Assert.Empty(harness.Identity.Modes);
    }

    [Fact]
    public async Task FailedSessionWithoutVerifiedMailboxOffersDisconnectAndCacheClear()
    {
        await using var harness = new ConnectionHarness();
        harness.Identity.Authenticate = (_, _) => throw new InvalidOperationException("Synthetic sign-in failure.");
        var bootstrap = harness.Bootstrap();
        var pending = harness.Service.StartBrowserSignIn(Principal(bootstrap), bootstrap.Version, ReturnUri);
        var principal = Principal(pending);
        await harness.WaitForPhaseAsync(principal, ConnectionPhase.Failed);

        var html = await RenderAsync(harness, principal);

        AssertDisconnectForm(html);
    }

    [Fact]
    public async Task AwaitingCompletionRendersGuardedCompletionForm()
    {
        await using var harness = new ConnectionHarness();
        var bootstrap = harness.Bootstrap();
        var pending = harness.Service.StartBrowserSignIn(Principal(bootstrap), bootstrap.Version, ReturnUri);
        var principal = Principal(pending);
        await harness.WaitForPhaseAsync(principal, ConnectionPhase.AwaitingCompletion);

        var html = await RenderAsync(harness, principal);

        var form = AssertPostForm(html, "/connection/complete");
        AssertHiddenInput(form, "version", pending.Version);
        Assert.Contains(Mailbox.Address, html);
        Assert.DoesNotContain("action=\"/connection/discover\"", html);
        AssertNoBootstrapGate(html);
        Assert.Equal(ConnectionPhase.AwaitingCompletion, (await harness.Service.GetStateAsync(principal)).Phase);
        Assert.Equal(0, harness.Calendar.Discoveries);
    }

    [Fact]
    public async Task ConnectedDiscoveryRendersSourceAndCalculatedPreviewWithoutMutationActions()
    {
        await using var harness = new ConnectionHarness();
        var location = new ConfirmedLocation("SK", "Regina", "America/Regina");
        // Keep the fixture inside the service's real rolling discovery window.
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(12), TimeSpan.Zero);
        var end = start.AddHours(1);
        var source = new SourceSnapshot(Mailbox.Identity, "render-meeting", "v1", "etag-render", start, end,
            new(start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), "UTC"),
            new(end.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), "UTC"),
            true, Mailbox.Address, [new("colleague@example.test", AttendeeKind.Internal)], false, false,
            EventKind.SingleInstance, null, new("UTC", "UTC", "UTC"), []);
        harness.Calendar.Discover = (scope, _) => Task.FromResult(new DiscoveryResult(scope, true, [source], [], [], 1, 0));
        var ticket = await harness.ConnectAsync();
        var principal = Principal(ticket);
        harness.Service.ConfirmLocation(principal, ticket.Version, location);
        harness.Service.StartDiscovery(principal, ticket.Version);
        await harness.WaitForPhaseAsync(principal, ConnectionPhase.Connected);
        harness.Service.PreviewMeeting(principal, ticket.Version, source.Id, start.Date.AddHours(7), location.TimeZoneId, false);
        var preview = Assert.IsType<MeetingTimePreview>((await harness.Service.GetStateAsync(principal)).Preview);
        Assert.Equal(PreviewDisposition.Proposed, preview.Disposition);
        Assert.Equal(start.AddHours(1), preview.ProposedStart);

        var html = await RenderAsync(harness, principal);
        var text = WebUtility.HtmlDecode(html);

        Assert.Contains(Mailbox.Address, text);
        Assert.Contains(source.Id, text);
        Assert.Contains(location.Community, text);
        Assert.Contains(location.TimeZoneId, text);
        Assert.Contains(preview.Reason, text);
        Assert.Contains("Keep the same absolute instant (UTC time)", text);
        Assert.Contains("keep the same absolute instant (UTC time)", text);
        AssertPreviewTime(text, "Source start", start, location.TimeZoneId);
        AssertPreviewTime(text, "Source end", end, location.TimeZoneId);
        AssertPreviewTime(text, "Proposed start", start.AddHours(1), location.TimeZoneId);
        AssertPreviewTime(text, "Proposed end", end.AddHours(1), location.TimeZoneId);
        var visibleText = Regex.Replace(text, "<[^>]+>", " ");
        Assert.Contains("Start change: +60 minutes (later)", visibleText);
        AssertPostForm(html, "/connection/discover");
        AssertHiddenInput(AssertPostForm(html, "/connection/preview"), "version", ticket.Version);
        var allowedActions = new[] { "/connection/location", "/connection/discover", "/connection/preview", "/connection/signout" };
        foreach (Match action in Regex.Matches(html, "<form\\b[^>]*action=\"([^\"]+)\""))
        {
            Assert.Contains(action.Groups[1].Value, allowedActions);
        }
        AssertNoBootstrapGate(html);
        Assert.Equal(1, harness.Calendar.Discoveries);
        Assert.Equal(1, harness.Calendar.Verifications);
        Assert.Single(harness.Identity.Modes);
    }

    internal static async Task<string> RenderAsync(ConnectionHarness harness, ClaimsPrincipal principal)
    {
        await using var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(harness.Service)
            .AddSingleton<AuthenticationStateProvider>(new TestAuthenticationStateProvider(principal))
            .AddSingleton<AntiforgeryStateProvider, TestAntiforgeryStateProvider>()
            .AddSingleton<IJSRuntime, NoJavaScriptRuntime>()
            .BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Connect>()).ToHtmlString());
    }

    internal static string AssertPostForm(string html, string action)
    {
        var form = Assert.Single(Regex.Matches(html, @"<form\b[^>]*>.*?</form>", RegexOptions.Singleline)
            .Cast<Match>(), match => match.Value.Contains($"action=\"{action}\"", StringComparison.Ordinal)).Value;
        Assert.Contains("method=\"post\"", form);
        Assert.Contains("data-enhance=\"false\"", form);
        AssertHiddenInput(form, "__RequestVerificationToken", "render-antiforgery-token");
        return form;
    }

    private static void AssertHiddenInput(string form, string name, string value)
    {
        var input = Assert.Single(Regex.Matches(form, @"<input\b[^>]*>").Cast<Match>(),
            match => match.Value.Contains($"name=\"{name}\"", StringComparison.Ordinal)).Value;
        Assert.Contains("type=\"hidden\"", input);
        Assert.Contains($"value=\"{WebUtility.HtmlEncode(value)}\"", input);
    }

    private static void AssertPreviewTime(string html, string label, DateTimeOffset instant, string zoneId)
    {
        var section = Assert.Single(Regex.Matches(html, @"<div\b[^>]*>\s*<dt\b[^>]*>(?<label>[^<]*)</dt>.*?</div>", RegexOptions.Singleline)
            .Cast<Match>(), match => match.Groups["label"].Value == label).Value;
        Assert.Contains(instant.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture), section);
        Assert.Contains(TimeZoneInfo.ConvertTimeBySystemTimeZoneId(instant, zoneId)
            .ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture), section);
    }

    private static void AssertNoBootstrapGate(string html)
    {
        Assert.DoesNotContain("/connection/bootstrap", html);
        Assert.DoesNotContain("Start local session", html);
        Assert.DoesNotContain("Start a local connection session", html);
    }

    private static void AssertDisconnectForm(string html)
    {
        var form = AssertPostForm(html, "/connection/signout");
        Assert.Contains("Disconnect and clear app account cache", form);
    }

    private sealed class TestAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class TestAntiforgeryStateProvider : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new("render-antiforgery-token", "__RequestVerificationToken");
    }

    private sealed class NoJavaScriptRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke browser JavaScript.");

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            throw new InvalidOperationException("Static rendering must not invoke browser JavaScript.");
    }
}
