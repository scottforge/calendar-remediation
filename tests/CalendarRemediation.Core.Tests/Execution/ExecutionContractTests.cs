using System.Collections.Immutable;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Tests.Execution;

public sealed class ExecutionContractTests
{
    [Theory]
    [InlineData("plan")]
    [InlineData("digest")]
    [InlineData("plan-id")]
    [InlineData("operator")]
    [InlineData("selection")]
    [InlineData("approval-lifetime")]
    [InlineData("approval-before-plan")]
    [InlineData("approval-after-start")]
    public async Task InvalidBindingCannotRecoverPersistedSuccess(string changed)
    {
        var draft = ContractFixture.Draft;
        if (changed == "approval-after-start")
        {
            var start = ContractFixture.Now.AddMinutes(1);
            draft = draft with
            {
                Items = [ContractFixture.Item with { Source = ContractFixture.Source with { Start = start, End = start.AddHours(1) } }]
            };
        }
        var rig = new ExecutionRig(draft);
        rig.Journal.Entries.Add(new(Guid.NewGuid(), rig.Request.Plan.Digest, "item-1", JournalState.Verified,
            "\"v1\"", ContractFixture.Patch, null));
        var approval = rig.Request.Approval;
        if (changed == "plan")
        {
            rig.Request = rig.Request with { Plan = rig.Request.Plan with { CanonicalJson = "tampered" } };
        }
        else
        {
            approval = changed switch
            {
                "digest" => approval with { Digest = "tampered" },
                "plan-id" => approval with { PlanId = "other" },
                "operator" => approval with { Operator = "" },
                "selection" => approval with { SelectedItemIds = ["item-1", "item-1"] },
                "approval-lifetime" => approval with { ExpiresAt = rig.Request.Plan.Content.ExpiresAt.AddTicks(1) },
                "approval-before-plan" => approval with { ApprovedAt = rig.Request.Plan.Content.CreatedAt.AddTicks(-1) },
                _ => approval with { ApprovedAt = rig.Request.Plan.Content.Items[0].Source.Start!.Value }
            };
            rig.Request = rig.Request with { Approval = approval };
        }
        rig.Clock.Advance(TimeSpan.FromDays(10));

        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Blocked, result.Outcome);
        Assert.Equal("approval-invalid", result.Reason);
        Assert.False(result.OrganizerVerified);
        Assert.Empty(rig.Journal.Prepared);
        Assert.Empty(rig.Writer.Writes);
        Assert.Equal(0, rig.Reader.Reads);
    }

    [Fact]
    public async Task RequestConstructedBeforeExpiryCannotAuthorizeLaterExecution()
    {
        var rig = new ExecutionRig();
        rig.Clock.Advance(rig.Request.Approval.ExpiresAt - ContractFixture.Now);

        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Blocked, result.Outcome);
        Assert.Empty(rig.Writer.Writes);
        Assert.Equal(0, rig.Reader.Reads);
        Assert.Empty(rig.Journal.Entries);
    }

    [Fact]
    public async Task JournalIntentIsDurableBeforeExactGuardedDispatchAndVerifiedReadback()
    {
        var rig = new ExecutionRig();
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Verified, result.Outcome);
        Assert.True(result.OrganizerVerified);
        Assert.False(result.DeliveryVerified);
        var write = Assert.Single(rig.Writer.Writes);
        Assert.Equal(ContractFixture.Patch, write.Patch);
        Assert.Equal("\"v1\"", write.Source.Guard);
        var intent = Assert.Single(rig.Journal.Entries, e => e.State == JournalState.Intent);
        Assert.Equal(write.AttemptId, intent.AttemptId);
        Assert.Equal(rig.Request.Plan.Digest, intent.PlanDigest);
        Assert.Equal(write.Patch, intent.Patch);
        Assert.Equal(write.Source.Guard, intent.Guard);
        var prepared = Assert.Single(rig.Journal.Prepared);
        Assert.Equal(rig.Request.Plan.CanonicalJson, prepared.Plan.CanonicalJson);
        Assert.Equal(rig.Request.Approval, prepared.Approval);
        Assert.True(rig.Trace.IndexOf("journal:Approval") < rig.Trace.IndexOf("journal:Intent"));
        Assert.True(rig.Trace.IndexOf("journal:Intent") < rig.Trace.IndexOf("write"));
        Assert.Equal(JournalState.Verified, rig.Journal.Entries[^1].State);
        Assert.Equal(2, rig.Reader.Reads);
    }

    [Fact]
    public async Task JournalFailureBlocksDispatch()
    {
        var rig = new ExecutionRig();
        rig.Journal.FailAppend = true;
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Blocked, result.Outcome);
        Assert.Empty(rig.Writer.Writes);
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("version")]
    [InlineData("admission")]
    [InlineData("expiry")]
    public async Task RevalidationFailurePreventsAnyMutation(string changed)
    {
        var rig = new ExecutionRig();
        switch (changed)
        {
            case "dependencies":
                rig.Request = rig.Request with { CurrentDependencies = ContractFixture.Dependencies with { PolicyHash = "changed-policy" } };
                break;
            case "version":
                rig.Reader.Before = ContractFixture.Source with { Version = "v2", Guard = "\"v2\"" };
                break;
            case "admission":
                rig.Request = rig.Request with { Admission = ContractFixture.Admission with { Admitted = false } };
                break;
            case "expiry":
                rig.Clock.Advance(rig.Request.Approval.ExpiresAt - ContractFixture.Now);
                break;
        }
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Contains(result.Outcome, new[] { ExecutionOutcome.Blocked, ExecutionOutcome.Conflict });
        Assert.Empty(rig.Writer.Writes);
        Assert.False(result.OrganizerVerified);
    }

    [Fact]
    public async Task SelectedNoopIsSkippedWithoutDispatch()
    {
        var source = ContractFixture.Source with
        {
            Start = ContractFixture.Patch.Start,
            End = ContractFixture.Patch.End,
            RawStart = new("2030-01-02T15:00:00", "UTC"),
            RawEnd = new("2030-01-02T16:00:00", "UTC")
        };
        var draft = ContractFixture.Draft with
        {
            Items = [ContractFixture.Item with { Source = source, Disposition = AssessmentDisposition.NoOp, Patch = null }]
        };
        var rig = new ExecutionRig(draft);
        rig.Reader.Before = source;
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Skipped, result.Outcome);
        Assert.Empty(rig.Writer.Writes);
    }

    [Fact]
    public async Task GuardConflictIsTerminalWithoutReadbackOrRetry()
    {
        var rig = new ExecutionRig();
        rig.Writer.Result = new(MutationOutcome.Conflict, "request-conflict");
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Conflict, result.Outcome);
        Assert.Single(rig.Writer.Writes);
        Assert.Equal(1, rig.Reader.Reads);
        Assert.Equal(JournalState.Conflict, rig.Journal.Entries[^1].State);
    }

    [Fact]
    public async Task AcceptedPatchWithMismatchingReadbackIsUnknownNotVerified()
    {
        var rig = new ExecutionRig();
        rig.Reader.After = ContractFixture.Source;
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.False(result.OrganizerVerified);
        Assert.False(result.DeliveryVerified);
        Assert.Single(rig.Writer.Writes);
        Assert.Equal(JournalState.Unknown, rig.Journal.Entries[^1].State);
    }

    [Fact]
    public async Task LostResponseIsHeldWithoutRetryEvenIfTargetIsLaterObserved()
    {
        var rig = new ExecutionRig();
        rig.Writer.Failure = new HttpRequestException("connection lost after dispatch");
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.False(result.OrganizerVerified);
        Assert.Single(rig.Writer.Writes);
        Assert.Equal(JournalState.Unknown, rig.Journal.Entries[^1].State);

        var observed = await rig.Reader.ReadAsync(ContractFixture.Source.Identity, ContractFixture.Source.Id);
        Assert.NotNull(observed);
        Assert.Equal(ContractFixture.Patch.Start, observed.Start);
        Assert.Equal(ContractFixture.Patch.End, observed.End);
        var persistedAttempt = rig.Journal.Entries[^1];
        rig.Writer.Failure = null;

        // ExecuteAsync creates a new executor over the same persisted attempt history.
        var restarted = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Unknown, restarted.Outcome);
        Assert.False(restarted.OrganizerVerified);
        Assert.False(restarted.DeliveryVerified);
        Assert.Single(rig.Writer.Writes);
        Assert.Equal(persistedAttempt, rig.Journal.Entries[^1]);
        Assert.DoesNotContain(rig.Journal.Entries, entry => entry.State == JournalState.Verified);
        Assert.Equal(2, rig.Reader.Reads);
    }

    [Fact]
    public async Task RestartWithDurableInflightIntentNeverRedispatches()
    {
        var rig = new ExecutionRig();
        rig.Journal.Entries.Add(new(Guid.NewGuid(), rig.Request.Plan.Digest, "item-1", JournalState.Dispatched,
            "\"v1\"", ContractFixture.Patch, null));
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Unknown, result.Outcome);
        Assert.Empty(rig.Writer.Writes);
    }

    [Fact]
    public async Task PauseBeforeDispatchKeepsItemsUnmutated()
    {
        var rig = new ExecutionRig();
        rig.Control.PauseRequested = true;
        var result = Assert.Single(await rig.ExecuteAsync());

        Assert.Equal(ExecutionOutcome.Paused, result.Outcome);
        Assert.Empty(rig.Writer.Writes);
    }

    [Fact]
    public async Task ApprovalSelectionIsEnforcedEvenWhenPlanHasOtherChanges()
    {
        var other = ContractFixture.ItemFor("item-2", "event-2");
        var rig = new ExecutionRig(ContractFixture.Draft with { Items = [ContractFixture.Item, other] });
        var results = await rig.ExecuteAsync();

        Assert.Equal("event", Assert.Single(rig.Writer.Writes).Source.Id);
        Assert.DoesNotContain(results, r => r.ItemId == "item-2" && r.Outcome == ExecutionOutcome.Verified);
    }
}

