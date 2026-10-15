using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarRemediation.Graph.Discovery;
using CalendarRemediation.Graph.Feasibility;

namespace CalendarRemediation.Tests.AdapterContracts;

public sealed class ConditionalProbeTests
{
    private const string Marker = "33333333-3333-3333-3333-333333333333";
    private static readonly string[] PatchFields = ["start", "end"];
    private static readonly string[] FullAttempt = ["GET", "PATCH", "GET"];
    private static readonly string[] LostAttempt = ["GET", "PATCH"];
    private static readonly string[] ReadOnly = ["GET"];
    private sealed class Scenario
    {
        private readonly DateTimeOffset _baseline = DateTimeOffset.UtcNow;
        public DateTimeOffset ProposedStart => _baseline.AddDays(1).AddHours(3);
        public PreparedProbe Prepared => new("11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", "event", Marker, _baseline,
            _baseline.AddMinutes(30), "\"v1\"", Event(),
            new("GET", "/me/events/event", _baseline, Guid.NewGuid(), 200, null, null, 1),
            "Synthetic experiment owner", _baseline.AddDays(1));

        public JsonElement Event(string version = "v1", bool attendees = false)
        {
            var item = JsonNode.Parse(ScriptedGraphHandler.Event(version: version, marker: Marker, attendees: attendees).GetRawText())!.AsObject();
            var start = _baseline.AddDays(1).AddHours(version == "v1" ? 0 : 1);
            item["start"] = JsonSerializer.SerializeToNode(new { dateTime = start.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" });
            item["end"] = JsonSerializer.SerializeToNode(new { dateTime = start.AddHours(1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture), timeZone = "UTC" });
            return JsonSerializer.SerializeToElement(item);
        }
    }

    [Fact]
    public async Task Stale412AndUnchangedCompetitorObserveNarrowGuardedOneShotOnly()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        var competitor = scenario.Event(version: "v2");
        handler.Add(competitor);
        var persisted = false;
        Guid persistedRequestId = default;
        handler.Add(new { error = "precondition failed" }, 412, request =>
        {
            Assert.True(persisted);
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfMatch).ToString());
            Assert.Equal("PATCH", request.Method.Method);
            Assert.Equal(persistedRequestId.ToString(), Assert.Single(request.Headers.GetValues("client-request-id")));
        });
        handler.Add(competitor);
        using var client = new HttpClient(handler);
        var probe = new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")));
        var result = await probe.AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), intent =>
        {
            using var doc = JsonDocument.Parse(intent.Patch);
            Assert.Equal(PatchFields, doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
            persisted = true;
            persistedRequestId = intent.ClientRequestId;
            return Task.CompletedTask;
        });
        Assert.True(result.StaleRejected);
        Assert.True(result.CompetitorPreserved);
        Assert.Equal(FullAttempt, handler.Methods);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task AcceptanceThrottleAndServerErrorNeverPassOrReplay(int status)
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        var competitor = scenario.Event(version: "v2");
        handler.Add(competitor);
        handler.Add(new { }, status);
        handler.Add(competitor);
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.False(result.StaleRejected);
        Assert.Equal(status == 200, result.StaleAccepted);
        Assert.Single(handler.Methods, m => m == "PATCH");
    }

    [Fact]
    public async Task LostResponseIsUnknownAndNeverReplayed()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        handler.Add(scenario.Event(version: "v2"));
        handler.AddFailure(new TaskCanceledException("lost response"));
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.StartsWith("UNKNOWN", result.Outcome, StringComparison.Ordinal);
        Assert.Equal(LostAttempt, handler.Methods);
    }

    [Fact]
    public async Task AcceptedStalePatchIsStillFailureWhenReadbackIsLost()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        handler.Add(scenario.Event(version: "v2"));
        handler.Add(new { }, 200);
        handler.AddFailure(new HttpRequestException("readback unavailable"));
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.True(result.StaleAccepted);
        Assert.StartsWith("FAILED", result.Outcome, StringComparison.Ordinal);
        Assert.False(result.CompetitorPreserved);
        Assert.Single(handler.Methods, m => m == "PATCH");
    }

    [Fact]
    public async Task FailedDurableIntentCannotDispatch()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        handler.Add(scenario.Event(version: "v2"));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => throw new IOException("disk full")));
        Assert.Equal(ReadOnly, handler.Methods);
    }

    [Fact]
    public async Task StaleRejectionWithChangedReadbackDoesNotEstablishPreservation()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        handler.Add(scenario.Event(version: "v2"));
        handler.Add(new { }, 412);
        handler.Add(scenario.Event(version: "v3"));
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.True(result.StaleRejected);
        Assert.False(result.CompetitorPreserved);
        Assert.StartsWith("Inconclusive", result.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRequiresAnExactGuardAndNeverMutates()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        handler.Add(scenario.Event());
        using var client = new HttpClient(handler);
        var prepared = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .PrepareAsync(scenario.Prepared.TenantId, scenario.Prepared.MailboxId, "event", Marker, "Synthetic experiment owner", scenario.Prepared.ExperimentStopDate);
        Assert.Equal("\"v1\"", prepared.SourceETag);
        Assert.Equal(ReadOnly, handler.Methods);
    }

    [Fact]
    public async Task AttendeeMeetingExpiredProbeAndUnchangedSourceCannotDispatch()
    {
        var scenario = new Scenario();
        using var handler = new ScriptedGraphHandler();
        using var client = new HttpClient(handler);
        var probe = new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(scenario.Prepared with { ExpiresAt = scenario.Prepared.PreparedAt.AddMinutes(-1) },
            scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(scenario.Prepared with { Source = scenario.Event(attendees: true) },
            scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask));
        Assert.Empty(handler.Methods);
        handler.Add(scenario.Event());
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(scenario.Prepared, scenario.ProposedStart, scenario.ProposedStart.AddHours(1), _ => Task.CompletedTask));
        Assert.Equal(ReadOnly, handler.Methods);
    }
}
