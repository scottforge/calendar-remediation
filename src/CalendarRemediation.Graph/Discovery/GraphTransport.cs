using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

public sealed record GraphExchange(string Method, string Endpoint, DateTimeOffset At,
    Guid ClientRequestId, int Status, string? RequestId, string? RetryAfter, long Bytes);

public sealed record GraphResponse(int Status, JsonElement? Body, GraphExchange Exchange);

// The supplied client must have redirects disabled and no retry or hedging handlers.
// Production composition uses CreateHandler. Every call performs exactly one SendAsync.
public sealed class GraphTransport(HttpClient client, Func<CancellationToken, Task<string>> acquireToken)
{
    public const string Root = GraphApi.Root;
    public const string Preferences = "IdType=\"ImmutableId\", outlook.timezone=\"UTC\"";
    private const int MaxResponseBytes = 4 * 1024 * 1024;

    public static HttpClientHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };

    public static Uri ValidateUri(string address) => GraphApi.ValidateUri(address);

    public async Task<GraphResponse> SendAsync(HttpMethod method, string address, string? json = null,
        string? ifMatch = null, Guid? clientRequestId = null, CancellationToken cancellationToken = default)
    {
        var uri = ValidateUri(address);
        using var requestBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestBudget.CancelAfter(TimeSpan.FromSeconds(45));
        var ct = requestBudget.Token;
        var requestId = clientRequestId ?? Guid.NewGuid();
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await acquireToken(ct).ConfigureAwait(false));
        request.Headers.Add("Prefer", Preferences);
        request.Headers.Add("client-request-id", requestId.ToString());
        request.Headers.Add("return-client-request-id", "true");
        if (ifMatch is not null)
        {
            if (!EntityTagHeaderValue.TryParse(ifMatch, out var tag) || tag.Tag == "*")
            {
                throw new ArgumentException("An exact entity tag is required.", nameof(ifMatch));
            }
            request.Headers.IfMatch.Add(tag);
        }
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        var at = DateTimeOffset.UtcNow;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        GraphExchange Exchange(long bytes) => new(method.Method, uri.GetLeftPart(UriPartial.Path), at, requestId,
            (int)response.StatusCode, response.Headers.TryGetValues("request-id", out var ids) ? ids.FirstOrDefault() : null,
            response.Headers.RetryAfter?.ToString(), bytes);
        // The feasibility mutation needs status and independent readback, not a response body.
        // Preserve a definitive acceptance/rejection even if a body is malformed or stalls.
        if (method != HttpMethod.Get)
        {
            return new((int)response.StatusCode, null, Exchange(0));
        }
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new InvalidDataException("Graph response exceeded the bounded response size.");
            }
            buffer.Write(chunk, 0, read);
        }
        JsonElement? body = null;
        // Error bodies can contain calendar data. Retain status and correlation only.
        if (response.IsSuccessStatusCode && buffer.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(buffer.ToArray());
                body = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Preserve HTTP status even if a successful response has unusable JSON.
                // Reads fail closed on a missing body, while a stale accepted PATCH is still a guard failure.
                body = null;
            }
        }
        return new((int)response.StatusCode, body, Exchange(buffer.Length));
    }
}