internal sealed class ExecutionRig
{
    internal List<string> Trace { get; } = [];
    internal ScriptedReader Reader { get; }
    internal RecordingWriter Writer { get; }
    internal MemoryJournal Journal { get; }
    internal ExecutionControl Control { get; } = new();
    internal ManualExecutionClock Clock { get; } = new();
    internal ExecutionRequest Request { get; set; }

    internal ExecutionRig(PlanDraft? draft = null)
    {
        Reader = new(Trace);
        Writer = new(Trace);
        Journal = new(Trace);
        var plan = PlanBuilder.Build(draft ?? ContractFixture.Draft);
        Request = new(plan, ContractFixture.Approval(plan), ContractFixture.Dependencies, ContractFixture.Admission);
    }

    internal Task<ImmutableArray<ExecutionResult>> ExecuteAsync()
        => new PlanExecutor(Reader, Writer, Journal, Control, Clock).ExecuteAsync(Request);
}

internal sealed class ManualExecutionClock : TimeProvider
{
    private long _ticks;
    public override DateTimeOffset GetUtcNow() => ContractFixture.Now.AddTicks(Interlocked.Read(ref _ticks));
    internal void Advance(TimeSpan elapsed) => Interlocked.Add(ref _ticks, elapsed.Ticks);
}

