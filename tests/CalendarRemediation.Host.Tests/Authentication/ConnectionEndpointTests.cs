using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Host.Authentication;
using Microsoft.AspNetCore.Antiforgery;
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

public sealed class ConnectionEndpointTests
{
    [Fact]
    public async Task RealHttpLifecycleIssuesNonpersistentRotatingCookiesAndRejectsStaleBrowser()
    {
        await using var host = await LoopbackHost.StartAsync();
        var initial = await host.StateAsync();
        Assert.Equal(ConnectionPhase.SignedOut, initial.Phase);
        Assert.Equal("", initial.Version);
        using (var bootstrap = await host.PostAsync("bootstrap")) { AssertRedirect(bootstrap); }
        var bootstrapCookie = host.Cookie;
        var bootstrapTicket = host.ReadCookie();
        Assert.False(bootstrapTicket.Properties.IsPersistent);
        Assert.False(bootstrapTicket.Properties.AllowRefresh);
        Assert.Equal("false", bootstrapTicket.Principal.FindFirstValue("connection_verified"));
        var bootstrapState = await host.StateAsync();
        using (var read = await host.PostAsync("discover", ("version", bootstrapState.Version)))
        {
            await AssertProblemAsync(read, HttpStatusCode.Conflict, "sign-in-required");
        }
        using (var start = await host.PostAsync("start", ("version", bootstrapState.Version), ("mode", "interactive")))
        {
            Assert.Equal(HttpStatusCode.Found, start.StatusCode);
            Assert.Equal(AuthorizationUri, start.Headers.Location);
        }
        var pendingCookie = host.Cookie;
        Assert.NotEqual(bootstrapCookie, pendingCookie);
        var pendingTicket = host.ReadCookie();
        await host.Harness.WaitForPhaseAsync(pendingTicket.Principal, ConnectionPhase.AwaitingCompletion);
        var ready = await host.StateAsync();
        using (var premature = await host.PostAsync("discover", ("version", ready.Version)))
        {
            await AssertProblemAsync(premature, HttpStatusCode.Conflict, "sign-in-required");
        }
        using (var complete = await host.PostAsync("complete", ("version", ready.Version)))
        {
            AssertRedirect(complete);
            var cookie = Assert.Single(complete.Headers.GetValues("Set-Cookie"), value => value.StartsWith(host.CookieName + "=", StringComparison.Ordinal));
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("max-age=", cookie, StringComparison.OrdinalIgnoreCase);
        }
        var verifiedTicket = host.ReadCookie();
        Assert.Equal("true", verifiedTicket.Principal.FindFirstValue("connection_verified"));
        Assert.Equal(Mailbox.Identity.MailboxId, verifiedTicket.Principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(Mailbox.DisplayName, verifiedTicket.Principal.Identity?.Name);
        Assert.NotEqual(pendingTicket.Principal.FindFirstValue(LocalConnectionService.SessionClaim),
            verifiedTicket.Principal.FindFirstValue(LocalConnectionService.SessionClaim));
        var state = await host.StateAsync();
        Assert.Equal(ConnectionPhase.Connected, state.Phase);
        using (var missingLocation = await host.PostAsync("discover", ("version", state.Version)))
        {
            await AssertProblemAsync(missingLocation, HttpStatusCode.Conflict, "location-required");
        }
        using (var location = await host.PostAsync("location", ("version", state.Version), ("province", "BC"),
            ("community", "Vancouver"), ("timeZoneId", "America/Vancouver"))) { AssertRedirect(location); }
        using (var discovery = await host.PostAsync("discover", ("version", state.Version))) { AssertRedirect(discovery); }
        await host.Harness.WaitForPhaseAsync(verifiedTicket.Principal, ConnectionPhase.Connected);
        Assert.NotNull((await host.StateAsync()).Discovery);
        using var staleClient = host.CreateClient(pendingCookie);
        var stale = await staleClient.GetFromJsonAsync<ConnectionState>("/connection/state", LoopbackHost.Json);
        Assert.NotNull(stale);
        Assert.Equal("", stale.Version);
        Assert.Null(stale.Mailbox);
        Assert.Null(stale.Discovery);
        using (var signout = await host.PostAsync("signout", ("version", state.Version))) { AssertRedirect(signout); }
        Assert.Equal(1, host.Harness.Identity.Clears);
        Assert.False(host.Harness.Service.IsCurrent(verifiedTicket.Principal));
        Assert.Equal("", (await host.StateAsync()).Version);
    }

    [Fact]
    public async Task AntiforgeryIsRequiredAndTokensMustBeRefreshedAfterCookiePrincipalTransition()
    {
        await using var host = await LoopbackHost.StartAsync();
        using (var missing = await host.Client.PostAsync("/connection/bootstrap", new FormUrlEncodedContent([])))
        {
            await AssertProblemAsync(missing, HttpStatusCode.BadRequest, "invalid-form");
        }
        var anonymousToken = await host.TokenAsync();
        using (var bootstrap = await host.PostWithTokenAsync("bootstrap", anonymousToken)) { AssertRedirect(bootstrap); }
        var state = await host.StateAsync();
        using (var expired = await host.PostWithTokenAsync("start", anonymousToken, ("version", state.Version), ("mode", "interactive")))
        {
            await AssertProblemAsync(expired, HttpStatusCode.BadRequest, "invalid-form");
        }
        Assert.Empty(host.Harness.Identity.Modes);
        using (var missingVersion = await host.PostAsync("start", ("mode", "interactive")))
        {
            await AssertProblemAsync(missingVersion, HttpStatusCode.Conflict, "stale-session");
        }
        using (var invalidMode = await host.PostAsync("start", ("version", state.Version), ("mode", "invented")))
        {
            await AssertProblemAsync(invalidMode, HttpStatusCode.Conflict, "invalid-mode");
        }
        using (var valid = await host.PostAsync("start", ("version", state.Version), ("mode", "interactive")))
        {
            Assert.Equal(HttpStatusCode.Found, valid.StatusCode);
            Assert.Equal(AuthorizationUri, valid.Headers.Location);
        }
    }

    [Theory]
    [InlineData("evil.example:5009", "http://evil.example:5009", null)]
    [InlineData("localhost:5010", "http://localhost:5010", null)]
    [InlineData("localhost:5009", "https://localhost:5009", null)]
    [InlineData("localhost:5009", "http://evil.example:5009", null)]
    [InlineData("localhost:5009", "http://localhost:5009", "cross-site")]
    public async Task GuardRejectsWrongHostPortOriginOrCrossSiteRequests(string requestHost, string origin, string? fetchSite)
    {
        await using var host = await LoopbackHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/connection/state");
        var port = host.Client.BaseAddress!.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        request.Headers.Host = requestHost.Replace(":5009", ":" + port, StringComparison.Ordinal);
        request.Headers.Add("Origin", origin.Replace(":5009", ":" + port, StringComparison.Ordinal));
        if (fetchSite is not null) { request.Headers.Add("Sec-Fetch-Site", fetchSite); }
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, host.Harness.Identity.CacheReads);
    }

