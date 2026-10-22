using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

public sealed class GraphCalendarReader(GraphTransport transport) : ICalendarReader
{
    private const string ConsumerTenantId = "9188040d-6c67-4c5b-b112-36a304b66dad";

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
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
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
            if (zonePage is not { } page || !GraphJson.TryArray(page, "value", out var aliases) || aliases.GetArrayLength() == 0
                || page.TryGetProperty("@odata.nextLink", out _)) { state.AddHold("read-unavailable"); return; }
            // Enumerate incrementally so aliases preceding malformed data remain in partial results.
            foreach (var zone in aliases.EnumerateArray())
            {
                var alias = GraphJson.Text(zone, "alias");
                if (string.IsNullOrWhiteSpace(alias)) { state.AddHold("read-unavailable"); return; }
                state.AddZones([alias]);
            }
        }

        string? next = GraphApi.CalendarView(scope.Start, scope.End);
        var exhausted = false;
        while (next is not null && !exhausted && state.AdmitPage(next) == DiscoveryPageAction.ReadPage)
        {
            if (await GetAsync(next, ct).ConfigureAwait(false) is not { } page) { state.AddHold("read-unavailable"); return; }
            state.RecordPageFetched();
            if (!GraphJson.TryArray(page, "value", out var values)) { state.AddHold("read-unavailable"); return; }
            foreach (var observation in values.EnumerateArray())
            {
                var decision = state.AdmitObservation(observation);
                if (decision.Action == DiscoveryObservationAction.Skip) { continue; }
                if (decision.Action == DiscoveryObservationAction.Stop) { exhausted = true; break; }
                var source = decision.Source!;
                SourceSnapshot? detail;
                try { detail = await ReadEventAsync(scope.Identity, source.Id, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException) { detail = null; }
                state.RecordDetail(DiscoveryDetailDecision.Reconcile(source, detail));
            }
            var continuation = DiscoveryContinuation.Parse(page);
            if (continuation.Kind == DiscoveryContinuationKind.Invalid) { state.AddHold("invalid-next-link"); break; }
            next = continuation.Address;
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
        if (await GetAsync(GraphApi.IdentityUrl, ct).ConfigureAwait(false) is not { } me) { return "read-unavailable"; }
        if (GraphJson.Text(me, "id") != identity.MailboxId) { return "identity-mismatch"; }
        if (await GetAsync(GraphApi.DefaultCalendarUrl, ct).ConfigureAwait(false) is not { } calendar) { return "read-unavailable"; }
        var owner = calendar.TryGetProperty("owner", out var ownerValue) ? GraphJson.Text(ownerValue, "address") : null;
        var consumer = string.Equals(identity.TenantId, ConsumerTenantId, StringComparison.OrdinalIgnoreCase);
        var ownerBoundByConsumerMeRoute = consumer && !string.IsNullOrWhiteSpace(owner);
        if (GraphJson.Text(calendar, "id") != identity.CalendarId || GraphJson.Boolean(calendar, "isDefaultCalendar") != true || string.IsNullOrWhiteSpace(owner)
            || !(ownerBoundByConsumerMeRoute
                || string.Equals(owner, GraphJson.Text(me, "mail"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(owner, GraphJson.Text(me, "userPrincipalName"), StringComparison.OrdinalIgnoreCase)))
        {
            return "calendar-mismatch";
        }
        return null;
    }

    private async Task<JsonElement?> GetAsync(string address, CancellationToken ct)
    {
        var response = await transport.SendAsync(HttpMethod.Get, address, cancellationToken: ct).ConfigureAwait(false);
        if (response.Status != 200 || response.Body is not { ValueKind: JsonValueKind.Object }) { return null; }
        return response.Body.Value;
    }

    private static void ValidateIdentity(CalendarIdentity identity)
    {
        if (!Guid.TryParse(identity.TenantId, out _) || string.IsNullOrWhiteSpace(identity.MailboxId) || string.IsNullOrWhiteSpace(identity.CalendarId))
        {
            throw new ArgumentException("An explicit tenant, mailbox, and calendar are required.", nameof(identity));
        }
    }
}