internal sealed class ScriptedReader(List<string> trace) : ICalendarReader
{
    internal SourceSnapshot Before { get; set; } = ContractFixture.Source;
    internal SourceSnapshot After { get; set; } = ContractFixture.Source with
    {
        Start = ContractFixture.Patch.Start,
        End = ContractFixture.Patch.End,
        RawStart = new("2030-01-02T15:00:00", "UTC"),
        RawEnd = new("2030-01-02T16:00:00", "UTC"),
        Version = "v2",
        Guard = "\"v2\""
    };
    internal int Reads { get; private set; }
    public Task<SourceSnapshot?> ReadAsync(CalendarIdentity identity, string eventId, CancellationToken cancellationToken = default)
    {
        trace.Add("read");
        return Task.FromResult<SourceSnapshot?>(Reads++ == 0 ? Before : After);
    }

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Execution uses exact identity reads, never a fresh broad discovery.");
}

internal sealed class RecordingWriter(List<string> trace) : ICalendarWriter
{
    internal List<GuardedWrite> Writes { get; } = [];
    internal MutationResult Result { get; set; } = new(MutationOutcome.Accepted, "request-success");
    internal Exception? Failure { get; set; }
    public Task<MutationResult> WriteAsync(GuardedWrite write, CancellationToken cancellationToken = default)
    {
        trace.Add("write");
        Writes.Add(write);
        return Failure is null ? Task.FromResult(Result) : Task.FromException<MutationResult>(Failure);
    }
}

internal sealed class MemoryJournal(List<string> trace) : IExecutionJournal
{
    internal List<JournalEntry> Entries { get; } = [];
    internal List<(RemediationPlan Plan, Approval Approval)> Prepared { get; } = [];
    internal bool FailAppend { get; set; }
    public Task PrepareAsync(RemediationPlan plan, Approval approval, CancellationToken cancellationToken = default)
    {
        trace.Add("journal:Approval");
        Prepared.Add((plan, approval));
        return Task.CompletedTask;
    }

    public Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        if (FailAppend)
        {
            return Task.FromException(new IOException("durable journal unavailable"));
        }
        trace.Add("journal:" + entry.State);
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<ImmutableArray<JournalEntry>> ReadAsync(string planDigest, CancellationToken cancellationToken = default)
        => Task.FromResult(Entries.Where(e => e.PlanDigest == planDigest).ToImmutableArray());
}

internal sealed class ExecutionControl : IExecutionControl
{
    public bool PauseRequested { get; set; }
    public bool StopRequested { get; set; }
}
