using System.Globalization;
using System.Text.Json;

namespace CalendarRemediation.Graph;

internal static class GraphEventPatch
{
    public static string Serialize(DateTimeOffset start, DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new ArgumentException("The event end must be after its start.", nameof(end));
        }
        return JsonSerializer.Serialize(new
        {
            start = new { dateTime = start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end = new { dateTime = end.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), timeZone = "UTC" }
        });
    }
}