    [Fact]
    public async Task MutationNeedsSameOriginOrRefererEvenWithValidAntiforgery()
    {
        await using var host = await LoopbackHost.StartAsync();
        var token = await host.TokenAsync();
        host.Client.DefaultRequestHeaders.Remove("Origin");
        using (var missingOrigin = await host.PostWithTokenAsync("bootstrap", token))
        {
            await AssertProblemAsync(missingOrigin, HttpStatusCode.Forbidden, "invalid-origin");
        }
        host.Client.DefaultRequestHeaders.Referrer = new Uri(host.Client.BaseAddress!, "/connect");
        using (var referer = await host.PostWithTokenAsync("bootstrap", token)) { AssertRedirect(referer); }
    }

    [Fact]
    public async Task CacheClearFailureReturns503ExpiresCookieAndInvalidatesCopiedCookie()
    {
        await using var host = await LoopbackHost.StartAsync();
        using (var bootstrap = await host.PostAsync("bootstrap")) { AssertRedirect(bootstrap); }
        var copiedCookie = host.Cookie;
        var state = await host.StateAsync();
        host.Harness.Identity.Clear = () => Task.FromException(new IOException("private storage diagnostic"));
        using (var failure = await host.PostAsync("signout", ("version", state.Version)))
        {
            await AssertProblemAsync(failure, HttpStatusCode.ServiceUnavailable, "account-cache-unavailable");
            Assert.Contains(failure.Headers.GetValues("Set-Cookie"), value => value.StartsWith(host.CookieName + "=;", StringComparison.Ordinal));
        }
        using var stale = host.CreateClient(copiedCookie);
        var staleState = await stale.GetFromJsonAsync<ConnectionState>("/connection/state", LoopbackHost.Json);
        Assert.Equal("", staleState?.Version);
        host.Harness.Identity.Clear = () => Task.CompletedTask;
        using (var retry = await host.PostAsync("bootstrap")) { AssertRedirect(retry); }
        Assert.NotEqual(state.Version, (await host.StateAsync()).Version);
    }

