using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

public sealed class GraphCalendarReader(GraphTransport transport) : ICalendarReader
{
    public async Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(scope.Identity);
        if (scope.Start.Offset != TimeSpan.Zero || scope.End.Offset != TimeSpan.Zero || scope.ExecutionCutoff.Offset != TimeSpan.Zero
            || scope.End <= scope.Start || scope.End - scope.Start > TimeSpan.FromDays(366)
            || scope.Budget.Events is < 1 or > 10000 || scope.Budget.Pages is < 1 or > 1000 || scope.Budget.Details is < 1 or > 10000
            || scope.Budget.Duration <= TimeSpan.Zero || scope.Budget.Duration > TimeSpan.FromHours(1))
        {
            throw new ArgumentException("Explicit UTC scope and bounded positive budgets are required.", nameof(scope));
        }
        var state = new DiscoveryState(scope);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(scope.Budget.Duration);
        var ct = budget.Token;
        try
        {
            var identityHold = await CheckIdentityAsync(scope.Identity, ct).ConfigureAwait(false);
            if (identityHold is not null) { state.AddHold(identityHold); }
            else { await ReadCalendarViewAsync(scope, state, ct).ConfigureAwait(false); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { state.AddHold("duration-budget"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or UriFormatException)
        {
            state.AddHold("read-unavailable");
        }
        return state.Snapshot();
    }

    private async Task ReadCalendarViewAsync(DiscoveryScope scope, DiscoveryState state, CancellationToken ct)
    {
        foreach (var zoneUrl in new[] { GraphApi.WindowsTimeZonesUrl, GraphApi.IanaTimeZonesUrl })
        {
            var zonePage = await GetAsync(zoneUrl, ct).ConfigureAwait(false);
            if (!GraphJson.TryArray(zonePage, "value", out var aliases) || aliases.GetArrayLength() == 0
                || zonePage.TryGetProperty("@odata.nextLink", out _)) { throw new InvalidDataException(); }
            // Enumerate incrementally so aliases preceding malformed data remain in partial results.
            state.AddZones(ReadZoneAliases(aliases));
        }

        string? next = GraphApi.CalendarView(scope.Start, scope.End);
        var exhausted = false;
        while (next is not null && !exhausted && state.AdmitPage(next) == DiscoveryPageAction.ReadPage)
        {
            var page = await GetAsync(next, ct).ConfigureAwait(false);
            state.RecordPageFetched();
            if (!GraphJson.TryArray(page, "value", out var values)) { throw new InvalidDataException(); }
            foreach (var observation in values.EnumerateArray())
            {
                var decision = state.AdmitObservation(observation);
                if (decision.Action == DiscoveryObservationAction.Skip) { continue; }
                if (decision.Action == DiscoveryObservationAction.Stop) { exhausted = true; break; }
                var source = decision.Source!;
                SourceSnapshot? detail;
                try { detail = await ReadEventAsync(scope.Identity, source.Id, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException) { detail = null; }
                state.RecordDetail(DiscoveryDetailDecision.Reconcile(source, detail));
            }
            var continuation = DiscoveryContinuation.Parse(page);
            if (continuation.Kind == DiscoveryContinuationKind.Invalid) { state.AddHold("invalid-next-link"); break; }
            next = continuation.Address;
        }
    }

    private static IEnumerable<string> ReadZoneAliases(JsonElement aliases)
    {
        foreach (var zone in aliases.EnumerateArray())
        {
            var alias = GraphJson.Text(zone, "alias");
            if (string.IsNullOrWhiteSpace(alias)) { throw new InvalidDataException(); }
            yield return alias;
        }
    }

    public async Task<SourceSnapshot?> ReadAsync(CalendarIdentity identity, string eventId,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (eventId is "." or ".." || eventId.Any(char.IsControl)) { throw new ArgumentException("Invalid event identity.", nameof(eventId)); }
        if (await CheckIdentityAsync(identity, cancellationToken).ConfigureAwait(false) is not null) { return null; }
        return await ReadEventAsync(identity, eventId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SourceSnapshot?> ReadEventAsync(CalendarIdentity identity, string eventId, CancellationToken ct)
    {
        var response = await transport.SendAsync(HttpMethod.Get, GraphApi.CalendarEventDetail(eventId), cancellationToken: ct).ConfigureAwait(false);
        if (response.Status != 200 || response.Body is null) { return null; }
        var mapped = GraphEventMapper.Map(identity, response.Body.Value);
        return mapped?.Id == eventId ? mapped : null;
    }

    private async Task<string?> CheckIdentityAsync(CalendarIdentity identity, CancellationToken ct)
    {
        // Token tenant validation belongs to the token-acquisition composition. Graph /me
        // independently establishes the mailbox and default-calendar ownership here.
        var me = await GetAsync(GraphApi.IdentityUrl, ct).ConfigureAwait(false);
        if (GraphJson.Text(me, "id") != identity.MailboxId) { return "identity-mismatch"; }
        var calendar = await GetAsync(GraphApi.DefaultCalendarUrl, ct).ConfigureAwait(false);
        var owner = calendar.TryGetProperty("owner", out var ownerValue) ? GraphJson.Text(ownerValue, "address") : null;
        if (GraphJson.Text(calendar, "id") != identity.CalendarId || GraphJson.Boolean(calendar, "isDefaultCalendar") != true || string.IsNullOrWhiteSpace(owner)
            || !(string.Equals(owner, GraphJson.Text(me, "mail"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(owner, GraphJson.Text(me, "userPrincipalName"), StringComparison.OrdinalIgnoreCase)))
        {
            return "calendar-mismatch";
        }
        return null;
    }

    private async Task<JsonElement> GetAsync(string address, CancellationToken ct)
    {
        var response = await transport.SendAsync(HttpMethod.Get, address, cancellationToken: ct).ConfigureAwait(false);
        if (response.Status != 200 || response.Body is not { ValueKind: JsonValueKind.Object }) { throw new InvalidDataException(); }
        return response.Body.Value;
    }

    private static void ValidateIdentity(CalendarIdentity identity)
    {
        if (!Guid.TryParse(identity.TenantId, out _) || !Guid.TryParse(identity.MailboxId, out _) || string.IsNullOrWhiteSpace(identity.CalendarId))
        {
            throw new ArgumentException("An explicit tenant, mailbox, and calendar are required.", nameof(identity));
        }
    }
}
