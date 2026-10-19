using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using MsalException = Microsoft.Identity.Client.MsalException;

namespace CalendarRemediation.Host.Authentication;

public sealed partial class GraphDiagnosticsHandler(ILogger logger, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    private static readonly object LoggedFailure = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var operation = Route(request.RequestUri);
        var method = Method(request.Method);
        var clientRequestId = HeaderGuid(request.Headers, "client-request-id");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                LogHttpFailure(logger, operation, method, (int)response.StatusCode,
                    clientRequestId, HeaderGuid(response.Headers, "request-id"),
                    RetryAfter(response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null),
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            else if (logger.IsEnabled(LogLevel.Information))
            {
                var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LogRequestCompleted(logger, operation, (int)response.StatusCode, elapsedMilliseconds);
            }
            return response;
        }
        catch (Exception ex)
        {
            LogFailure(logger, operation, ex, cancellationToken, method,
                clientRequestId, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    internal static void LogFailure(ILogger logger, string operation, Exception exception, CancellationToken cancellationToken,
        string method = "unavailable", string clientRequestId = "unavailable", double elapsedMilliseconds = 0)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested) { return; }
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data.Contains(LoggedFailure)) { return; }
        }
        var failure = exception switch
        {
            MsalException => "token",
            OperationCanceledException => "timeout",
            HttpRequestException => "network",
            IOException => "response-read",
            InvalidDataException => "invalid-response",
            JsonException => "invalid-json",
            _ => "unexpected"
        };
        // The same exception can cross both the HTTP and verification boundaries.
        exception.Data[LoggedFailure] = true;
        if (failure == "unexpected") { LogUnexpectedFailure(logger, operation, failure, method, clientRequestId, elapsedMilliseconds); }
        else { LogExpectedFailure(logger, operation, failure, method, clientRequestId, elapsedMilliseconds); }
    }

    internal static string SafeGuid(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out var id)
        ? id.ToString("D") : "unavailable";

    internal static string RetryAfter(string? value)
    {
        if (value is not { Length: <= 128 } || !RetryConditionHeaderValue.TryParse(value, out var retry)) { return "unavailable"; }
        return retry.Delta is { } delta
            ? Math.Clamp(delta.TotalSeconds, 0, 86400).ToString(CultureInfo.InvariantCulture)
            : retry.Date!.Value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string HeaderGuid(HttpHeaders headers, string name) => headers.TryGetValues(name, out var values)
        ? SafeGuid(values.FirstOrDefault()) : "unavailable";

    private static string Method(HttpMethod method) => method.Method switch
    {
        "GET" or "POST" or "PATCH" or "PUT" or "DELETE" or "HEAD" or "OPTIONS" => method.Method,
        _ => "other"
    };

    private static string Route(Uri? uri) => uri?.AbsolutePath switch
    {
        "/v1.0/me" => "mailbox",
        "/v1.0/me/calendar" => "default-calendar",
        "/v1.0/me/calendarView" => "calendar-view",
        "/v1.0/me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Windows')" => "windows-time-zones",
        "/v1.0/me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')" => "iana-time-zones",
        { } path when path.StartsWith("/v1.0/me/calendar/events/", StringComparison.Ordinal)
            || path.StartsWith("/v1.0/me/events/", StringComparison.Ordinal) => "event-detail",
        _ => "other-graph"
    };

    [LoggerMessage(EventId = 7204, Level = LogLevel.Warning, Message = "Graph HTTP failure for {Operation}: method {Method}, status {Status}, client request ID {ClientRequestId}, provider request ID {ProviderRequestId}, retry after {RetryAfter}, elapsed milliseconds {ElapsedMilliseconds}")]
    private static partial void LogHttpFailure(ILogger logger, string operation, string method, int status,
        string clientRequestId, string providerRequestId, string retryAfter, double elapsedMilliseconds);

    [LoggerMessage(EventId = 7205, Level = LogLevel.Warning, Message = "Graph operation {Operation} failed: {Failure}, method {Method}, client request ID {ClientRequestId}, elapsed milliseconds {ElapsedMilliseconds}")]
    private static partial void LogExpectedFailure(ILogger logger, string operation, string failure, string method,
        string clientRequestId, double elapsedMilliseconds);

    [LoggerMessage(EventId = 7206, Level = LogLevel.Error, Message = "Graph operation {Operation} failed unexpectedly: {Failure}, method {Method}, client request ID {ClientRequestId}, elapsed milliseconds {ElapsedMilliseconds}")]
    private static partial void LogUnexpectedFailure(ILogger logger, string operation, string failure, string method,
        string clientRequestId, double elapsedMilliseconds);

    [LoggerMessage(EventId = 7209, Level = LogLevel.Information, Message = "Graph {Operation} completed: {Status} in {ElapsedMilliseconds} ms")]
    private static partial void LogRequestCompleted(ILogger logger, string operation, int status, double elapsedMilliseconds);
}