    [Fact]
    public async Task TwoListenerPortsKeepPreviouslyIssuedFormsAndCookiesIndependent()
    {
        var keys = Directory.CreateTempSubdirectory("calendar-connection-keys-");
        try
        {
            var cookies = new CookieContainer();
            await using var first = await LoopbackHost.StartAsync(cookies, keys.CreateSubdirectory("first"));
            await using var second = await LoopbackHost.StartAsync(cookies, keys.CreateSubdirectory("second"));
            Assert.NotEqual(first.Client.BaseAddress!.Port, second.Client.BaseAddress!.Port);
            Assert.Equal("CalendarRemediation.Connection." + first.Client.BaseAddress.Port, first.CookieName);
            Assert.Equal("CalendarRemediation.Connection." + second.Client.BaseAddress.Port, second.CookieName);
            Assert.Equal("CalendarRemediation.Antiforgery." + first.Client.BaseAddress.Port, first.AntiforgeryCookieName);
            Assert.Equal("CalendarRemediation.Antiforgery." + second.Client.BaseAddress.Port, second.AntiforgeryCookieName);
            var firstState = await first.ConnectAsync();
            var firstToken = await first.TokenAsync();
            var firstAntiforgeryCookie = cookies.GetCookies(first.Client.BaseAddress)[first.AntiforgeryCookieName]!.Value;
            var secondState = await second.ConnectAsync();
            var secondToken = await second.TokenAsync();

            Assert.Equal(firstState.Version, (await first.StateAsync()).Version);
            Assert.Equal(secondState.Version, (await second.StateAsync()).Version);
            Assert.Equal("true", first.ReadCookie().Principal.FindFirstValue("connection_verified"));
            Assert.Equal("true", second.ReadCookie().Principal.FindFirstValue("connection_verified"));
            Assert.NotNull(cookies.GetCookies(first.Client.BaseAddress)[first.CookieName]);
            Assert.NotNull(cookies.GetCookies(second.Client.BaseAddress)[second.CookieName]);
            Assert.Equal(firstAntiforgeryCookie, cookies.GetCookies(first.Client.BaseAddress)[first.AntiforgeryCookieName]!.Value);
            Assert.NotNull(cookies.GetCookies(second.Client.BaseAddress)[second.AntiforgeryCookieName]);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(keys.FullName, "first"), "key-*.xml"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(keys.FullName, "second"), "key-*.xml"));

            using (var wrongToken = await first.PostWithTokenAsync("signout", secondToken, ("version", firstState.Version)))
            {
                await AssertProblemAsync(wrongToken, HttpStatusCode.BadRequest, "invalid-form");
            }
            // Submit the original A form after visiting B, without fetching a replacement A token.
            using (var signout = await first.PostWithTokenAsync("signout", firstToken, ("version", firstState.Version))) { AssertRedirect(signout); }

