using System.Net;
using System.Text;
using System.Text.Json;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Tests.AdapterContracts;

internal sealed class ScriptedGraphHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _steps = new();
    public List<string> Methods { get; } = [];
    public List<string> Addresses { get; } = [];

    public void Add(object body, int status = 200, Action<HttpRequestMessage>? inspect = null) => _steps.Enqueue(request =>
    {
        inspect?.Invoke(request);
        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        });
    });

    public void AddFailure(Exception exception) => _steps.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal("graph.microsoft.com", request.RequestUri!.Host);
        Assert.Equal(GraphTransport.Preferences, Assert.Single(request.Headers.GetValues("Prefer")));
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Methods.Add(request.Method.Method);
        Addresses.Add(request.RequestUri.AbsoluteUri);
        Assert.NotEmpty(_steps);
        return _steps.Dequeue()(request);
    }

    public void AddReadFoundation(string mailbox)
    {
        Add(new { id = mailbox, mail = "owner@example.test", userPrincipalName = "owner@example.test" });
        Add(new { id = "calendar", isDefaultCalendar = true, owner = new { address = "owner@example.test" } });
        Add(new { value = new[] { new { alias = "UTC", displayName = "UTC" } } });
        Add(new { value = new[] { new { alias = "Etc/UTC", displayName = "UTC" } } });
    }

    public static JsonElement Event(string id = "event", string version = "v1", string? marker = null, bool attendees = false)
        => JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["changeKey"] = version,
            ["@odata.etag"] = "\"" + version + "\"",
            ["start"] = new { dateTime = version == "v1" ? "2030-01-01T09:00:00" : "2030-01-01T10:00:00", timeZone = "UTC" },
            ["end"] = new { dateTime = version == "v1" ? "2030-01-01T10:00:00" : "2030-01-01T11:00:00", timeZone = "UTC" },
            ["isOrganizer"] = true,
            ["organizer"] = new { emailAddress = new { address = "owner@example.test" } },
            ["attendees"] = attendees ? new object[] { new { type = "required", emailAddress = new { address = "attendee@example.test" } } } : [],
            ["isAllDay"] = false,
            ["isCancelled"] = false,
            ["type"] = "singleInstance",
            ["seriesMasterId"] = null,
            ["originalStartTimeZone"] = "UTC",
            ["originalEndTimeZone"] = "UTC",
            ["subject"] = marker is null ? "not selected by discovery" : "PHASE0-DISPOSABLE:" + marker
        });
}
