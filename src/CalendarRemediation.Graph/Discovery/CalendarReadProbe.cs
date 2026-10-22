using System.Diagnostics;
using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

public sealed record ReadProbeOptions(string TenantId, string MailboxId, DateTimeOffset Start, DateTimeOffset End,
    int MaxEvents, int MaxPages, int MaxDetailReads, int MaxSeconds);

public sealed record ReadProbeResult(DateTimeOffset StartedAt, ReadProbeOptions Scope, bool Complete,
    string[] Holds, string? CalendarId, int Pages, int DetailReads, int Observations,
    int OrganizerMeetings, double ElapsedSeconds, JsonElement[] Events,
    JsonElement[] SupportedZones, GraphExchange[] Exchanges);

public sealed class CalendarReadProbe(GraphTransport transport)
{
    public async Task<ReadProbeResult> RunAsync(ReadProbeOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Guid.TryParse(options.TenantId, out _) || !Guid.TryParse(options.MailboxId, out _)
            || options.Start.Offset != TimeSpan.Zero || options.End.Offset != TimeSpan.Zero
            || options.Start >= options.End || options.End - options.Start > TimeSpan.FromDays(366)
            || options.MaxEvents is < 1 or > 10000 || options.MaxPages is < 1 or > 1000
            || options.MaxDetailReads is < 1 or > 10000 || options.MaxSeconds is < 1 or > 3600)
        {
            throw new ArgumentException("Explicit UTC boundaries and bounded positive budgets are required.", nameof(options));
        }
        var started = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(options.MaxSeconds));
        var ct = budget.Token;
        var holds = new List<string>();
        var exchanges = new List<GraphExchange>();
        var state = new ReadProbeState(options);
        var zones = new List<JsonElement>();
        string? calendarId = null;
        var pages = 0;

        async Task<JsonElement?> GetAsync(string path)
        {
            var response = await transport.SendAsync(HttpMethod.Get, path, cancellationToken: ct).ConfigureAwait(false);
            exchanges.Add(response.Exchange);
            if (response.Status != 200 || response.Body is null)
            {
                holds.Add($"Graph read rejected with HTTP {response.Status}. No retry was made.");
                return null;
            }
            if (response.Body.Value.ValueKind != JsonValueKind.Object)
            {
                holds.Add("Malformed Graph read response. Scope is incomplete.");
                return null;
            }
            return response.Body.Value;
        }

        async Task<bool> ReadSupportedZonesAsync()
        {
            foreach (var zoneUrl in new[] { GraphApi.WindowsTimeZonesUrl, GraphApi.IanaTimeZonesUrl })
            {
                if (await GetAsync(zoneUrl).ConfigureAwait(false) is not { } zonePage) { return false; }
                if (zonePage.TryGetProperty("@odata.nextLink", out _))
                {
                    holds.Add("Unexpected supported-zone continuation requires adapter review.");
                    return false;
                }
                zones.Add(zonePage);
                if (!GraphJson.TryArray(zonePage, "value", out var aliases) || aliases.GetArrayLength() == 0)
                {
                    holds.Add("Supported-zone response was empty or malformed.");
                    return false;
                }
            }
            return true;
        }

        async Task ReadCalendarViewAsync()
        {
            string? next = GraphApi.CalendarView(options.Start, options.End);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (next is not null)
            {
                if (pages >= options.MaxPages || !visited.Add(next))
                {
                    holds.Add("Page budget exhausted or continuation cycle observed.");
                    return;
                }
                if (!GraphApi.IsCalendarViewUri(next))
                {
                    holds.Add("Continuation escaped the calendar-view endpoint.");
                    return;
                }
                if (await GetAsync(next).ConfigureAwait(false) is not { } page) { return; }
                pages++;
                if (!GraphJson.TryArray(page, "value", out var values))
                {
                    holds.Add("Malformed calendar-view response.");
                    return;
                }
                foreach (var item in values.EnumerateArray())
                {
                    if (!state.AdmitObservation(item))
                    {
                        if (state.Hold is not null) { return; }
                        continue;
                    }
                    if (await GetAsync(GraphApi.ProbeEventDetail(GraphJson.Text(item, "id")!)).ConfigureAwait(false) is not { } detail) { return; }
                    state.RecordDetail(item, detail);
                    if (state.Hold is not null) { return; }
                }
                var continuation = DiscoveryContinuation.Parse(page);
                if (continuation.Kind == DiscoveryContinuationKind.Invalid)
                {
                    holds.Add("Malformed continuation.");
                    return;
                }
                next = continuation.Address;
            }
        }

        async Task ReadAsync()
        {
            if (await GetAsync(GraphApi.IdentityUrl).ConfigureAwait(false) is not { } me) { return; }
            if (GraphJson.Text(me, "id") != options.MailboxId)
            {
                holds.Add("Mailbox identity did not match the configured object ID.");
                return;
            }
            if (await GetAsync(GraphApi.DefaultCalendarUrl).ConfigureAwait(false) is not { } calendar) { return; }
            calendarId = GraphJson.Text(calendar, "id");
            var owner = calendar.TryGetProperty("owner", out var ownerValue) ? GraphJson.Text(ownerValue, "address") : null;
            if (string.IsNullOrEmpty(calendarId) || GraphJson.Boolean(calendar, "isDefaultCalendar") != true
                || string.IsNullOrEmpty(owner) || !(string.Equals(owner, GraphJson.Text(me, "mail"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(owner, GraphJson.Text(me, "userPrincipalName"), StringComparison.OrdinalIgnoreCase)))
            {
                holds.Add("Default calendar ownership could not be established.");
                return;
            }
            if (!await ReadSupportedZonesAsync().ConfigureAwait(false)) { return; }
            await ReadCalendarViewAsync().ConfigureAwait(false);
        }
        try
        {
            await ReadAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            holds.Add("Run duration exhausted or request timed out. Scope is incomplete.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException)
        {
            holds.Add($"Read failed ({ex.GetType().Name}). Scope is incomplete.");
        }
        if (state.Hold is not null) { holds.Add(state.Hold); }
        return new(started, options, holds.Count == 0, holds.ToArray(), calendarId, pages, state.DetailReads, state.Observations,
            state.OrganizerMeetings, timer.Elapsed.TotalSeconds, state.Events.ToArray(), zones.ToArray(), exchanges.ToArray());
    }

    public static bool Equivalent(JsonElement first, JsonElement second) => ReadProbeValidation.Equivalent(first, second);
}
