using System.Net;
using CalendarRemediation.Graph.Discovery;
using CalendarRemediation.Host.Authentication;
using Microsoft.Extensions.Logging;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class GraphDiagnosticsHandlerTests
{
    private const string ClientId = "135b9920-4fb6-4cf3-abce-768e5cd99712";
    private const string ProviderId = "d21d1065-9434-465e-9a0d-27a6c5c12b51";

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task FailedExchangesLogOriginalCorrelationAndBoundedHeaders(int status)
    {
        var logger = new RecordingLogger();
        using var inner = new ResponseHandler(status, ProviderId, "999999999");
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, inner));
        using var request = Request("calendar/events/private-event-secret?token=private-query-secret");

        using var response = await http.SendAsync(request);

        Assert.Equal(status, (int)response.StatusCode);
        var entry = AssertStartedAndFailure(logger);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(7204, entry.EventId.Id);
        Assert.Equal("event-detail", entry.Fields["Operation"]);
        Assert.Equal("GET", entry.Fields["Method"]);
        Assert.Equal(status, entry.Fields["Status"]);
        Assert.Equal(ClientId, entry.Fields["ClientRequestId"]);
        Assert.Equal(ProviderId, entry.Fields["ProviderRequestId"]);
        Assert.Equal("86400", entry.Fields["RetryAfter"]);
        Assert.True(Assert.IsType<double>(entry.Fields["ElapsedMilliseconds"]) >= 0);
        AssertPrivate(entry);
        Assert.Equal(1, inner.Calls);
    }

    [Theory]
    [InlineData("", "mailbox")]
    [InlineData("calendar", "default-calendar")]
    [InlineData("calendarView?$skiptoken=secret", "calendar-view")]
    [InlineData("outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Windows')", "windows-time-zones")]
    [InlineData("outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')", "iana-time-zones")]
    [InlineData("events/secret", "event-detail")]
    [InlineData("calendars/secret/events/secret", "other-graph")]
    public async Task RoutesAreFixedLabelsWithoutIdentifiersOrQueries(string path, string operation)
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, new ResponseHandler(403, null, null)));
        using var request = Request(path);

        using var response = await http.SendAsync(request);

        var entry = AssertStartedAndFailure(logger);
        Assert.Equal(operation, entry.Fields["Operation"]);
        AssertPrivate(entry);
    }

    [Theory]
    [InlineData("private-id-secret", "private-retry-secret", "unavailable", "unavailable")]
    [InlineData("{d21d1065-9434-465e-9a0d-27a6c5c12b51}", "-1", "unavailable", "unavailable")]
    [InlineData("D21D1065-9434-465E-9A0D-27A6C5C12B51", "120", ProviderId, "120")]
    public async Task UntrustedCorrelationAndRetryHeadersAreSanitized(string provider, string retry, string expectedProvider, string expectedRetry)
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, new ResponseHandler(429, provider, retry)));
        using var request = Request("calendar");
        request.Headers.Remove("client-request-id");
        request.Headers.TryAddWithoutValidation("client-request-id", "private-client-secret");

        using var response = await http.SendAsync(request);

        var entry = AssertStartedAndFailure(logger);
        Assert.Equal("unavailable", entry.Fields["ClientRequestId"]);
        Assert.Equal(expectedProvider, entry.Fields["ProviderRequestId"]);
        Assert.Equal(expectedRetry, entry.Fields["RetryAfter"]);
        AssertPrivate(entry);
    }

    [Fact]
    public async Task OversizedHeaderAndUnknownMethodAreNotLoggedVerbatim()
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, new ResponseHandler(429, new string('x', 1000), new string('9', 1000))));
        using var request = Request("calendar");
        request.Method = new HttpMethod("PRIVATE-SECRET");

        using var response = await http.SendAsync(request);

        var entry = AssertStartedAndFailure(logger);
        Assert.Equal("other", entry.Fields["Method"]);
        Assert.Equal("unavailable", entry.Fields["ProviderRequestId"]);
        Assert.Equal("unavailable", entry.Fields["RetryAfter"]);
        AssertPrivate(entry);
    }

    [Theory]
    [InlineData("network", LogLevel.Warning, "network")]
    [InlineData("io", LogLevel.Warning, "response-read")]
    [InlineData("timeout", LogLevel.Warning, "timeout")]
    [InlineData("unexpected", LogLevel.Error, "unexpected")]
    public async Task TransportExceptionsLogClassificationAndPreserveException(string failure, LogLevel level, string classification)
    {
        var exception = failure switch
        {
            "network" => (Exception)new HttpRequestException("private-secret"),
            "io" => new IOException("private-secret"),
            "timeout" => new TaskCanceledException("private-secret"),
            _ => new InvalidOperationException("private-secret")
        };
        var logger = new RecordingLogger();
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, new ThrowingHandler(exception)));
        using var request = Request("events/private-secret");

        var actual = await Record.ExceptionAsync(() => http.SendAsync(request));

        Assert.NotNull(actual);
        if (exception is not OperationCanceledException) { Assert.Same(exception, actual); }
        var entry = AssertStartedAndFailure(logger);
        Assert.Equal(level, entry.Level);
        Assert.Equal(classification, entry.Fields["Failure"]);
        Assert.Equal(ClientId, entry.Fields["ClientRequestId"]);
        Assert.Equal("GET", entry.Fields["Method"]);
        Assert.True(Assert.IsType<double>(entry.Fields["ElapsedMilliseconds"]) >= 0);
        AssertPrivate(entry);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationDoesNotDuplicateHandlerExceptionWarning(bool timeout)
    {
        var logger = new RecordingLogger();
        Exception exception = timeout ? new TaskCanceledException("secret") : new HttpRequestException("secret");
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, new ThrowingHandler(exception)));
        using var client = new LocalCalendarClient(logger, http);

        var actual = await Record.ExceptionAsync(() => client.VerifyAsync(ConnectionHarness.Account,
            _ => Task.FromResult("token-secret"), CancellationToken.None));

        Assert.NotNull(actual);
        AssertStartedAndFailure(logger);
    }

    [Fact]
    public async Task TransportUsesGeneratedRequestIdRatherThanProviderEcho()
    {
        var logger = new RecordingLogger();
        using var inner = new ResponseHandler(403, ProviderId, null);
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, inner));
        var transport = new GraphTransport(http, _ => Task.FromResult("private-token-secret"));

        var result = await transport.SendAsync(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/calendar/events/event-secret?$select=secret");

        var entry = AssertStartedAndFailure(logger);
        Assert.Equal(result.Exchange.ClientRequestId.ToString("D"), entry.Fields["ClientRequestId"]);
        AssertPrivate(entry);
    }

    [Theory]
    [InlineData(200, ProviderId)]
    [InlineData(204, "private-provider-secret")]
    public async Task SuccessLogsSafeInformationWithoutWarnings(int status, string providerId)
    {
        var logger = new RecordingLogger();
        using var http = new HttpClient(new GraphDiagnosticsHandler(logger, new ResponseHandler(status, providerId, null)));
        using var request = Request("calendar/events/private-event-secret?$select=private-query-secret");

        using var response = await http.SendAsync(request);

        Assert.True(response.IsSuccessStatusCode);
        var completed = Assert.Single(logger.Entries);
        Assert.Equal(7209, completed.EventId.Id);
        Assert.Equal(status, completed.Fields["Status"]);
        Assert.True(Assert.IsType<double>(completed.Fields["ElapsedMilliseconds"]) >= 0);
        Assert.Equal(LogLevel.Information, completed.Level);
        Assert.Equal("event-detail", completed.Fields["Operation"]);
        AssertPrivate(completed);
    }

    [Fact]
    public async Task CallerCancellationLogsStartButNoWarningOrCompletion()
    {
        var logger = new RecordingLogger();
        using var cancellation = new CancellationTokenSource();
        using var canceledHttp = new HttpClient(new GraphDiagnosticsHandler(logger, new CancelingHandler(cancellation)));
        using var canceledRequest = Request("calendar");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledHttp.SendAsync(canceledRequest, cancellation.Token));

        Assert.Empty(logger.Entries);
    }

    private static LogEntry AssertStartedAndFailure(RecordingLogger logger)
    {
        var failure = Assert.Single(logger.Entries);
        Assert.True(failure.Level is LogLevel.Warning or LogLevel.Error);
        AssertPrivate(failure);
        return failure;
    }

    private static HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me" + (path.Length == 0 ? "" : "/" + path));
        request.Headers.Add("client-request-id", ClientId);
        request.Headers.Add("Authorization", "Bearer private-token-secret");
        request.Headers.Add("Cookie", "private-cookie-secret");
        request.Headers.Add("X-Account", "owner-secret@example.test");
        return request;
    }

    private static void AssertPrivate(LogEntry entry)
    {
        Assert.Null(entry.Exception);
        var text = entry.Message + string.Join(" ", entry.Fields.Values);
        foreach (var secret in new[] { "secret", "https://", "$select", "Authorization", "Cookie", "private-body", "@example.test" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ResponseHandler(int status, string? provider, string? retry) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private-body-secret") };
            if (provider is not null) { response.Headers.TryAddWithoutValidation("request-id", provider); }
            if (retry is not null) { response.Headers.TryAddWithoutValidation("Retry-After", retry); }
            response.Headers.TryAddWithoutValidation("client-request-id", "provider-echo-secret");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "private-cookie-secret");
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class CancelingHandler(CancellationTokenSource cancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception? Exception, string Message, Dictionary<string, object?> Fields);

    private sealed class RecordingLogger : ILogger<LocalCalendarClient>
    {
        public List<LogEntry> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, eventId, exception, formatter(state, exception),
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value)));
    }
}
