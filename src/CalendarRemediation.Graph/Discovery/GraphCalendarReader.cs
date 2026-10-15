using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

public sealed class GraphCalendarReader(GraphTransport transport) : ICalendarReader
{
    private static readonly string[] LocalFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

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
        var holds = ImmutableArray.CreateBuilder<string>();
        var zones = new HashSet<string>(StringComparer.Ordinal);
        var views = new Dictionary<string, SourceSnapshot>(StringComparer.Ordinal);
        var events = new Dictionary<string, SourceSnapshot>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pages = 0;
        var details = 0;
        var observations = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(scope.Budget.Duration);
        var ct = budget.Token;
        try
        {
            var identityHold = await CheckIdentityAsync(scope.Identity, ct).ConfigureAwait(false);
            if (identityHold is not null) { holds.Add(identityHold); }
            else
            {
                foreach (var zoneUrl in new[] { GraphApi.WindowsTimeZonesUrl, GraphApi.IanaTimeZonesUrl })
                {
                    var zonePage = await GetAsync(zoneUrl, ct).ConfigureAwait(false);
                    if (!TryArray(zonePage, "value", out var values) || values.GetArrayLength() == 0 || zonePage.TryGetProperty("@odata.nextLink", out _))
                    {
                        throw new InvalidDataException();
                    }
                    foreach (var zone in values.EnumerateArray())
                    {
                        var alias = Text(zone, "alias");
                        if (string.IsNullOrWhiteSpace(alias)) { throw new InvalidDataException(); }
                        zones.Add(alias);
                    }
                }
                string? next = GraphApi.CalendarView(scope.Start, scope.End);
                var exhausted = false;
                while (next is not null && !exhausted)
                {
                    if (pages >= scope.Budget.Pages) { holds.Add("page-budget"); break; }
                    if (!visited.Add(next)) { holds.Add("paging-cycle"); break; }
                    if (!GraphApi.IsCalendarViewUri(next))
                    {
                        holds.Add("invalid-next-link");
                        break;
                    }
                    var page = await GetAsync(next, ct).ConfigureAwait(false);
                    pages++;
                    if (!TryArray(page, "value", out var values)) { throw new InvalidDataException(); }
                    foreach (var observation in values.EnumerateArray())
                    {
                        if (++observations > scope.Budget.Events) { holds.Add("event-budget"); exhausted = true; break; }
                        var source = Map(scope.Identity, observation);
                        if (source is null) { holds.Add("invalid-response"); continue; }
                        if (views.TryGetValue(source.Id, out var previous))
                        {
                            if (!Equivalent(previous, source)) { holds.Add("conflicting-observation"); }
                            continue;
                        }
                        views.Add(source.Id, source);
                        if (details >= scope.Budget.Details) { holds.Add("detail-budget"); exhausted = true; break; }
                        details++;
                        try
                        {
                            var detail = await ReadEventAsync(scope.Identity, source.Id, ct).ConfigureAwait(false);
                            if (detail is null)
                            {
                                holds.Add("detail read unavailable");
                                events.Add(source.Id, source with { Unresolved = source.Unresolved.Add("detail-read-unavailable") });
                            }
                            else
                            {
                                if (!Equivalent(source, detail)) { holds.Add("conflicting-observation"); }
                                events.Add(source.Id, detail);
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
                        {
                            holds.Add("detail read unavailable");
                            events.Add(source.Id, source with { Unresolved = source.Unresolved.Add("detail-read-unavailable") });
                        }
                    }
                    if (page.TryGetProperty("@odata.nextLink", out var link))
                    {
                        next = link.ValueKind == JsonValueKind.String ? link.GetString() : null;
                        if (string.IsNullOrWhiteSpace(next)) { holds.Add("invalid-next-link"); break; }
                    }
                    else { next = null; }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { holds.Add("duration-budget"); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException or UriFormatException)
        {
            holds.Add("read-unavailable");
        }
        return new(scope, holds.Count == 0, [.. events.Values.OrderBy(e => e.Id, StringComparer.Ordinal)],
            [.. zones.Order(StringComparer.Ordinal)], [.. holds.Distinct(StringComparer.Ordinal)], pages, details);
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
        var mapped = Map(identity, response.Body.Value);
        return mapped?.Id == eventId ? mapped : null;
    }

    private async Task<string?> CheckIdentityAsync(CalendarIdentity identity, CancellationToken ct)
    {
        // Token tenant validation belongs to the token-acquisition composition. Graph /me
        // independently establishes the mailbox and default-calendar ownership here.
        var me = await GetAsync(GraphApi.IdentityUrl, ct).ConfigureAwait(false);
        if (Text(me, "id") != identity.MailboxId) { return "identity-mismatch"; }
        var calendar = await GetAsync(GraphApi.DefaultCalendarUrl, ct).ConfigureAwait(false);
        var owner = calendar.TryGetProperty("owner", out var ownerValue) ? Text(ownerValue, "address") : null;
        if (Text(calendar, "id") != identity.CalendarId || Boolean(calendar, "isDefaultCalendar") != true || string.IsNullOrWhiteSpace(owner)
            || !(string.Equals(owner, Text(me, "mail"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(owner, Text(me, "userPrincipalName"), StringComparison.OrdinalIgnoreCase)))
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

    private static bool Equivalent(SourceSnapshot a, SourceSnapshot b) => SourceSnapshotEvidence.Matches(a, b);
    private static string? Text(JsonElement item, string key) => CalendarReadProbe.Text(item, key);
    private static bool? Boolean(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static bool TryArray(JsonElement item, string key, out JsonElement value)
    {
        value = default;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Array;
    }

    private static SourceSnapshot? Map(CalendarIdentity identity, JsonElement item)
    {
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(char.IsControl)) { return null; }
        var unresolved = ImmutableArray.CreateBuilder<string>();
        string? RequiredText(string key)
        {
            var text = Text(item, key);
            if (string.IsNullOrWhiteSpace(text)) { unresolved.Add(key + "-unknown"); }
            return text;
        }
        bool? Flag(string key)
        {
            var value = Boolean(item, key);
            if (value is null) { unresolved.Add(key + "-unknown"); }
            return value;
        }
        (ProviderTime Raw, DateTimeOffset? Instant) Time(string key)
        {
            var raw = item.TryGetProperty(key, out var time) ? new ProviderTime(Text(time, "dateTime"), Text(time, "timeZone")) : new(null, null);
            DateTimeOffset? instant = null;
            if (raw.Zone == "UTC" && DateTime.TryParseExact(raw.DateTime, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            {
                instant = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc));
            }
            else if (raw.Zone == "UTC" && raw.DateTime is { } text && (text.EndsWith('Z') || text.EndsWith("+00:00", StringComparison.Ordinal))
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var utc) && utc.Offset == TimeSpan.Zero)
            {
                instant = utc;
            }
            if (instant is null) { unresolved.Add(key + "-unknown"); }
            return (raw, instant);
        }
        var version = RequiredText("changeKey");
        var guard = RequiredText("@odata.etag");
        if (!EntityTagHeaderValue.TryParse(guard, out var tag) || tag.Tag == "*") { unresolved.Add("guard-unknown"); }
        var start = Time("start");
        var end = Time("end");
        if (start.Instant is not null && end.Instant <= start.Instant) { unresolved.Add("duration-invalid"); }
        var organizer = item.TryGetProperty("organizer", out var org) && org.ValueKind == JsonValueKind.Object
            && org.TryGetProperty("emailAddress", out var email) ? Text(email, "address") : null;
        if (string.IsNullOrWhiteSpace(organizer)) { unresolved.Add("organizer-unknown"); }
        var attendees = ImmutableArray.CreateBuilder<Attendee>();
        if (TryArray(item, "attendees", out var values))
        {
            foreach (var attendee in values.EnumerateArray())
            {
                var address = attendee.ValueKind == JsonValueKind.Object && attendee.TryGetProperty("emailAddress", out var attendeeEmail)
                    ? Text(attendeeEmail, "address") : null;
                var type = Text(attendee, "type");
                // Required/optional and matching email domains do not establish tenant membership.
                var kind = type == "resource" ? AttendeeKind.Resource : AttendeeKind.Unknown;
                attendees.Add(new(address ?? "", kind));
                if (kind == AttendeeKind.Unknown || string.IsNullOrWhiteSpace(address)) { unresolved.Add("attendee-membership-unknown"); }
            }
        }
        else { unresolved.Add("attendees-unknown"); }
        var kindValue = Text(item, "type") switch
        {
            "singleInstance" => EventKind.SingleInstance,
            "occurrence" => EventKind.Occurrence,
            "exception" => EventKind.Exception,
            "seriesMaster" => EventKind.SeriesMaster,
            _ => EventKind.Unknown
        };
        if (kindValue == EventKind.Unknown) { unresolved.Add("event-kind-unknown"); }
        var master = Text(item, "seriesMasterId");
        if (item.TryGetProperty("seriesMasterId", out var masterValue) && masterValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            unresolved.Add("series-identity-unknown");
        }
        if ((kindValue is EventKind.Occurrence or EventKind.Exception && string.IsNullOrWhiteSpace(master))
            || (kindValue is EventKind.SingleInstance or EventKind.SeriesMaster && master is not null))
        {
            unresolved.Add("series-identity-unknown");
        }
        var originalStart = RequiredText("originalStartTimeZone");
        var originalEnd = RequiredText("originalEndTimeZone");
        var isOrganizer = Flag("isOrganizer");
        var allDay = Flag("isAllDay");
        var cancelled = Flag("isCancelled");
        return new(identity, id, version, guard, start.Instant, end.Instant, start.Raw, end.Raw, isOrganizer, organizer,
            attendees.ToImmutable(), allDay, cancelled, kindValue, master, new(originalStart, originalEnd, "UTC"),
            [.. unresolved.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }
}
