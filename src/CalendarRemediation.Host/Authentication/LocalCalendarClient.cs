using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Host.Authentication;

public sealed partial class LocalCalendarClient(ILogger<LocalCalendarClient> logger, HttpClient? client = null) : ILocalCalendarClient, IDisposable
{
    private readonly ILogger<LocalCalendarClient> _logger = logger;
    private readonly bool _ownsClient = client is null;
    // Caller-supplied clients retain their own handler pipeline and ownership.
    private readonly HttpClient _client = client ?? new(new GraphDiagnosticsHandler(logger, GraphTransport.CreateHandler())) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<VerifiedMailbox> VerifyAsync(SignInObservation account, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken)
    {
        var transport = new GraphTransport(_client, ct => AcquireTokenAsync(token, ct, cancellationToken));
        var me = await GetAsync(transport, GraphApi.MailboxUrl, "mailbox", cancellationToken);
        var mailboxId = Text(me, "id");
        // Consumer /me IDs are opaque Graph mailbox IDs, not MSAL subject or home-account IDs.
        var consumer = string.Equals(account.TenantId, TenantSignIn.ConsumerTenantId, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(mailboxId)
            || (!consumer && !string.Equals(mailboxId, account.MailboxObjectId, StringComparison.OrdinalIgnoreCase)))
        {
            LogIdentityMismatch(consumer, !string.IsNullOrWhiteSpace(mailboxId), !string.IsNullOrWhiteSpace(account.MailboxObjectId),
                string.Equals(mailboxId, account.MailboxObjectId, StringComparison.OrdinalIgnoreCase));
            throw new ConnectionException(new("identity-mismatch", "The Microsoft 365 mailbox did not match the signed-in account."));
        }
        var calendar = await GetAsync(transport, GraphApi.DefaultCalendarUrl, "default-calendar", cancellationToken);
        var calendarId = Text(calendar, "id");
        var owner = calendar.TryGetProperty("owner", out var value) ? Text(value, "address") : null;
        var mail = Text(me, "mail");
        var upn = Text(me, "userPrincipalName");
        var defaultPresent = calendar.TryGetProperty("isDefaultCalendar", out var isDefault);
        var ownerMatchesMail = !string.IsNullOrWhiteSpace(owner) && string.Equals(owner, mail, StringComparison.OrdinalIgnoreCase);
        var ownerMatchesUpn = !string.IsNullOrWhiteSpace(owner) && string.Equals(owner, upn, StringComparison.OrdinalIgnoreCase);
        var ownerMatchesAccount = consumer && !string.IsNullOrWhiteSpace(owner)
            && string.Equals(owner, account.AccountUsername, StringComparison.OrdinalIgnoreCase);
        var ownerBoundByConsumerMeRoute = consumer && !string.IsNullOrWhiteSpace(owner);
        if (string.IsNullOrWhiteSpace(calendarId) || string.IsNullOrWhiteSpace(owner)
            || !defaultPresent || isDefault.ValueKind != JsonValueKind.True
            || !(ownerMatchesMail || ownerMatchesUpn || ownerMatchesAccount || ownerBoundByConsumerMeRoute))
        {
            LogCalendarMismatch(!string.IsNullOrWhiteSpace(calendarId), !string.IsNullOrWhiteSpace(owner), defaultPresent,
                isDefault.ValueKind.ToString(), isDefault.ValueKind == JsonValueKind.True, ownerMatchesMail, ownerMatchesUpn,
                ownerMatchesAccount, ownerBoundByConsumerMeRoute);
            throw new ConnectionException(new("calendar-mismatch", "The account's default calendar ownership could not be verified."));
        }
        return new(new(account.TenantId, mailboxId!, calendarId), owner, Text(me, "displayName") ?? owner);
    }

    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await new GraphCalendarReader(new(_client, ct => AcquireTokenAsync(token, ct, cancellationToken))).DiscoverAsync(scope, cancellationToken);
            if (!result.Complete && !cancellationToken.IsCancellationRequested)
            {
                LogDiscoveryIncomplete(result);
            }
            return result;
        }
        catch (Exception ex)
        {
            GraphDiagnosticsHandler.LogFailure(_logger, "discovery", ex, cancellationToken);
            throw;
        }
    }

    private async Task<string> AcquireTokenAsync(Func<CancellationToken, Task<string>> token, CancellationToken requestToken,
        CancellationToken callerToken)
    {
        try { return await token(requestToken); }
        catch (Exception ex)
        {
            GraphDiagnosticsHandler.LogFailure(_logger, "token-acquisition", ex, callerToken);
            throw;
        }
    }

    private async Task<JsonElement> GetAsync(GraphTransport transport, string address, string operation, CancellationToken cancellationToken)
    {
        GraphResponse response;
        try { response = await transport.SendAsync(HttpMethod.Get, address, cancellationToken: cancellationToken); }
        catch (Exception ex)
        {
            GraphDiagnosticsHandler.LogFailure(_logger, operation, ex, cancellationToken);
            throw;
        }
        if (response.Status != 200 || response.Body is null || response.Body.Value.ValueKind != JsonValueKind.Object)
        {
            var exchange = response.Exchange;
            var providerRequestId = GraphDiagnosticsHandler.SafeGuid(exchange.RequestId);
            var retryAfter = GraphDiagnosticsHandler.RetryAfter(exchange.RetryAfter);
            var shape = response.Body is null ? response.Status is >= 200 and < 300 && exchange.Bytes > 0 ? "invalid-json" : "missing"
                : response.Body.Value.ValueKind == JsonValueKind.Object ? "object" : "nonobject";
            // The owned HTTP handler already reports non-success status and correlation.
            if (!_ownsClient || response.Status is >= 200 and < 300)
            {
                LogGraphUnavailable(operation, response.Status, exchange.ClientRequestId, providerRequestId,
                    retryAfter, exchange.Bytes, shape);
            }
            throw new ConnectionException(new("graph-unavailable", "Microsoft 365 identity verification was unavailable. Try signing in again."));
        }
        return response.Body.Value;
    }

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Microsoft 365 identity verification unavailable for {Operation}: status {Status}, client request ID {ClientRequestId}, provider request ID {ProviderRequestId}, retry after {RetryAfter}, response bytes {ResponseBytes}, response shape {ResponseShape}")]
    private partial void LogGraphUnavailable(string operation, int status, Guid clientRequestId, string providerRequestId,
        string retryAfter, long responseBytes, string responseShape);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning, Message = "Microsoft 365 identity-mismatch: personal account {PersonalAccount}, Graph mailbox ID present {MailboxIdPresent}, authentication object ID present {AuthObjectIdPresent}, IDs match {MailboxMatchesAuthObjectId}")]
    private partial void LogIdentityMismatch(bool personalAccount, bool mailboxIdPresent, bool authObjectIdPresent, bool mailboxMatchesAuthObjectId);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Warning, Message = "Microsoft 365 calendar-mismatch: calendar ID present {CalendarIdPresent}, owner present {OwnerPresent}, default field present {DefaultPresent}, default value kind {DefaultValueKind}, is default {IsDefault}, owner matches mail {OwnerMatchesMail}, owner matches UPN {OwnerMatchesUpn}, owner matches account {OwnerMatchesAccount}, consumer owner bound by /me route {OwnerBoundByConsumerMeRoute}")]
    private partial void LogCalendarMismatch(bool calendarIdPresent, bool ownerPresent, bool defaultPresent,
        string defaultValueKind, bool isDefault, bool ownerMatchesMail, bool ownerMatchesUpn, bool ownerMatchesAccount,
        bool ownerBoundByConsumerMeRoute);

    private void LogDiscoveryIncomplete(DiscoveryResult result)
    {
        // Holds can contain provider-derived text. Only exact known categories may cross the logging boundary.
        var categories = result.Holds.Select(hold => hold switch
        {
            "identity-mismatch" or "calendar-mismatch" or "duration-budget" or "read-unavailable"
                or "page-budget" or "paging-cycle" or "invalid-next-link" or "event-budget"
                or "invalid-response" or "conflicting-observation" or "detail-budget" or "detail read unavailable" => hold,
            _ => "unknown"
        }).Distinct(StringComparer.Ordinal);
        LogDiscoveryIncomplete(result.Holds.Length, result.Pages, result.DetailReads,
            result.Holds.Contains("duration-budget"), result.Holds.Contains("read-unavailable"), string.Join(", ", categories));
    }

    [LoggerMessage(EventId = 7207, Level = LogLevel.Warning, Message = "Graph discovery incomplete: holds {HoldCount}, pages {Pages}, detail reads {DetailReads}, duration budget exceeded {DurationBudgetExceeded}, read unavailable {ReadUnavailable}, hold categories {HoldCategories}")]
    private partial void LogDiscoveryIncomplete(int holdCount, int pages, int detailReads, bool durationBudgetExceeded, bool readUnavailable,
        string holdCategories);

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public void Dispose()
    {
        if (_ownsClient) { _client.Dispose(); }
    }
}
