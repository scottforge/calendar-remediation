using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Host.Authentication;

public sealed partial class LocalCalendarClient(ILogger<LocalCalendarClient> logger, HttpClient? client = null) : ILocalCalendarClient, IDisposable
{
    private readonly ILogger<LocalCalendarClient> _logger = logger;
    private readonly bool _ownsClient = client is null;
    private readonly HttpClient _client = client ?? new(GraphTransport.CreateHandler()) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<VerifiedMailbox> VerifyAsync(SignInObservation account, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken)
    {
        var transport = new GraphTransport(_client, token);
        var me = await GetAsync(transport, GraphApi.MailboxUrl, "mailbox", cancellationToken);
        var mailboxId = Text(me, "id");
        if (!string.Equals(mailboxId, account.MailboxObjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectionException(new("identity-mismatch", "The Microsoft 365 mailbox did not match the signed-in account."));
        }
        var calendar = await GetAsync(transport, GraphApi.DefaultCalendarUrl, "default-calendar", cancellationToken);
        var calendarId = Text(calendar, "id");
        var owner = calendar.TryGetProperty("owner", out var value) ? Text(value, "address") : null;
        var mail = Text(me, "mail");
        var upn = Text(me, "userPrincipalName");
        if (string.IsNullOrWhiteSpace(calendarId) || string.IsNullOrWhiteSpace(owner)
            || !calendar.TryGetProperty("isDefaultCalendar", out var isDefault) || isDefault.ValueKind != JsonValueKind.True
            || !(string.Equals(owner, mail, StringComparison.OrdinalIgnoreCase) || string.Equals(owner, upn, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ConnectionException(new("calendar-mismatch", "The account's default calendar ownership could not be verified."));
        }
        return new(new(account.TenantId, mailboxId!, calendarId), owner, Text(me, "displayName") ?? owner);
    }

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken) => new GraphCalendarReader(new(_client, token)).DiscoverAsync(scope, cancellationToken);

    private async Task<JsonElement> GetAsync(GraphTransport transport, string address, string operation, CancellationToken cancellationToken)
    {
        var response = await transport.SendAsync(HttpMethod.Get, address, cancellationToken: cancellationToken);
        if (response.Status != 200 || response.Body is null || response.Body.Value.ValueKind != JsonValueKind.Object)
        {
            var exchange = response.Exchange;
            var providerRequestId = exchange.RequestId is { Length: 36 } id && Guid.TryParseExact(id, "D", out var parsed)
                ? parsed.ToString("D") : "unavailable";
            var retryAfter = "unavailable";
            if (RetryConditionHeaderValue.TryParse(exchange.RetryAfter, out var retry))
            {
                retryAfter = retry.Delta is { } delta
                    ? Math.Clamp(delta.TotalSeconds, 0, 86400).ToString(CultureInfo.InvariantCulture)
                    : retry.Date!.Value.ToString("R", CultureInfo.InvariantCulture);
            }
            var shape = response.Body is null ? "missing"
                : response.Body.Value.ValueKind == JsonValueKind.Object ? "object" : "nonobject";
            LogGraphUnavailable(operation, response.Status, exchange.ClientRequestId, providerRequestId,
                retryAfter, exchange.Bytes, shape);
            throw new ConnectionException(new("graph-unavailable", "Microsoft 365 identity verification was unavailable. Try signing in again."));
        }
        return response.Body.Value;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Microsoft 365 identity verification unavailable for {Operation}: status {Status}, client request ID {ClientRequestId}, provider request ID {ProviderRequestId}, retry after {RetryAfter}, response bytes {ResponseBytes}, response shape {ResponseShape}")]
    private partial void LogGraphUnavailable(string operation, int status, Guid clientRequestId, string providerRequestId,
        string retryAfter, long responseBytes, string responseShape);

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public void Dispose()
    {
        if (_ownsClient) { _client.Dispose(); }
    }
}
