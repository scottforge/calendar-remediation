using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

internal static class GraphEventParser
{
    private static readonly string[] LocalFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];

    internal static ParsedProviderTime ParseTime(JsonElement item, string propertyName)
    {
        var raw = item.ValueKind == JsonValueKind.Object && item.TryGetProperty(propertyName, out var time)
            ? new ProviderTime(GraphJson.Text(time, "dateTime"), GraphJson.Text(time, "timeZone"))
            : new(null, null);
        DateTimeOffset? instant = null;
        var localText = raw.DateTime;
        if (localText is not null)
        {
            if (localText.EndsWith('Z')) { localText = localText[..^1]; }
            else if (localText.EndsWith("+00:00", StringComparison.Ordinal)) { localText = localText[..^6]; }
        }
        if (raw.Zone == "UTC" && DateTime.TryParseExact(localText, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            instant = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc));
        }
        // A parsed UTC instant does not establish the meeting's intended time zone.
        return new(raw, instant, instant is null ? [propertyName + "-unknown"] : []);
    }

    internal static ParsedAttendees ParseAttendees(JsonElement item)
    {
        if (!GraphJson.TryArray(item, "attendees", out var values))
        {
            return new([], ["attendees-unknown"]);
        }

        var attendees = ImmutableArray.CreateBuilder<Attendee>();
        var unresolved = ImmutableArray.CreateBuilder<string>();
        foreach (var attendee in values.EnumerateArray())
        {
            var address = ReadEmailAddress(attendee);
            // Required/optional and matching email domains do not establish tenant membership.
            var kind = GraphJson.Text(attendee, "type") == "resource" ? AttendeeKind.Resource : AttendeeKind.Unknown;
            attendees.Add(new(address ?? "", kind));
            if (kind == AttendeeKind.Unknown || string.IsNullOrWhiteSpace(address)) { unresolved.Add("attendee-membership-unknown"); }
        }
        return new(attendees.ToImmutable(), unresolved.ToImmutable());
    }

    internal static ParsedSeriesIdentity ParseSeriesIdentity(JsonElement item)
    {
        var kind = GraphJson.Text(item, "type") switch
        {
            "singleInstance" => EventKind.SingleInstance,
            "occurrence" => EventKind.Occurrence,
            "exception" => EventKind.Exception,
            "seriesMaster" => EventKind.SeriesMaster,
            _ => EventKind.Unknown
        };
        var unresolved = ImmutableArray.CreateBuilder<string>();
        if (kind == EventKind.Unknown) { unresolved.Add("event-kind-unknown"); }
        var master = GraphJson.Text(item, "seriesMasterId");
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("seriesMasterId", out var masterValue)
            && masterValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
        {
            unresolved.Add("series-identity-unknown");
        }
        if ((kind is EventKind.Occurrence or EventKind.Exception && string.IsNullOrWhiteSpace(master))
            || (kind is EventKind.SingleInstance or EventKind.SeriesMaster && master is not null))
        {
            unresolved.Add("series-identity-unknown");
        }
        return new(kind, master, unresolved.ToImmutable());
    }

    internal static string? ReadEmailAddress(JsonElement value) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("emailAddress", out var email) ? GraphJson.Text(email, "address") : null;
}

internal sealed record ParsedProviderTime(ProviderTime Raw, DateTimeOffset? Instant, ImmutableArray<string> Unresolved);

internal sealed record ParsedAttendees(ImmutableArray<Attendee> Values, ImmutableArray<string> Unresolved);

internal sealed record ParsedSeriesIdentity(EventKind Kind, string? MasterId, ImmutableArray<string> Unresolved);
