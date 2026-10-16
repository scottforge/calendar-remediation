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

        async Task<JsonElement> GetAsync(string path)
        {
            var response = await transport.SendAsync(HttpMethod.Get, path, cancellationToken: ct).ConfigureAwait(false);
            exchanges.Add(response.Exchange);
            if (response.Status != 200 || response.Body is null)
            {
                throw new InvalidDataException($"Graph read rejected with HTTP {response.Status}. No retry was made.");
            }
            return response.Body.Value;
        }

        async Task ReadSupportedZonesAsync()
        {
            foreach (var zoneUrl in new[] { GraphApi.WindowsTimeZonesUrl, GraphApi.IanaTimeZonesUrl })
            {
                var zonePage = await GetAsync(zoneUrl).ConfigureAwait(false);
                if (zonePage.TryGetProperty("@odata.nextLink", out _))
                {
                    throw new InvalidDataException("Unexpected supported-zone continuation requires adapter review.");
                }
                zones.Add(zonePage);
                if (zonePage.GetProperty("value").GetArrayLength() == 0)
                {
                    throw new InvalidDataException("Supported-zone response was empty.");
                }
            }
        }

        async Task ReadCalendarViewAsync()
        {
            string? next = GraphApi.CalendarView(options.Start, options.End);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (next is not null)
            {
                if (pages >= options.MaxPages || !visited.Add(next))
                {
                    throw new InvalidDataException("Page budget exhausted or continuation cycle observed.");
                }
                if (!GraphApi.IsCalendarViewUri(next))
                {
                    throw new InvalidDataException("Continuation escaped the calendar-view endpoint.");
                }
                var page = await GetAsync(next).ConfigureAwait(false);
                pages++;
                foreach (var item in page.GetProperty("value").EnumerateArray())
                {
                    if (!state.AdmitObservation(item)) { continue; }
                    var detail = await GetAsync(GraphApi.ProbeEventDetail(GraphJson.Text(item, "id")!)).ConfigureAwait(false);
                    state.RecordDetail(item, detail);
                }
                var continuation = DiscoveryContinuation.Parse(page);
                if (continuation.Kind == DiscoveryContinuationKind.Invalid)
                {
                    throw new InvalidDataException("Malformed continuation.");
                }
                next = continuation.Address;
            }
        }

        try
        {
            var me = await GetAsync(GraphApi.IdentityUrl).ConfigureAwait(false);
            if (GraphJson.Text(me, "id") != options.MailboxId)
            {
                throw new InvalidDataException("Mailbox identity did not match the configured object ID.");
            }
            var calendar = await GetAsync(GraphApi.DefaultCalendarUrl).ConfigureAwait(false);
            calendarId = GraphJson.Text(calendar, "id");
            var owner = calendar.GetProperty("owner").GetProperty("address").GetString();
            if (string.IsNullOrEmpty(calendarId) || !calendar.GetProperty("isDefaultCalendar").GetBoolean()
                || string.IsNullOrEmpty(owner) || !(string.Equals(owner, GraphJson.Text(me, "mail"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(owner, GraphJson.Text(me, "userPrincipalName"), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Default calendar ownership could not be established.");
            }
            await ReadSupportedZonesAsync().ConfigureAwait(false);
            await ReadCalendarViewAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            holds.Add("Run duration exhausted or request timed out. Scope is incomplete.");
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or UriFormatException)
        {
            holds.Add(ex is InvalidDataException ? ex.Message : $"Read failed ({ex.GetType().Name}). Scope is incomplete.");
        }
        return new(started, options, holds.Count == 0, holds.ToArray(), calendarId, pages, state.DetailReads, state.Observations,
            state.OrganizerMeetings, timer.Elapsed.TotalSeconds, state.Events.ToArray(), zones.ToArray(), exchanges.ToArray());
    }

    public static bool Equivalent(JsonElement first, JsonElement second) => ReadProbeValidation.Equivalent(first, second);
}
