using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Host.Authentication;

public sealed class LocalCalendarClient : ILocalCalendarClient, IDisposable
{
    private readonly HttpClient _client = new(GraphTransport.CreateHandler()) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<VerifiedMailbox> VerifyAsync(SignInObservation account, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken)
    {
        var transport = new GraphTransport(_client, token);
        var me = await GetAsync(transport, GraphApi.MailboxUrl, cancellationToken);
        var mailboxId = Text(me, "id");
        if (!string.Equals(mailboxId, account.MailboxObjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ConnectionException(new("identity-mismatch", "The Microsoft 365 mailbox did not match the signed-in account."));
        }
        var calendar = await GetAsync(transport, GraphApi.DefaultCalendarUrl, cancellationToken);
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

    private static async Task<JsonElement> GetAsync(GraphTransport transport, string address, CancellationToken cancellationToken)
    {
        var response = await transport.SendAsync(HttpMethod.Get, address, cancellationToken: cancellationToken);
        if (response.Status != 200 || response.Body is null || response.Body.Value.ValueKind != JsonValueKind.Object)
        {
            throw new ConnectionException(new("graph-unavailable", "Microsoft 365 identity verification was unavailable. Try signing in again."));
        }
        return response.Body.Value;
    }

    private static string? Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    public void Dispose() => _client.Dispose();
}
