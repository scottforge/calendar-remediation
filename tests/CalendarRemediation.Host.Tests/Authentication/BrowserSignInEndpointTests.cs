using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarRemediation.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class BrowserSignInEndpointTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/connect")]
    [InlineData("/connection/antiforgery")]
    public async Task BrowserFormsRetainSameOriginWithoutSendingCrossOriginReferrers(string path)
    {
        await using var host = await BrowserHost.StartAsync();
        using var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("same-origin", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task AnonymousSingleSubmitStartsBrowserSignInAndCompletionRotatesToVerifiedCookie()
    {
        await using var host = await BrowserHost.StartAsync();
        var gate = new Gate<SignInObservation>();
        host.Harness.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        Assert.Equal(ConnectionPhase.SignedOut, (await host.StateAsync()).Phase);
        using (var signin = await host.PostAsync("signin"))
        {
            AssertAuthorizationRedirect(signin);
            var cookie = Assert.Single(signin.Headers.GetValues("Set-Cookie"), value => value.StartsWith(host.ConnectionCookieName + "=", StringComparison.Ordinal));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
        }
        await gate.WaitForEntryAsync();
        Assert.Equal([true], host.Harness.Identity.Modes);
        Assert.Equal(ReturnUri, Assert.Single(host.Harness.Identity.Browsers)!.ReturnUri);
        var pendingCookie = host.ConnectionCookie;
        var pendingTicket = host.ReadCookie();
        Assert.Equal("false", pendingTicket.Principal.FindFirstValue("connection_verified"));
        Assert.False(pendingTicket.Properties.IsPersistent);
        Assert.False(pendingTicket.Properties.AllowRefresh);
        Assert.Equal(ConnectionPhase.SigningIn, (await host.StateAsync()).Phase);
        var pending = await host.StateAsync();
        using (var premature = await host.PostAsync("complete", ("version", pending.Version)))
        {
            await AssertProblemAsync(premature, HttpStatusCode.Conflict, "not-ready");
        }
        gate.Result.SetResult(Account);
        await host.Harness.WaitForPhaseAsync(pendingTicket.Principal, ConnectionPhase.AwaitingCompletion);
        var ready = await host.StateAsync();
        using (var blocked = await host.PostAsync("discover", ("version", ready.Version)))
        {
            await AssertProblemAsync(blocked, HttpStatusCode.Conflict, "sign-in-required");
        }
        using (var complete = await host.PostAsync("complete", ("version", ready.Version))) { AssertRedirect(complete); }
        var verified = host.ReadCookie();
        Assert.Equal("true", verified.Principal.FindFirstValue("connection_verified"));
        Assert.Equal(Mailbox.Identity.MailboxId, verified.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(Mailbox.DisplayName, verified.Principal.Identity?.Name);
        Assert.NotEqual(pendingCookie, host.ConnectionCookie);
        Assert.NotEqual(pending.Version, (await host.StateAsync()).Version);
        Assert.Equal(ConnectionPhase.Connected, (await host.StateAsync()).Phase);
        Assert.False(host.Harness.Service.IsCurrent(pendingTicket.Principal));
        using var staleClient = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = host.Client.BaseAddress };
        staleClient.DefaultRequestHeaders.Host = "localhost:5009";
        staleClient.DefaultRequestHeaders.Add("Cookie", pendingCookie);
        var stale = await host.StateAsync(staleClient);
        Assert.Equal("", stale.Version);
        Assert.Null(stale.Mailbox);
    }

    [Fact]
    public async Task CancellationAllowsSingleSubmitRetryAndRejectsOldCompletion()
    {
        await using var host = await BrowserHost.StartAsync();
        var gate = new Gate<SignInObservation>();
        host.Harness.Identity.Authenticate = (_, ct) => gate.RunAsync(ct);
        using (var signin = await host.PostAsync("signin")) { AssertAuthorizationRedirect(signin); }
        await gate.WaitForEntryAsync();
        var pending = host.ReadCookie().Principal;
        var state = await host.StateAsync();
        using (var cancel = await host.PostAsync("cancel", ("version", state.Version),
            ("operationRevision", state.OperationRevision.ToString(CultureInfo.InvariantCulture)))) { AssertRedirect(cancel); }
        await gate.WaitForCancellationAsync();
        var cancelled = await host.Harness.WaitForPhaseAsync(pending, ConnectionPhase.SignedOut);
        Assert.Equal("cancelled", cancelled.Problem?.Code);
        host.Harness.Identity.Authenticate = (_, _) => Task.FromResult(Account);
        using (var retry = await host.PostAsync("signin", ("version", cancelled.Version))) { AssertAuthorizationRedirect(retry); }
        var retryPrincipal = host.ReadCookie().Principal;
        Assert.False(host.Harness.Service.IsCurrent(pending));
        await host.Harness.WaitForPhaseAsync(retryPrincipal, ConnectionPhase.AwaitingCompletion);
        using (var stale = await host.PostAsync("complete", ("version", state.Version)))
        {
            await AssertProblemAsync(stale, HttpStatusCode.Conflict, "stale-session");
        }
        using (var complete = await host.PostAsync("complete", ("version", (await host.StateAsync()).Version))) { AssertRedirect(complete); }
        Assert.Equal([true, true], host.Harness.Identity.Modes);
        Assert.Equal(ConnectionPhase.Connected, (await host.StateAsync()).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldSignInPageCannotReplaceAConnectedSession(bool useCurrentVersion)
    {
        await using var host = await BrowserHost.StartAsync();
        using (var signin = await host.PostAsync("signin")) { AssertAuthorizationRedirect(signin); }
        var pending = host.ReadCookie().Principal;
        var ready = await host.Harness.WaitForPhaseAsync(pending, ConnectionPhase.AwaitingCompletion);
        using (var complete = await host.PostAsync("complete", ("version", ready.Version))) { AssertRedirect(complete); }
        var principal = host.ReadCookie().Principal;
        var connected = await host.StateAsync();
        host.Harness.Service.ConfirmLocation(principal, connected.Version, Location);
        host.Harness.Service.StartDiscovery(principal, connected.Version);
        var before = await host.Harness.WaitForPhaseAsync(principal, ConnectionPhase.Connected);

        using var stale = await host.PostAsync("signin", ("version", useCurrentVersion ? connected.Version : ""));

        await AssertProblemAsync(stale, HttpStatusCode.Conflict, "stale-session");
        var after = await host.Harness.Service.GetStateAsync(principal);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.Location, after.Location);
        Assert.NotNull(before.Discovery);
        Assert.Same(before.Discovery, after.Discovery);
        Assert.Equal(ConnectionPhase.Connected, after.Phase);
        Assert.Single(host.Harness.Identity.Modes);
    }

    [Theory]
    [InlineData("signin")]
    [InlineData("start")]
    public async Task SignInRequiresPostSameOriginAndAntiforgeryBeforeStartingIdentityWork(string action)
    {
        await using var host = await BrowserHost.StartAsync();
        using (var get = await host.Client.GetAsync("/connection/" + action)) { Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode); }
        using (var content = new FormUrlEncodedContent([]))
        using (var missingToken = await host.Client.PostAsync("/connection/" + action, content))
        {
            await AssertProblemAsync(missingToken, HttpStatusCode.BadRequest, "invalid-form");
        }
        host.Client.DefaultRequestHeaders.Remove("Origin");
        using (var missingOrigin = await host.PostAsync(action, ("mode", "interactive")))
        {
            await AssertProblemAsync(missingOrigin, HttpStatusCode.Forbidden, "invalid-origin");
        }
        host.Client.DefaultRequestHeaders.Add("Origin", "http://evil.example:5009");
        using (var content = new FormUrlEncodedContent([]))
        using (var wrongOrigin = await host.Client.PostAsync("/connection/" + action, content)) { Assert.Equal(HttpStatusCode.Forbidden, wrongOrigin.StatusCode); }
        Assert.Empty(host.Harness.Identity.Modes);
        Assert.Null(host.Cookies.GetCookies(host.Client.BaseAddress!)[host.ConnectionCookieName]);
        host.Client.DefaultRequestHeaders.Remove("Origin");
        host.Client.DefaultRequestHeaders.Referrer = new Uri("http://localhost:5009/connect");
        using (var referer = await host.PostAsync(action, ("mode", "interactive"))) { AssertAuthorizationRedirect(referer); }
        await host.Harness.WaitForPhaseAsync(host.ReadCookie().Principal, ConnectionPhase.AwaitingCompletion);
        Assert.Equal([true], host.Harness.Identity.Modes);
    }

    [Theory]
    [InlineData("signin")]
    [InlineData("start")]
    public async Task EarlyIdentityFailureReturnsToConnectWithPendingCookieAndSanitizedProblem(string action)
    {
        await using var host = await BrowserHost.StartAsync();
        host.Harness.Identity.OpenBrowser = false;
        host.Harness.Identity.Authenticate = (_, _) => Task.FromException<SignInObservation>(new InvalidOperationException("private diagnostic"));

        using var response = await host.PostAsync(action, ("mode", "interactive"));

        AssertRedirect(response);
        var ticket = host.ReadCookie();
        Assert.Equal("false", ticket.Principal.FindFirstValue("connection_verified"));
        var failed = await host.Harness.WaitForPhaseAsync(ticket.Principal, ConnectionPhase.Failed);
        Assert.Equal("connection-failed", failed.Problem?.Code);
        Assert.DoesNotContain("private diagnostic", failed.Problem!.Message);
        Assert.Equal(0, host.Harness.Calendar.Verifications);
        Assert.Equal([true], host.Harness.Identity.Modes);
    }

    [Fact]
    public async Task ContinueUsesSilentAuthenticationWithoutBrowserHandoff()
    {
        await using var host = await BrowserHost.StartAsync();
        using (var bootstrap = await host.PostAsync("bootstrap")) { AssertRedirect(bootstrap); }
        var state = await host.StateAsync();

        using var response = await host.PostAsync("start", ("version", state.Version), ("mode", "continue"));

        AssertRedirect(response);
        await host.Harness.WaitForPhaseAsync(host.ReadCookie().Principal, ConnectionPhase.AwaitingCompletion);
        Assert.Equal([false], host.Harness.Identity.Modes);
        Assert.Null(Assert.Single(host.Harness.Identity.Browsers));
    }

    [Theory]
    [InlineData("GET", "/connect", "navigate", "document", "127.0.0.1:5009", null, HttpStatusCode.OK)]
    [InlineData("GET", "/connect", "navigate", "document", "localhost:5009", null, HttpStatusCode.OK)]
    [InlineData("GET", "/connect", "navigate", "document", "[::1]:5009", null, HttpStatusCode.OK)]
    [InlineData("POST", "/connect", "navigate", "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connection/state", "navigate", "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connection/antiforgery", "navigate", "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/", "navigate", "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect/other", "navigate", "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", "cors", "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", "navigate", "iframe", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", null, "document", "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", "navigate", null, "127.0.0.1:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", "navigate", "document", "evil.example:5009", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", "navigate", "document", "127.0.0.1:5010", null, HttpStatusCode.Forbidden)]
    [InlineData("GET", "/connect", "navigate", "document", "127.0.0.1:5009", "http://localhost:5009", HttpStatusCode.Forbidden)]
    public async Task CrossSiteLoopbackReturnOnlyAllowsDocumentGetToConnect(string method, string path, string? mode,
        string? destination, string requestHost, string? origin, HttpStatusCode expected)
    {
        await using var host = await BrowserHost.StartAsync();
        host.Client.DefaultRequestHeaders.Remove("Origin");
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Host = requestHost;
        request.Headers.Referrer = new Uri("http://localhost:49152/");
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        if (mode is not null) { request.Headers.Add("Sec-Fetch-Mode", mode); }
        if (destination is not null) { request.Headers.Add("Sec-Fetch-Dest", destination); }
        if (origin is not null) { request.Headers.Add("Origin", origin); }

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(host.Harness.Identity.Modes);
        Assert.Equal(0, host.Harness.Identity.CacheReads);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static void AssertAuthorizationRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal(AuthorizationUri, response.Headers.Location);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static void AssertRedirect(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/connect", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ConnectionProblem>())?.Code);
    }

    private sealed class BrowserHost(WebApplication app, ConnectionHarness harness, HttpClient client, CookieContainer cookies) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        public ConnectionHarness Harness { get; } = harness;
        public HttpClient Client { get; } = client;
        public CookieContainer Cookies { get; } = cookies;
        public string ConnectionCookieName => app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ConnectionEndpoints.Scheme).Cookie.Name!;
        public string ConnectionCookie => ConnectionCookieName + "=" + Cookies.GetCookies(Client.BaseAddress!)[ConnectionCookieName]!.Value;

        public static async Task<BrowserHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddLocalConnection(builder.Configuration);
            builder.Services.AddAntiforgery();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var harness = new ConnectionHarness();
            builder.Services.AddSingleton<ILocalIdentityClient>(harness.Identity);
            builder.Services.AddSingleton<ILocalCalendarClient>(harness.Calendar);
            builder.Services.AddSingleton(harness.Service);
            var app = builder.Build();
            try
            {
                app.UseLocalConnectionGuard();
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/", () => "Connection page");
                app.MapGet("/connect", () => "Connection page");
                app.MapLocalConnection();
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                var cookies = new CookieContainer();
                var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
                {
                    BaseAddress = new Uri(address),
                    Timeout = TimeSpan.FromSeconds(10)
                };
                client.DefaultRequestHeaders.Host = "localhost:5009";
                client.DefaultRequestHeaders.Add("Origin", "http://localhost:5009");
                return new(app, harness, client, cookies);
            }
            catch
            {
                await app.DisposeAsync();
                await harness.DisposeAsync();
                throw;
            }
        }

        public AuthenticationTicket ReadCookie() => Assert.IsType<AuthenticationTicket>(app.Services
            .GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ConnectionEndpoints.Scheme)
            .TicketDataFormat.Unprotect(Cookies.GetCookies(Client.BaseAddress!)[ConnectionCookieName]!.Value));

        public async Task<ConnectionState> StateAsync(HttpClient? client = null) => (await (client ?? Client).GetFromJsonAsync<ConnectionState>("/connection/state", Json))!;

        public async Task<HttpResponseMessage> PostAsync(string action, params (string Key, string Value)[] fields)
        {
            var token = (await Client.GetFromJsonAsync<AntiforgeryForm>("/connection/antiforgery"))!;
            using var content = new FormUrlEncodedContent(fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value))
                .Append(new(token.FormFieldName, token.RequestToken)));
            return await Client.PostAsync("/connection/" + action, content);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.DisposeAsync();
            await Harness.DisposeAsync();
        }

        private sealed record AntiforgeryForm(string RequestToken, string FormFieldName);
    }
}
