using System.Collections.Immutable;
using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

internal static class GraphEventMapper
{
    internal static SourceSnapshot? Map(CalendarIdentity identity, JsonElement item)
    {
        var id = GraphJson.Text(item, "id");
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || id.Any(char.IsControl)) { return null; }
        var unresolved = ImmutableArray.CreateBuilder<string>();
        var version = GraphJson.Text(item, "changeKey");
        var guard = GraphJson.Text(item, "@odata.etag");
        if (string.IsNullOrWhiteSpace(version)) { unresolved.Add("changeKey-unknown"); }
        if (string.IsNullOrWhiteSpace(guard)) { unresolved.Add("@odata.etag-unknown"); }
        if (!EntityTagHeaderValue.TryParse(guard, out var tag) || tag.Tag == "*") { unresolved.Add("guard-unknown"); }
        var start = GraphEventParser.ParseTime(item, "start");
        var end = GraphEventParser.ParseTime(item, "end");
        unresolved.AddRange(start.Unresolved);
        unresolved.AddRange(end.Unresolved);
        if (start.Instant is not null && end.Instant <= start.Instant) { unresolved.Add("duration-invalid"); }
        var organizer = item.TryGetProperty("organizer", out var org) ? GraphEventParser.ReadEmailAddress(org) : null;
        if (string.IsNullOrWhiteSpace(organizer)) { unresolved.Add("organizer-unknown"); }
        var attendees = GraphEventParser.ParseAttendees(item);
        var series = GraphEventParser.ParseSeriesIdentity(item);
        unresolved.AddRange(attendees.Unresolved);
        unresolved.AddRange(series.Unresolved);
        var originalStart = GraphJson.Text(item, "originalStartTimeZone");
        var originalEnd = GraphJson.Text(item, "originalEndTimeZone");
        if (string.IsNullOrWhiteSpace(originalStart)) { unresolved.Add("originalStartTimeZone-unknown"); }
        if (string.IsNullOrWhiteSpace(originalEnd)) { unresolved.Add("originalEndTimeZone-unknown"); }
        var isOrganizer = GraphJson.Boolean(item, "isOrganizer");
        var allDay = GraphJson.Boolean(item, "isAllDay");
        var cancelled = GraphJson.Boolean(item, "isCancelled");
        if (isOrganizer is null) { unresolved.Add("isOrganizer-unknown"); }
        if (allDay is null) { unresolved.Add("isAllDay-unknown"); }
        if (cancelled is null) { unresolved.Add("isCancelled-unknown"); }
        return new(identity, id, version, guard, start.Instant, end.Instant, start.Raw, end.Raw, isOrganizer, organizer,
            attendees.Values, allDay, cancelled, series.Kind, series.MasterId, new(originalStart, originalEnd, "UTC"),
            [.. unresolved.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }
}
