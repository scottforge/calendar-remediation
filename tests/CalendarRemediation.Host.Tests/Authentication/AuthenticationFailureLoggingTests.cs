using System.Security.Claims;
using CalendarRemediation.Host.Authentication;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static CalendarRemediation.Host.Tests.Authentication.ConnectionHarness;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class AuthenticationFailureLoggingTests
{
    private const string Sensitive = "private@example.test secret-token private-tenant";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExternalIdentityOperationsLogSafeStartedAndCompletedEventsWithFreshOperationScopes(bool interactive, bool personal)
    {
        var logger = new FailureLogger<LocalConnectionService>();
        var account = Account with
        {
            TenantId = personal ? "9188040d-6c67-4c5b-b112-36a304b66dad" : Sensitive,
            ClientId = Sensitive,
            MailboxObjectId = Sensitive,
            HomeAccountId = Sensitive,
            AuthenticationPath = Sensitive,
            CacheProtection = Sensitive,
            Scopes = [Sensitive]
        };
        var identity = new FakeIdentity
        {
            Authenticate = (_, _) => Task.FromResult(account),
            Acquire = (_, _) => Task.FromResult(Sensitive)
        };
        var calendar = new FakeCalendar();
        calendar.Verify = async (_, ct) =>
        {
            Assert.Equal(Sensitive, await calendar.VerificationToken!(ct));
            return Mailbox;
        };
        calendar.Discover = async (scope, ct) =>
        {
            Assert.Equal(Sensitive, await calendar.DiscoveryToken!(ct));
            return new(scope, true, [], [], [], 1, 0);
        };
        await using var service = new LocalConnectionService(identity, calendar, logger);
        var bootstrap = service.Bootstrap(new());
        var pending = interactive ? service.StartBrowserSignIn(Principal(bootstrap), bootstrap.Version, ReturnUri)
            : service.StartSignIn(Principal(bootstrap), bootstrap.Version);
        await ConnectionFailureLoggingTests.WaitAsync(service, pending, ConnectionPhase.AwaitingCompletion);
        var connected = service.Complete(Principal(pending), pending.Version);
        await service.GetStateAsync(Principal(connected));
        await service.GetStateAsync(Principal(connected));
        var entries = logger.Entries.ToArray();
        Assert.Equal(4, entries.Length);
        var operation = interactive ? "interactive-authorization" : "silent-continuation";
        var accountType = personal ? "personal" : "configured-organization";
        Assert.Contains(operation + " started", entries[0].Message);
        Assert.Equal("unverified", entries[0].Fields["AccountType"]);
        Assert.Contains(operation + " completed", entries[1].Message);
        Assert.Contains("token-acquisition started", entries[2].Message);
        Assert.Contains("token-acquisition completed", entries[3].Message);
        var operationId = Assert.IsType<Guid>(entries[0].OperationId);
        Assert.NotEqual(Guid.Empty, operationId);
        foreach (var entry in entries)
        {
            Assert.Equal(operationId, entry.OperationId);
            Assert.Equal(LogLevel.Information, entry.Level);
            Assert.Null(entry.Exception);
        }
        foreach (var entry in entries.Skip(1)) { Assert.Equal(accountType, entry.Fields["AccountType"]); }
        foreach (var entry in new[] { entries[1], entries[3] })
        {
            Assert.True(Assert.IsType<long>(entry.Fields["ElapsedMilliseconds"]) >= 0);
        }
        service.ConfirmLocation(Principal(connected), connected.Version, Location);
        service.StartDiscovery(Principal(connected), connected.Version);
        await ConnectionFailureLoggingTests.WaitAsync(service, connected, ConnectionPhase.Connected);
        var discovery = logger.Entries.ToArray()[4..];
        Assert.Equal(2, discovery.Length);
        Assert.Contains("token-acquisition started", discovery[0].Message);
        Assert.Contains("token-acquisition completed", discovery[1].Message);
        Assert.NotNull(discovery[0].OperationId);
        Assert.NotEqual(operationId, discovery[0].OperationId);
        Assert.Equal(discovery[0].OperationId, discovery[1].OperationId);
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(Sensitive, entry.Message);
            Assert.DoesNotContain(Sensitive, string.Join(" ", entry.Fields.Values));
            Assert.DoesNotContain(connected.Id, entry.Message);
            Assert.DoesNotContain(connected.Version, entry.Message);
            Assert.DoesNotContain(account.TenantId, entry.Message);
            Assert.DoesNotContain(Mailbox.Address, entry.Message);
            Assert.DoesNotContain(AuthorizationUri.AbsoluteUri, entry.Message);
        }
    }

    [Fact]
    public async Task FailedTokenAcquisitionSharesTheStartedOperationScopeAndHasNoSuccessfulCompletion()
    {
        var logger = new FailureLogger<LocalConnectionService>();
        var identity = new FakeIdentity
        {
            Acquire = (_, _) => Task.FromException<string>(new Microsoft.Identity.Client.MsalUiRequiredException(Sensitive, Sensitive))
        };
        var calendar = new FakeCalendar();
        calendar.Verify = async (_, ct) =>
        {
            await calendar.VerificationToken!(ct);
            return Mailbox;
        };
        await using var service = new LocalConnectionService(identity, calendar, logger);
        var bootstrap = service.Bootstrap(new());
        var pending = service.StartSignIn(Principal(bootstrap), bootstrap.Version);
        var state = await ConnectionFailureLoggingTests.WaitAsync(service, pending, ConnectionPhase.Failed);
        Assert.Equal("interaction-required", state.Problem?.Code);
        var warning = Assert.Single(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        var started = Assert.Single(logger.Entries, entry => entry.Message.Contains("token-acquisition started", StringComparison.Ordinal));
        Assert.NotNull(started.OperationId);
        Assert.Equal(started.OperationId, warning.OperationId);
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains("token-acquisition completed", StringComparison.Ordinal));
        foreach (var entry in logger.Entries)
        {
            Assert.DoesNotContain(Sensitive, entry.Message);
            Assert.Null(entry.Exception);
        }
    }

    [Fact]
    public void InvalidConfigurationIsReportedOnceWithoutConfigurationValues()
    {
        var logger = new FailureLogger<LocalIdentityClient>();
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(new Provider(logger)));
        using var identity = new LocalIdentityClient(Options.Create(new LocalConnectionOptions { TenantId = Sensitive, ClientId = Sensitive }), factory);
        Assert.Equal("configuration-required", identity.Unavailable?.Code);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("initialize", entry.Message);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void InvalidProbeConfigurationLogsOnlySafeReasonAndExceptionType()
    {
        var logger = new FailureLogger<TenantSignIn>();
        Assert.Throws<ArgumentException>(() => new TenantSignIn(new(Sensitive, Sensitive, "", Sensitive), logger));
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("configuration-required", entry.Message);
        Assert.Contains(nameof(ArgumentException), entry.Message);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.Null(entry.Exception);
    }

    [Theory]
    [InlineData("origin", "invalid-origin")]
    [InlineData("antiforgery", "antiforgery-rejected")]
    [InlineData("form", "invalid-form")]
    [InlineData("stale", "stale-session")]
    [InlineData("cache-io", "account-cache-unavailable")]
    [InlineData("cache-crypto", "account-cache-unavailable")]
    [InlineData("unexpected", "unexpected-fault")]
    [InlineData("antiforgery-get", "antiforgery-unavailable")]
    public async Task RejectedBrowserActionsLogFixedCategoriesWithoutRequestContents(string scenario, string reason)
    {
        var logger = new FailureLogger<LocalIdentityClient>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders().AddProvider(new Provider(logger));
        builder.Services.AddLocalConnection(builder.Configuration);
        var identity = new FakeIdentity();
        identity.Clear = () => Task.FromException(scenario switch
        {
            "cache-io" => new IOException(Sensitive),
            "cache-crypto" => new System.Security.Cryptography.CryptographicException(Sensitive),
            _ => new NotSupportedException(Sensitive)
        });
        builder.Services.AddSingleton<ILocalIdentityClient>(identity);
        builder.Services.AddSingleton<ILocalCalendarClient>(new FakeCalendar());
        builder.Services.AddSingleton<IAntiforgery>(new Antiforgery(scenario == "antiforgery"));
        await using var app = builder.Build();
        app.MapLocalConnection();
        var signout = scenario is "cache-io" or "cache-crypto" or "unexpected";
        var action = signout ? "signout" : scenario == "antiforgery-get" ? "antiforgery" : "complete";
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single(route => route.RoutePattern.RawText == "/connection/" + action);
        var context = new DefaultHttpContext { RequestServices = app.Services };
        context.Request.Scheme = "http";
        context.Request.Host = new("localhost", 5009);
        context.Request.Headers.Origin = scenario == "origin" ? "https://private.example.test" : "http://localhost:5009";
        context.Request.Method = "POST";
        context.Request.ContentType = scenario == "form" ? "text/plain" : "application/x-www-form-urlencoded";
        context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["version"] = Sensitive });
        if (signout)
        {
            var service = app.Services.GetRequiredService<LocalConnectionService>();
            var ticket = service.Bootstrap(new());
            context.User = Principal(ticket);
            context.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues> { ["version"] = ticket.Version });
        }
        if (scenario == "form") { context.Request.ContentLength = 17 * 1024; }
        context.Response.Body = new MemoryStream();
        if (scenario is "unexpected" or "antiforgery-get")
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => endpoint.RequestDelegate!(context));
        }
        else
        {
            await endpoint.RequestDelegate!(context);
            Assert.Equal(signout ? 503 : scenario == "origin" ? 403 : scenario == "stale" ? 409 : 400, context.Response.StatusCode);
        }
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(signout || scenario == "antiforgery-get" ? LogLevel.Error : LogLevel.Warning, entry.Level);
        Assert.Contains(reason, entry.Message);
        Assert.Contains(action, entry.Message);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.DoesNotContain("private.example.test", entry.Message);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public async Task DisallowedLocalRequestLogsOnlyTheRejectionCategory()
    {
        var logger = new FailureLogger<LocalIdentityClient>();
        await using var services = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(new Provider(logger)))
            .Configure<LocalConnectionOptions>(options => options.Port = 5009)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        app.UseLocalConnectionGuard();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Host = new("private.example.test", 5009);
        context.Request.Headers.Origin = Sensitive;
        await app.Build()(context);
        Assert.Equal(403, context.Response.StatusCode);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("local-request-rejected", entry.Message);
        Assert.DoesNotContain("private.example.test", entry.Message);
        Assert.DoesNotContain(Sensitive, entry.Message);
        Assert.Null(entry.Exception);
    }

    private sealed class Provider(ILogger logger) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => categoryName.StartsWith("CalendarRemediation.Host.Authentication", StringComparison.Ordinal)
            ? logger : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public void Dispose() { }
    }

    private sealed class Antiforgery(bool reject) : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => throw new NotSupportedException(Sensitive);
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => throw new NotSupportedException();
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(!reject);
        public void SetCookieTokenAndHeader(HttpContext httpContext) => throw new NotSupportedException();
        public Task ValidateRequestAsync(HttpContext httpContext) => reject
            ? Task.FromException(new AntiforgeryValidationException(Sensitive)) : Task.CompletedTask;
    }
}
