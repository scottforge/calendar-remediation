using System.Globalization;
using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

internal static class ReadProbeValidation
{
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    internal static bool IsValid(JsonElement item) => item.ValueKind == JsonValueKind.Object
        && HasIdentityAndTiming(item) && HasClassification(item) && HasParticipants(item);

    internal static bool Equivalent(JsonElement first, JsonElement second) => IsValid(first)
        && IsValid(second) && GraphApi.EventFields.Split(',').All(field =>
        {
            var hasFirst = first.TryGetProperty(field, out var a);
            var hasSecond = second.TryGetProperty(field, out var b);
            return hasFirst && hasSecond ? JsonElement.DeepEquals(a, b)
                : field == "seriesMasterId" && !hasFirst && !hasSecond;
        });

    private static bool HasIdentityAndTiming(JsonElement item) =>
        GraphJson.Text(item, "id") is { } id && !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") && !id.Any(char.IsControl)
        && !string.IsNullOrWhiteSpace(GraphJson.Text(item, "changeKey"))
        && !string.IsNullOrWhiteSpace(GraphJson.Text(item, "originalStartTimeZone"))
        && !string.IsNullOrWhiteSpace(GraphJson.Text(item, "originalEndTimeZone"))
        && HasDateTimeZone(item, "start") && HasDateTimeZone(item, "end");

    private static bool HasClassification(JsonElement item)
    {
        if (!HasBoolean(item, "isOrganizer") || !HasBoolean(item, "isAllDay") || !HasBoolean(item, "isCancelled"))
        {
            return false;
        }

        return GraphJson.Text(item, "type") switch
        {
            "occurrence" or "exception" => !string.IsNullOrWhiteSpace(GraphJson.Text(item, "seriesMasterId")),
            // The master ID is not applicable to single instances or masters themselves.
            "singleInstance" or "seriesMaster" => !item.TryGetProperty("seriesMasterId", out var master)
                || master.ValueKind == JsonValueKind.Null,
            _ => false
        };
    }

    private static bool HasParticipants(JsonElement item) =>
        item.TryGetProperty("organizer", out var organizer)
        && !string.IsNullOrWhiteSpace(GraphEventParser.ReadEmailAddress(organizer))
        && item.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array
        && attendees.EnumerateArray().All(a => !string.IsNullOrWhiteSpace(GraphEventParser.ReadEmailAddress(a))
            && GraphJson.Text(a, "type") is "required" or "optional" or "resource");

    private static bool HasBoolean(JsonElement item, string field) => item.TryGetProperty(field, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool HasDateTimeZone(JsonElement item, string field)
    {
        if (!item.TryGetProperty(field, out var value)) { return false; }
        var zone = GraphJson.Text(value, "timeZone");
        var dateTime = GraphJson.Text(value, "dateTime");
        if (string.IsNullOrWhiteSpace(zone) || dateTime is null) { return false; }
        if (zone == "UTC") { return GraphEventParser.ParseTime(item, field).Instant is not null; }
        return DateTime.TryParseExact(dateTime, DateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
