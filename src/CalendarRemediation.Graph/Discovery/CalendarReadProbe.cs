using System.Diagnostics;
using System.Globalization;
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
    public const string EventFields = "id,changeKey,start,end,originalStartTimeZone,originalEndTimeZone,isOrganizer,organizer,attendees,isAllDay,isCancelled,type,seriesMasterId";
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

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
        var events = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var views = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var zones = new List<JsonElement>();
        string? calendarId = null;
        int pages = 0, details = 0, observations = 0;

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

        try
        {
            var me = await GetAsync(GraphTransport.Root + "me?$select=id,mail,userPrincipalName").ConfigureAwait(false);
            if (Text(me, "id") != options.MailboxId)
            {
                throw new InvalidDataException("Mailbox identity did not match the configured object ID.");
            }
            var calendar = await GetAsync(GraphTransport.Root + "me/calendar?$select=id,isDefaultCalendar,owner").ConfigureAwait(false);
            calendarId = Text(calendar, "id");
            var owner = calendar.GetProperty("owner").GetProperty("address").GetString();
            if (string.IsNullOrEmpty(calendarId) || !calendar.GetProperty("isDefaultCalendar").GetBoolean()
                || string.IsNullOrEmpty(owner) || !(string.Equals(owner, Text(me, "mail"), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(owner, Text(me, "userPrincipalName"), StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Default calendar ownership could not be established.");
            }
            foreach (var standard in new[] { "Windows", "Iana" })
            {
                var zonePage = await GetAsync(GraphTransport.Root + $"me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'{standard}')").ConfigureAwait(false);
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
            string? next = GraphTransport.Root + "me/calendarView?startDateTime="
                + Uri.EscapeDataString(options.Start.ToString("O", CultureInfo.InvariantCulture)) + "&endDateTime="
                + Uri.EscapeDataString(options.End.ToString("O", CultureInfo.InvariantCulture)) + "&$top=100&$select=" + EventFields;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (next is not null)
            {
                if (pages >= options.MaxPages || !visited.Add(next))
                {
                    throw new InvalidDataException("Page budget exhausted or continuation cycle observed.");
                }
                var uri = GraphTransport.ValidateUri(next);
                if (uri.AbsolutePath != "/v1.0/me/calendarView")
                {
                    throw new InvalidDataException("Continuation escaped the calendar-view endpoint.");
                }
                var page = await GetAsync(next).ConfigureAwait(false);
                pages++;
                foreach (var item in page.GetProperty("value").EnumerateArray())
                {
                    if (++observations > options.MaxEvents)
                    {
                        throw new InvalidDataException("Observation budget exhausted. Narrow the scope or explicitly increase its budget.");
                    }
                    if (!HasValidSelectedFields(item))
                    {
                        throw new InvalidDataException("Missing or malformed required discovery/classification fields in calendar-view observation.");
                    }
                    var id = Text(item, "id")!;
                    if (views.TryGetValue(id, out var previous))
                    {
                        if (!Equivalent(previous, item))
                        {
                            throw new InvalidDataException("Repeated identity has differing version or evidence.");
                        }
                        continue;
                    }
                    views.Add(id, item.Clone());
                    if (details >= options.MaxDetailReads)
                    {
                        throw new InvalidDataException("Detail-read budget exhausted.");
                    }
                    details++;
                    var detail = await GetAsync(GraphTransport.Root + "me/events/" + Uri.EscapeDataString(id) + "?$select=" + EventFields).ConfigureAwait(false);
                    if (!HasValidSelectedFields(detail))
                    {
                        throw new InvalidDataException("Missing or malformed required discovery/classification fields in event detail.");
                    }
                    if (!Equivalent(item, detail))
                    {
                        throw new InvalidDataException("Identity, version or relevant evidence changed between view and detail.");
                    }
                    events.Add(id, detail.Clone());
                }
                next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
                if (page.TryGetProperty("@odata.nextLink", out _) && string.IsNullOrWhiteSpace(next))
                {
                    throw new InvalidDataException("Malformed continuation.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            holds.Add("Run duration exhausted or request timed out. Scope is incomplete.");
        }
        catch (Exception ex) when (ex is InvalidDataException or HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or UriFormatException)
        {
            holds.Add(ex is InvalidDataException ? ex.Message : $"Read failed ({ex.GetType().Name}). Scope is incomplete.");
        }
        var organizerMeetings = events.Values.Count(e => e.GetProperty("isOrganizer").GetBoolean()
            && Text(e, "type") == "singleInstance" && e.GetProperty("attendees").GetArrayLength() > 0);
        return new(started, options, holds.Count == 0, holds.ToArray(), calendarId, pages, details, observations,
            organizerMeetings, timer.Elapsed.TotalSeconds, events.Values.ToArray(), zones.ToArray(), exchanges.ToArray());
    }

    public static string? Text(JsonElement element, string property) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static bool Equivalent(JsonElement first, JsonElement second) => HasValidSelectedFields(first)
        && HasValidSelectedFields(second) && EventFields.Split(',').All(field =>
        {
            var hasFirst = first.TryGetProperty(field, out var a);
            var hasSecond = second.TryGetProperty(field, out var b);
            return hasFirst && hasSecond ? JsonElement.DeepEquals(a, b)
                : field == "seriesMasterId" && !hasFirst && !hasSecond;
        });

    private static bool HasValidSelectedFields(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(Text(item, "id")) || string.IsNullOrWhiteSpace(Text(item, "changeKey"))
            || string.IsNullOrWhiteSpace(Text(item, "originalStartTimeZone")) || string.IsNullOrWhiteSpace(Text(item, "originalEndTimeZone"))
            || !HasDateTimeZone(item, "start") || !HasDateTimeZone(item, "end")
            || !HasBoolean(item, "isOrganizer") || !HasBoolean(item, "isAllDay") || !HasBoolean(item, "isCancelled")
            || !item.TryGetProperty("organizer", out var organizer) || !HasEmailAddress(organizer)
            || !item.TryGetProperty("attendees", out var attendees) || attendees.ValueKind != JsonValueKind.Array
            || attendees.EnumerateArray().Any(a => !HasEmailAddress(a) || Text(a, "type") is not ("required" or "optional" or "resource")))
        {
            return false;
        }
        var type = Text(item, "type");
        if (type is not ("singleInstance" or "occurrence" or "exception" or "seriesMaster"))
        {
            return false;
        }
        // The master ID is not applicable to single instances or masters themselves.
        return type is "occurrence" or "exception" ? !string.IsNullOrWhiteSpace(Text(item, "seriesMasterId"))
            : !item.TryGetProperty("seriesMasterId", out var master) || master.ValueKind == JsonValueKind.Null;
    }

    private static bool HasBoolean(JsonElement item, string field) => item.TryGetProperty(field, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool HasDateTimeZone(JsonElement item, string field) => item.TryGetProperty(field, out var value)
        && !string.IsNullOrWhiteSpace(Text(value, "timeZone"))
        && DateTime.TryParseExact(Text(value, "dateTime"), DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool HasEmailAddress(JsonElement item) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty("emailAddress", out var email) && !string.IsNullOrWhiteSpace(Text(email, "address"));
}
