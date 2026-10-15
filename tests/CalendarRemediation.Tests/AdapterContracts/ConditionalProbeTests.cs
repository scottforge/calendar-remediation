using System.Text.Json;
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
    private static PreparedProbe Prepared => new("11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222", "event", Marker, DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(30), "\"v1\"", ScriptedGraphHandler.Event(marker: Marker),
        new("GET", "/me/events/event", DateTimeOffset.UtcNow, Guid.NewGuid(), 200, null, null, 1),
        "Synthetic experiment owner", DateTimeOffset.UtcNow.AddDays(1));
    private static readonly DateTimeOffset ProposedStart = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Stale412AndUnchangedCompetitorObserveNarrowGuardedOneShotOnly()
    {
        using var handler = new ScriptedGraphHandler();
        var competitor = ScriptedGraphHandler.Event(version: "v2", marker: Marker);
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
        var result = await probe.AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), intent =>
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
        using var handler = new ScriptedGraphHandler();
        var competitor = ScriptedGraphHandler.Event(version: "v2", marker: Marker);
        handler.Add(competitor);
        handler.Add(new { }, status);
        handler.Add(competitor);
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.False(result.StaleRejected);
        Assert.Equal(status == 200, result.StaleAccepted);
        Assert.Single(handler.Methods, m => m == "PATCH");
    }

    [Fact]
    public async Task LostResponseIsUnknownAndNeverReplayed()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(ScriptedGraphHandler.Event(version: "v2", marker: Marker));
        handler.AddFailure(new TaskCanceledException("lost response"));
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.StartsWith("UNKNOWN", result.Outcome, StringComparison.Ordinal);
        Assert.Equal(LostAttempt, handler.Methods);
    }

    [Fact]
    public async Task AcceptedStalePatchIsStillFailureWhenReadbackIsLost()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(ScriptedGraphHandler.Event(version: "v2", marker: Marker));
        handler.Add(new { }, 200);
        handler.AddFailure(new HttpRequestException("readback unavailable"));
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.True(result.StaleAccepted);
        Assert.StartsWith("FAILED", result.Outcome, StringComparison.Ordinal);
        Assert.False(result.CompetitorPreserved);
        Assert.Single(handler.Methods, m => m == "PATCH");
    }

    [Fact]
    public async Task FailedDurableIntentCannotDispatch()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(ScriptedGraphHandler.Event(version: "v2", marker: Marker));
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), _ => throw new IOException("disk full")));
        Assert.Equal(ReadOnly, handler.Methods);
    }

    [Fact]
    public async Task StaleRejectionWithChangedReadbackDoesNotEstablishPreservation()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(ScriptedGraphHandler.Event(version: "v2", marker: Marker));
        handler.Add(new { }, 412);
        handler.Add(ScriptedGraphHandler.Event(version: "v3", marker: Marker));
        using var client = new HttpClient(handler);
        var result = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask);
        Assert.True(result.StaleRejected);
        Assert.False(result.CompetitorPreserved);
        Assert.StartsWith("Inconclusive", result.Outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRequiresAnExactGuardAndNeverMutates()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(ScriptedGraphHandler.Event(marker: Marker));
        using var client = new HttpClient(handler);
        var prepared = await new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")))
            .PrepareAsync(Prepared.TenantId, Prepared.MailboxId, "event", Marker, "Synthetic experiment owner", DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal("\"v1\"", prepared.SourceETag);
        Assert.Equal(ReadOnly, handler.Methods);
    }

    [Fact]
    public async Task AttendeeMeetingExpiredProbeAndUnchangedSourceCannotDispatch()
    {
        using var handler = new ScriptedGraphHandler();
        using var client = new HttpClient(handler);
        var probe = new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(Prepared with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
            ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask));
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(Prepared with { Source = ScriptedGraphHandler.Event(marker: Marker, attendees: true) },
            ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask));
        Assert.Empty(handler.Methods);
        handler.Add(ScriptedGraphHandler.Event(marker: Marker));
        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(Prepared, ProposedStart, ProposedStart.AddHours(1), _ => Task.CompletedTask));
        Assert.Equal(ReadOnly, handler.Methods);
    }
}
