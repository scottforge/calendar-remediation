using System.Text.Json;

namespace CalendarRemediation.Graph;

internal static class GraphJson
{
    internal static string? Text(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(key, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static bool? Boolean(JsonElement item, string key) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    internal static bool TryArray(JsonElement item, string key, out JsonElement value)
    {
        value = default;
        return item.ValueKind == JsonValueKind.Object && item.TryGetProperty(key, out value) && value.ValueKind == JsonValueKind.Array;
    }
}