            Assert.Equal("", (await first.StateAsync()).Version);
            Assert.Null(cookies.GetCookies(first.Client.BaseAddress)[first.CookieName]);
            var remaining = await second.StateAsync();
            Assert.Equal(ConnectionPhase.Connected, remaining.Phase);
            Assert.Equal(secondState.Version, remaining.Version);
            Assert.Equal(Mailbox, remaining.Mailbox);
            Assert.NotNull(cookies.GetCookies(second.Client.BaseAddress)[second.CookieName]);
            Assert.NotNull(cookies.GetCookies(second.Client.BaseAddress)[second.AntiforgeryCookieName]);
            Assert.Equal(1, first.Harness.Identity.Clears);
            Assert.Equal(0, second.Harness.Identity.Clears);
        }
        finally { keys.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OldCancelFormCannotCancelReplacementDiscovery(bool replaceLocation, bool cancelReplacement)
    {
        await using var host = await LoopbackHost.StartAsync();
        var connected = await host.ConnectAsync();
        var principal = host.ReadCookie().Principal;
        using (var location = await host.PostAsync("location", ("version", connected.Version), ("province", "BC"),
            ("community", "Vancouver"), ("timeZoneId", "America/Vancouver"))) { AssertRedirect(location); }
        var first = new Gate<DiscoveryResult>();
        host.Harness.Calendar.Discover = (_, ct) => first.RunAsync(ct);
        using (var discover = await host.PostAsync("discover", ("version", connected.Version))) { AssertRedirect(discover); }
        await first.WaitForEntryAsync();
        var oldForm = await CancelFormAsync(host, principal);
        if (replaceLocation)
        {
            using var location = await host.PostAsync("location", ("version", connected.Version), ("province", "AB"),
                ("community", "Calgary"), ("timeZoneId", "America/Edmonton"));
            AssertRedirect(location);
        }
        else
        {
            using var cancel = await host.PostAsync("cancel", oldForm);
            AssertRedirect(cancel);
        }
        await first.WaitForCancellationAsync();
        var replacement = new Gate<DiscoveryResult>();
        host.Harness.Calendar.Discover = (_, ct) => replacement.RunAsync(ct);
        using (var discover = await host.PostAsync("discover", ("version", connected.Version))) { AssertRedirect(discover); }
        await replacement.WaitForEntryAsync();
        var current = await host.StateAsync();
        Assert.Equal(connected.Version, current.Version);
        var currentForm = await CancelFormAsync(host, principal);
        Assert.NotEqual(oldForm[1].Value, currentForm[1].Value);

        using (var stale = await host.PostAsync("cancel", oldForm))
        {
            await AssertProblemAsync(stale, HttpStatusCode.Conflict, "stale-session");
        }
        foreach (var revision in new[] { "", "-1", "not-a-revision", "2147483648" })
        {
            using var invalid = await host.PostAsync("cancel", ("version", current.Version), ("operationRevision", revision));
            await AssertProblemAsync(invalid, HttpStatusCode.Conflict, "stale-session");
        }
        using (var missing = await host.PostAsync("cancel", ("version", current.Version)))
        {
            await AssertProblemAsync(missing, HttpStatusCode.Conflict, "stale-session");
        }
        Assert.False(replacement.Cancelled.Task.IsCompleted);
        var after = await host.StateAsync();
        Assert.Equal(ConnectionPhase.Discovering, after.Phase);
        Assert.Equal(current.OperationRevision, after.OperationRevision);
        Assert.Null(after.Problem);
        Assert.Equal(current.Location, after.Location);
        if (cancelReplacement)
        {
            using var cancel = await host.PostAsync("cancel", currentForm);
            AssertRedirect(cancel);
            await replacement.WaitForCancellationAsync();
            var cancelled = await host.StateAsync();
            Assert.Equal(ConnectionPhase.Connected, cancelled.Phase);
            Assert.Equal("cancelled", cancelled.Problem?.Code);
            Assert.Null(cancelled.Discovery);
        }
        else
        {
            replacement.Result.SetResult(new(host.Harness.Calendar.Scope!, true, [], [], [], 1, 0));
            var complete = await host.Harness.WaitForPhaseAsync(principal, ConnectionPhase.Connected);
            Assert.True(complete.Discovery?.Complete);
            Assert.Null(complete.Problem);
        }
    }

    private static async Task<(string Key, string Value)[]> CancelFormAsync(LoopbackHost host, ClaimsPrincipal principal)
    {
        var html = await ConnectPageTests.RenderAsync(host.Harness, principal);
        var form = ConnectPageTests.AssertPostForm(html, "/connection/cancel");
        return [("version", Value("version")), ("operationRevision", Value("operationRevision"))];

        string Value(string name)
        {
            var input = Assert.Single(Regex.Matches(form, "<input\\b[^>]*>").Cast<Match>(),
                match => match.Value.Contains($"name=\"{name}\"", StringComparison.Ordinal)).Value;
            Assert.Contains("type=\"hidden\"", input);
            return WebUtility.HtmlDecode(Regex.Match(input, "value=\"([^\"]*)\"").Groups[1].Value);
        }
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

    private sealed class LoopbackHost : IAsyncDisposable
    {
        public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };
        private readonly WebApplication _app;
        private readonly CookieContainer _cookies;
        public ConnectionHarness Harness { get; }
        public HttpClient Client { get; private set; } = null!;
        public string Cookie => _cookies.GetCookieHeader(Client.BaseAddress!);
        public string CookieName => _app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ConnectionEndpoints.Scheme).Cookie.Name!;
        public string AntiforgeryCookieName => _app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;

        private LoopbackHost(WebApplication app, ConnectionHarness harness, CookieContainer cookies)
        {
            _app = app;
            Harness = harness;
            _cookies = cookies;
        }

        public static async Task<LoopbackHost> StartAsync(CookieContainer? cookies = null, DirectoryInfo? keyDirectory = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            // Allocate independent sockets, then use their actual ports for the guard and cookies.
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddLocalConnection(builder.Configuration);
            builder.Services.AddAntiforgery();
            var protection = builder.Services.AddDataProtection();
            if (keyDirectory is null) { protection.UseEphemeralDataProtectionProvider(); }
            else { protection.PersistKeysToFileSystem(keyDirectory); }
            var harness = new ConnectionHarness();
            builder.Services.AddSingleton<ILocalIdentityClient>(harness.Identity);
            builder.Services.AddSingleton<ILocalCalendarClient>(harness.Calendar);
            builder.Services.AddSingleton(harness.Service);
            var app = builder.Build();
            app.UseLocalConnectionGuard();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapLocalConnection();
            var host = new LoopbackHost(app, harness, cookies ?? new());
            try
            {
                await app.StartAsync();
                var server = app.Services.GetRequiredService<IServer>();
                var address = Assert.Single(server.Features.Get<IServerAddressesFeature>()!.Addresses);
                app.Services.GetRequiredService<IOptions<LocalConnectionOptions>>().Value.Port = new Uri(address).Port;
                host.Client = new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = host._cookies })
                {
                    BaseAddress = new Uri(address),
                    Timeout = TimeSpan.FromSeconds(10)
                };
                SetHeaders(host.Client);
                return host;
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        public HttpClient CreateClient(string cookie)
        {
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = Client.BaseAddress };
            SetHeaders(client);
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            return client;
        }

        private static void SetHeaders(HttpClient client)
        {
            client.DefaultRequestHeaders.Add("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
        }

        public AuthenticationTicket ReadCookie()
        {
            var options = _app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ConnectionEndpoints.Scheme);
            var value = _cookies.GetCookies(Client.BaseAddress!)[CookieName]!.Value;
            return Assert.IsType<AuthenticationTicket>(options.TicketDataFormat.Unprotect(value));
        }

        public async Task<ConnectionState> StateAsync()
        {
            using var response = await Client.GetAsync("/connection/state");
            response.EnsureSuccessStatusCode();
            Assert.True(response.Headers.CacheControl?.NoStore);
            return (await response.Content.ReadFromJsonAsync<ConnectionState>(Json))!;
        }

        public async Task<ConnectionState> ConnectAsync()
        {
            using (var signin = await PostAsync("signin"))
            {
                Assert.Equal(HttpStatusCode.Found, signin.StatusCode);
                Assert.Equal(AuthorizationUri, signin.Headers.Location);
            }
            var ready = await Harness.WaitForPhaseAsync(ReadCookie().Principal, ConnectionPhase.AwaitingCompletion);
            using (var complete = await PostAsync("complete", ("version", ready.Version))) { AssertRedirect(complete); }
            var state = await StateAsync();
            Assert.Equal(ConnectionPhase.Connected, state.Phase);
            return state;
        }

        public async Task<AntiforgeryForm> TokenAsync() => (await Client.GetFromJsonAsync<AntiforgeryForm>("/connection/antiforgery"))!;

        public async Task<HttpResponseMessage> PostAsync(string action, params (string Key, string Value)[] fields) =>
            await PostWithTokenAsync(action, await TokenAsync(), fields);

        public async Task<HttpResponseMessage> PostWithTokenAsync(string action, AntiforgeryForm token, params (string Key, string Value)[] fields)
        {
            var values = fields.Select(field => new KeyValuePair<string, string>(field.Key, field.Value)).ToList();
            values.Add(new(token.FormFieldName, token.RequestToken));
            using var content = new FormUrlEncodedContent(values);
            return await Client.PostAsync("/connection/" + action, content);
        }

        public async ValueTask DisposeAsync()
        {
            Client?.Dispose();
            await _app.DisposeAsync();
            await Harness.DisposeAsync();
        }

        public sealed record AntiforgeryForm(string RequestToken, string FormFieldName, string HeaderName);
    }
}
