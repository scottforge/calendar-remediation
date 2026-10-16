using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

internal enum DiscoveryContinuationKind { End, Next, Invalid }

internal sealed record DiscoveryContinuation(DiscoveryContinuationKind Kind, string? Address = null)
{
    public static DiscoveryContinuation Parse(JsonElement page)
    {
        if (!page.TryGetProperty("@odata.nextLink", out var link)) { return new(DiscoveryContinuationKind.End); }
        var address = link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        return string.IsNullOrWhiteSpace(address)
            ? new(DiscoveryContinuationKind.Invalid)
            : new(DiscoveryContinuationKind.Next, address);
    }
}
