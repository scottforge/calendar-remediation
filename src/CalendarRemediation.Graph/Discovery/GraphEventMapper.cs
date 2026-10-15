using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

internal static class GraphEventMapper
{
    private static readonly string[] LocalFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    internal static SourceSnapshot? Map(CalendarIdentity identity, JsonElement item)
    {
        var id = GraphJson.Text(item, "id");
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(char.IsControl)) { return null; }
        var unresolved = ImmutableArray.CreateBuilder<string>();
        string? RequiredText(string key)
        {
            var text = GraphJson.Text(item, key);
            if (string.IsNullOrWhiteSpace(text)) { unresolved.Add(key + "-unknown"); }
            return text;
        }
        bool? Flag(string key)
        {
            var value = GraphJson.Boolean(item, key);
            if (value is null) { unresolved.Add(key + "-unknown"); }
            return value;
        }
        (ProviderTime Raw, DateTimeOffset? Instant) Time(string key)
        {
            var raw = item.TryGetProperty(key, out var time) ? new ProviderTime(GraphJson.Text(time, "dateTime"), GraphJson.Text(time, "timeZone")) : new(null, null);
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
            && org.TryGetProperty("emailAddress", out var email) ? GraphJson.Text(email, "address") : null;
        if (string.IsNullOrWhiteSpace(organizer)) { unresolved.Add("organizer-unknown"); }
        var attendees = ImmutableArray.CreateBuilder<Attendee>();
        if (GraphJson.TryArray(item, "attendees", out var values))
        {
            foreach (var attendee in values.EnumerateArray())
            {
                var address = attendee.ValueKind == JsonValueKind.Object && attendee.TryGetProperty("emailAddress", out var attendeeEmail)
                    ? GraphJson.Text(attendeeEmail, "address") : null;
                var type = GraphJson.Text(attendee, "type");
                // Required/optional and matching email domains do not establish tenant membership.
                var kind = type == "resource" ? AttendeeKind.Resource : AttendeeKind.Unknown;
                attendees.Add(new(address ?? "", kind));
                if (kind == AttendeeKind.Unknown || string.IsNullOrWhiteSpace(address)) { unresolved.Add("attendee-membership-unknown"); }
            }
        }
        else { unresolved.Add("attendees-unknown"); }
        var kindValue = GraphJson.Text(item, "type") switch
        {
            "singleInstance" => EventKind.SingleInstance,
            "occurrence" => EventKind.Occurrence,
            "exception" => EventKind.Exception,
            "seriesMaster" => EventKind.SeriesMaster,
            _ => EventKind.Unknown
        };
        if (kindValue == EventKind.Unknown) { unresolved.Add("event-kind-unknown"); }
        var master = GraphJson.Text(item, "seriesMasterId");
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
