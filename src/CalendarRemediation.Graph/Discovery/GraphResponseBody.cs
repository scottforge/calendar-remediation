using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

internal static class GraphResponseBody
{
    public static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[Math.Min(8192, maxBytes)];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (read > maxBytes - buffer.Length)
            {
                throw new InvalidDataException("Graph response exceeded the bounded response size.");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public static JsonElement? ParseJson(int status, ReadOnlyMemory<byte> content)
    {
        // Error bodies can contain calendar data. Retain status and correlation only.
        if (status is < 200 or >= 300 || content.IsEmpty)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Preserve HTTP status when a successful response has unusable JSON.
            return null;
        }
    }
}
