using System.Text.Json;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Tests.Discovery;

public sealed class ReadProbeContinuationTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{}")]
    public async Task MalformedContinuationIsIncompleteWithoutAnotherHttpRead(string continuationJson)
    {
        const string mailbox = "22222222-2222-2222-2222-222222222222";
        var scope = new ReadProbeOptions("11111111-1111-1111-1111-111111111111", mailbox,
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 2, 1, 0, 0, 0, TimeSpan.Zero), 10, 3, 10, 10);
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(mailbox);
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = Array.Empty<object>(),
            ["@odata.nextLink"] = JsonSerializer.Deserialize<JsonElement>(continuationJson)
        });
        using var client = new HttpClient(handler);

        var result = await new CalendarReadProbe(new GraphTransport(client, _ => Task.FromResult("fake"))).RunAsync(scope);

        Assert.False(result.Complete);
        Assert.NotEmpty(result.Holds);
        Assert.Equal(1, result.Pages);
        Assert.Equal(0, result.DetailReads);
        Assert.Empty(result.Events);
        Assert.Equal(5, handler.Methods.Count);
    }
}
