using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;
using CalendarRemediation.Host.Authentication;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class ConnectionPreviewTests
{
    private static readonly ConfirmedLocation PreviewLocation = new("SK", "Regina", "America/Regina");

    [Fact]
    public async Task DiscoveredSourceProducesProposalWithoutAdditionalGraphOrTokenRequests()
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = await DiscoverAsync(h, source);
        var principal = Principal(ticket);
        var discovery = (await h.Service.GetStateAsync(principal)).Discovery;
        Assert.Equal(source.Id, Assert.Single(discovery!.OrganizerMeetings).Id);
        h.Service.PreviewMeeting(principal, ticket.Version, source.Id, IntendedStart(source), PreviewLocation.TimeZoneId, false);
        var state = await h.Service.GetStateAsync(principal);
        var preview = Assert.IsType<MeetingTimePreview>(state.Preview);
        Assert.Equal(PreviewDisposition.Proposed, preview.Disposition);
        Assert.Equal(source.Start, preview.CurrentStart);
        Assert.Equal(source.End, preview.CurrentEnd);
        Assert.Equal(source.Start!.Value.Date.AddHours(16).AddMinutes(30), preview.ProposedStart!.Value.UtcDateTime);
        Assert.Equal(source.End - source.Start, preview.ProposedEnd - preview.ProposedStart);
        Assert.Equal(PreviewLocation.TimeZoneId, preview.TimeZoneId);
        Assert.Same(discovery, state.Discovery);
        Assert.Equal(ConnectionPhase.Connected, state.Phase);
        AssertReadOnly(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MatchingLocalTimeAndPreserveInstantKeepOriginalTimes(bool preserveInstant)
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = await DiscoverAsync(h, source);
        var local = TimeZoneInfo.ConvertTime(source.Start!.Value, TimeZoneInfo.FindSystemTimeZoneById(PreviewLocation.TimeZoneId)).DateTime;
        h.Service.PreviewMeeting(Principal(ticket), ticket.Version, source.Id, preserveInstant ? default : local,
            PreviewLocation.TimeZoneId, preserveInstant);
        var preview = Assert.IsType<MeetingTimePreview>((await h.Service.GetStateAsync(Principal(ticket))).Preview);
        Assert.Equal(PreviewDisposition.Unchanged, preview.Disposition);
        Assert.Equal(source.Start, preview.ProposedStart);
        Assert.Equal(source.End, preview.ProposedEnd);
        AssertReadOnly(h);
    }

    [Theory]
    [InlineData("unknown-event", "America/Regina", "invalid-meeting")]
    [InlineData("meeting-a", "America/Vancouver", "invalid-zone")]
    public async Task InvalidSelectionClearsPreviousPreview(string eventId, string zone, string code)
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = await DiscoverAsync(h, source);
        Preview(h, ticket, source);
        Assert.NotNull((await h.Service.GetStateAsync(Principal(ticket))).Preview);
        AssertProblem(code, () => h.Service.PreviewMeeting(Principal(ticket), ticket.Version, eventId, IntendedStart(source), zone, false));
        Assert.Null((await h.Service.GetStateAsync(Principal(ticket))).Preview);
        AssertReadOnly(h);
    }

    [Fact]
    public async Task InvalidLocationPreservesCurrentLocationDiscoveryAndPreview()
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = await DiscoverAsync(h, source);
        var principal = Principal(ticket);
        Preview(h, ticket, source);
        var before = await h.Service.GetStateAsync(principal);

        AssertProblem("invalid-location", () => h.Service.ConfirmLocation(principal, ticket.Version,
            new("AB", "Calgary", "America/Vancouver")));

        var after = await h.Service.GetStateAsync(principal);
        Assert.Equal(before.Location, after.Location);
        Assert.Same(before.Discovery, after.Discovery);
        Assert.Same(before.Preview, after.Preview);
        AssertReadOnly(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrIncompleteDiscoveryCannotProducePreview(bool incomplete)
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = incomplete ? await DiscoverAsync(h, source, false) : await h.ConnectAsync();
        if (!incomplete) { h.Service.ConfirmLocation(Principal(ticket), ticket.Version, PreviewLocation); }
        AssertProblem("discovery-required", () => Preview(h, ticket, source));
        Assert.Null((await h.Service.GetStateAsync(Principal(ticket))).Preview);
        Assert.Equal(incomplete ? 1 : 0, h.Calendar.Discoveries);
        Assert.Empty(h.Identity.TokenAccounts);
    }

    [Fact]
    public async Task PreviewRequiresCurrentVerifiedSession()
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = await DiscoverAsync(h, source);
        AssertProblem("stale-session", () => h.Service.PreviewMeeting(new ClaimsPrincipal(), ticket.Version,
            source.Id, IntendedStart(source), PreviewLocation.TimeZoneId, false));
        AssertProblem("stale-session", () => h.Service.PreviewMeeting(Principal(ticket), "old-version",
            source.Id, IntendedStart(source), PreviewLocation.TimeZoneId, false));
        AssertProblem("sign-in-required", () => h.Service.PreviewMeeting(Principal(ticket, false), ticket.Version,
            source.Id, IntendedStart(source), PreviewLocation.TimeZoneId, false));
        Assert.Null((await h.Service.GetStateAsync(Principal(ticket))).Preview);
        AssertReadOnly(h);
    }

    [Theory]
    [InlineData("location")]
    [InlineData("rediscover")]
    [InlineData("cancel")]
    [InlineData("signout")]
    public async Task SessionChangesInvalidatePreviewAndPreventReuseOfOldDiscovery(string change)
    {
        await using var h = new ConnectionHarness();
        var source = Meeting();
        var ticket = await DiscoverAsync(h, source);
        var principal = Principal(ticket);
        Preview(h, ticket, source);
        Assert.NotNull((await h.Service.GetStateAsync(principal)).Preview);
        var operationRevision = (await h.Service.GetStateAsync(principal)).OperationRevision;
        var gate = new Gate<DiscoveryResult>();
        switch (change)
        {
            case "location":
                h.Service.ConfirmLocation(principal, ticket.Version, new("AB", "Calgary", "America/Edmonton"));
                break;
            case "rediscover":
                h.Calendar.Discover = (_, ct) => gate.RunAsync(ct);
                h.Service.StartDiscovery(principal, ticket.Version);
                await gate.WaitForEntryAsync();
                break;
            case "cancel": h.Service.Cancel(principal, ticket.Version, operationRevision); break;
            case "signout": await h.Service.SignOutAsync(principal, ticket.Version); break;
        }
        Assert.Null((await h.Service.GetStateAsync(principal)).Preview);
        AssertProblem(change == "signout" ? "stale-session" : "discovery-required", () => Preview(h, ticket, source));
        if (change == "rediscover")
        {
            var scope = h.Calendar.Scope!;
            gate.Result.SetResult(new(scope, true, [source], [], [], 1, 0));
            await h.WaitForPhaseAsync(principal, ConnectionPhase.Connected);
            Assert.Null((await h.Service.GetStateAsync(principal)).Preview);
            Preview(h, ticket, source);
            Assert.NotNull((await h.Service.GetStateAsync(principal)).Preview);
        }
    }

    [Theory]
    [InlineData("local", "yyyy-MM-dd'T'HH:mm")]
    [InlineData("local", "yyyy-MM-dd'T'HH:mm:ss")]
    [InlineData("instant", null)]
    public async Task HttpPreviewAcceptsLocalFormatsAndInstantWithoutLocalTime(string mode, string? format)
    {
        await using var host = await PreviewHost.StartAsync();
        using var response = await host.PostAsync(host.Fields(mode, format is null ? "" : IntendedStart(host.Source).ToString(format, CultureInfo.InvariantCulture)));
        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/connect", response.Headers.Location?.OriginalString);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var preview = Assert.IsType<MeetingTimePreview>((await host.StateAsync()).Preview);
        Assert.Equal(mode == "instant" ? PreviewDisposition.Unchanged : PreviewDisposition.Proposed, preview.Disposition);
        Assert.Equal(host.Source.Start, preview.CurrentStart);
        AssertReadOnly(host.Harness);
    }

    [Theory]
    [InlineData("LOCAL", "2026-01-01T10:30", "invalid-mode")]
    [InlineData("", "2026-01-01T10:30", "invalid-mode")]
    [InlineData("local", "", "invalid-time")]
    [InlineData("local", "2026-01-01T10:30Z", "invalid-time")]
    [InlineData("local", "2026-01-01T10:30:00-06:00", "invalid-time")]
    [InlineData("local", "2026-02-30T10:30", "invalid-time")]
    public async Task HttpMalformedInputClearsPreviousPreview(string mode, string localStart, string code)
    {
        await using var host = await PreviewHost.StartAsync();
        Preview(host.Harness, host.Ticket, host.Source);
        Assert.NotNull((await host.StateAsync()).Preview);
        using var response = await host.PostAsync(host.Fields(mode, localStart));
        await AssertProblemAsync(response, HttpStatusCode.Conflict, code);
        Assert.Null((await host.StateAsync()).Preview);
        AssertReadOnly(host.Harness);
    }

    [Fact]
    public async Task HttpPreviewRequiresAntiforgeryAndSameOriginBeforeChangingState()
    {
        await using var host = await PreviewHost.StartAsync();
        Preview(host.Harness, host.Ticket, host.Source);
        using (var missingToken = await host.Client.PostAsync("/connection/preview", new FormUrlEncodedContent(host.Fields())))
        {
            await AssertProblemAsync(missingToken, HttpStatusCode.BadRequest, "invalid-form");
        }
        host.Client.DefaultRequestHeaders.Remove("Origin");
        using (var missingOrigin = await host.PostAsync(host.Fields()))
        {
            await AssertProblemAsync(missingOrigin, HttpStatusCode.Forbidden, "invalid-origin");
        }
        Assert.NotNull((await host.StateAsync()).Preview);
        AssertReadOnly(host.Harness);
    }

    [Fact]
    public async Task LivePageResponsesAreNoStore()
    {
        await using var host = await PreviewHost.StartAsync();
        await host.AssertLiveRouteResponsesAreNoStoreAsync();
    }

    private static SourceSnapshot Meeting()
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1).AddHours(12), TimeSpan.Zero);
        return new(Mailbox.Identity, "meeting-a", "v1", "etag-a", start, start.AddHours(1),
            new(start.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), "UTC"),
            new(start.AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), "UTC"),
            true, Mailbox.Address, [new("attendee@example.test", AttendeeKind.Internal)], false, false,
            EventKind.SingleInstance, null, new("UTC", "UTC", "UTC"), []);
    }

    private static DateTime IntendedStart(SourceSnapshot source) => source.Start!.Value.Date.AddHours(10).AddMinutes(30);

    private static async Task<ConnectionTicket> DiscoverAsync(ConnectionHarness h, SourceSnapshot source, bool complete = true)
    {
        h.Calendar.Discover = (scope, _) => Task.FromResult(new DiscoveryResult(scope, complete, [source], [],
            complete ? [] : ["page-budget"], 1, 0));
        var ticket = await h.ConnectAsync();
        h.Service.ConfirmLocation(Principal(ticket), ticket.Version, PreviewLocation);
        h.Service.StartDiscovery(Principal(ticket), ticket.Version);
        await h.WaitForPhaseAsync(Principal(ticket), ConnectionPhase.Connected);
        return ticket;
    }

    private static void Preview(ConnectionHarness h, ConnectionTicket ticket, SourceSnapshot source) =>
        h.Service.PreviewMeeting(Principal(ticket), ticket.Version, source.Id, IntendedStart(source), PreviewLocation.TimeZoneId, false);

    private static void AssertReadOnly(ConnectionHarness h)
    {
        Assert.Equal(1, h.Calendar.Verifications);
        Assert.Equal(1, h.Calendar.Discoveries);
        Assert.Single(h.Identity.Modes);
        Assert.Empty(h.Identity.TokenAccounts);
    }

    private static void AssertProblem(string code, Action action) => Assert.Equal(code, Assert.Throws<ConnectionException>(action).Problem.Code);

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<ConnectionProblem>())?.Code);
    }

    private sealed class PreviewHost(WebApplication app, ConnectionHarness harness, ConnectionTicket ticket, SourceSnapshot source) : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        public ConnectionHarness Harness { get; } = harness;
        public ConnectionTicket Ticket { get; } = ticket;
        public SourceSnapshot Source { get; } = source;
        public HttpClient Client { get; private set; } = null!;

        public static async Task<PreviewHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            builder.Services.AddLocalConnection(builder.Configuration);
            builder.Services.AddAntiforgery();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            var h = new ConnectionHarness();
            builder.Services.AddSingleton<ILocalIdentityClient>(h.Identity);
            builder.Services.AddSingleton<ILocalCalendarClient>(h.Calendar);
            builder.Services.AddSingleton(h.Service);
            var app = builder.Build();
            app.UseLocalConnectionGuard();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapGet("/", () => Results.Ok());
            app.MapGet("/connect", () => Results.Ok());
            app.MapLocalConnection();
            try
            {
                await app.StartAsync();
                var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
                var source = Meeting();
                var ticket = await DiscoverAsync(h, source);
                // Use the production cookie format while keeping sign-in outside each preview assertion.
                var options = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(ConnectionEndpoints.Scheme);
                var authentication = new AuthenticationTicket(Principal(ticket), new(), ConnectionEndpoints.Scheme);
                var cookies = new CookieContainer();
                cookies.Add(new Uri(address), new Cookie(options.Cookie.Name!, options.TicketDataFormat.Protect(authentication), "/"));
                var host = new PreviewHost(app, h, ticket, source)
                {
                    Client = new(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies })
                    {
                        BaseAddress = new Uri(address),
                        Timeout = TimeSpan.FromSeconds(10)
                    }
                };
                host.Client.DefaultRequestHeaders.Host = "localhost:5009";
                host.Client.DefaultRequestHeaders.Add("Origin", "http://localhost:5009");
                return host;
            }
            catch
            {
                await app.DisposeAsync();
                await h.DisposeAsync();
                throw;
            }
        }

        public Dictionary<string, string> Fields(string mode = "local", string? localStart = null) => new()
        {
            ["version"] = Ticket.Version,
            ["eventId"] = Source.Id,
            ["timeZoneId"] = PreviewLocation.TimeZoneId,
            ["mode"] = mode,
            ["localStart"] = localStart ?? IntendedStart(Source).ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture)
        };

        public async Task<HttpResponseMessage> PostAsync(Dictionary<string, string> fields)
        {
            var token = (await Client.GetFromJsonAsync<AntiforgeryForm>("/connection/antiforgery"))!;
            fields.Add(token.FormFieldName, token.RequestToken);
            using var content = new FormUrlEncodedContent(fields);
            return await Client.PostAsync("/connection/preview", content);
        }

        public async Task<ConnectionState> StateAsync() => (await Client.GetFromJsonAsync<ConnectionState>("/connection/state", Json))!;

        public async Task AssertLiveRouteResponsesAreNoStoreAsync()
        {
            foreach (var path in new[] { "/", "/connect" })
            {
                using var response = await Client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.Contains(response.Headers.Pragma, value => value.Name == "no-cache");
            }
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
