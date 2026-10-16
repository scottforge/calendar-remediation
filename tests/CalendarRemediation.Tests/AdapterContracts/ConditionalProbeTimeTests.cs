using CalendarRemediation.Graph.Feasibility;

namespace CalendarRemediation.Tests.AdapterContracts;

public sealed class ConditionalProbeTimeTests
{
    private static readonly string[] ReadOnly = ["GET"];

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }

    [Fact]
    public async Task IntentPersistenceThatReachesExpiryConsumesOneIntentWithoutDispatch()
    {
        var prepared = ConditionalProbePolicyTests.Prepared();
        var clock = new ManualTimeProvider(prepared.PreparedAt);
        using var handler = new ScriptedGraphHandler();
        handler.Add(ConditionalProbePolicyTests.Event(version: "v2"));
        using var client = new HttpClient(handler);
        var probe = new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")), clock);
        var persistedCount = 0;
        var start = prepared.PreparedAt.AddDays(2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.AttemptAsync(prepared, start, start.AddHours(1), intent =>
        {
            Assert.Equal(ReadOnly, handler.Methods);
            Assert.Equal(prepared.PreparedAt, intent.At);
            persistedCount++;
            clock.Advance(prepared.ExpiresAt - clock.GetUtcNow());
            return Task.CompletedTask;
        }));

        Assert.Equal(1, persistedCount);
        Assert.Equal(prepared.ExpiresAt, clock.GetUtcNow());
        Assert.Equal(ReadOnly, handler.Methods);
    }

    [Fact]
    public async Task PrepareChecksExperimentStopAgainAfterHttpRead()
    {
        var baseline = ConditionalProbePolicyTests.Prepared();
        var clock = new ManualTimeProvider(baseline.PreparedAt);
        var stop = baseline.PreparedAt.AddMinutes(10);
        using var handler = new ScriptedGraphHandler();
        handler.Add(baseline.Source, inspect: _ => clock.Advance(stop - clock.GetUtcNow()));
        using var client = new HttpClient(handler);
        var probe = new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")), clock);

        await Assert.ThrowsAsync<ArgumentException>(() => probe.PrepareAsync(
            baseline.TenantId, baseline.MailboxId, baseline.EventId, baseline.Marker, baseline.ExperimentOwner, stop));

        Assert.Equal(stop, clock.GetUtcNow());
        Assert.Equal(ReadOnly, handler.Methods);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    public async Task PrepareUsesPostReadClockAndCapsExpiryAtThirtyMinutesOrExperimentStop(int stopMinutes)
    {
        var baseline = ConditionalProbePolicyTests.Prepared();
        var clock = new ManualTimeProvider(baseline.PreparedAt);
        var stop = baseline.PreparedAt.AddMinutes(stopMinutes);
        using var handler = new ScriptedGraphHandler();
        handler.Add(baseline.Source, inspect: _ => clock.Advance(TimeSpan.FromMinutes(1)));
        using var client = new HttpClient(handler);
        var probe = new ConditionalWriteProbe(new(client, _ => Task.FromResult("fake")), clock);

        var prepared = await probe.PrepareAsync(
            baseline.TenantId, baseline.MailboxId, baseline.EventId, baseline.Marker, baseline.ExperimentOwner, stop);

        Assert.Equal(baseline.PreparedAt.AddMinutes(1), prepared.PreparedAt);
        Assert.Equal(stopMinutes == 10 ? stop : prepared.PreparedAt.AddMinutes(30), prepared.ExpiresAt);
        Assert.Equal(ReadOnly, handler.Methods);
    }
}
