using System.Collections.Immutable;
using System.Globalization;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Tests.Execution;

public sealed class ExecutionBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedRequestUsesUtcClockAfterWaitingForJournal(bool pastStart)
    {
        var firstItem = ContractFixture.Item;
        if (pastStart)
        {
            var start = ContractFixture.Now.AddMinutes(1);
            firstItem = firstItem with { Source = firstItem.Source with { Start = start, End = start.AddHours(1) } };
        }
        var batch = new BoundaryBatch(firstItem);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.Journal.BeforeReadAsync = async () =>
        {
            entered.TrySetResult();
            await release.Task;
        };
        var first = batch.ExecuteAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = batch.ExecuteAsync();
        try
        {
            Assert.False(queued.IsCompleted);
            var boundary = pastStart ? firstItem.Source.Start!.Value : batch.Request.Approval.ExpiresAt;
            batch.Clock.Advance(boundary - ContractFixture.Now);
        }
        finally
        {
            release.TrySetResult();
        }

        var results = await queued.WaitAsync(TimeSpan.FromSeconds(10));
        await first.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.All(results, result => Assert.Equal(ExecutionOutcome.Blocked, result.Outcome));
        Assert.Empty(batch.Provider.Writes);
        Assert.Empty(batch.Provider.Reads);
        Assert.Empty(batch.Journal.Entries);
    }

    [Theory]
    [InlineData(JournalState.Verified, "expiry")]
    [InlineData(JournalState.Unknown, "expiry")]
    [InlineData(JournalState.Dispatched, "expiry")]
    [InlineData(JournalState.Verified, "dependencies")]
    [InlineData(JournalState.Unknown, "dependencies")]
    [InlineData(JournalState.Dispatched, "dependencies")]
    [InlineData(JournalState.Verified, "source-start")]
    [InlineData(JournalState.Unknown, "source-start")]
    [InlineData(JournalState.Dispatched, "source-start")]
    [InlineData(JournalState.Verified, "patch-start")]
    [InlineData(JournalState.Unknown, "patch-start")]
    [InlineData(JournalState.Dispatched, "patch-start")]
    [InlineData(JournalState.Verified, "plan-expiry")]
    [InlineData(JournalState.Unknown, "plan-expiry")]
    [InlineData(JournalState.Dispatched, "plan-expiry")]
    public async Task RecoveryRetainsEvidenceWhileInvalidAuthorizationBlocksFreshItem(JournalState state, string changed)
    {
        var first = ContractFixture.Item;
        if (changed == "source-start")
        {
            var start = ContractFixture.Now.AddMinutes(1);
            first = first with { Source = first.Source with { Start = start, End = start.AddHours(1) } };
        }
        if (changed == "patch-start")
        {
            var start = ContractFixture.Now.AddMinutes(1);
            first = first with { Patch = first.Patch! with { Start = start, End = start.AddHours(1) } };
        }
        var batch = new BoundaryBatch(first);
        var persisted = new JournalEntry(Guid.NewGuid(), batch.Request.Plan.Digest, first.Id,
            state, first.Source.Guard!, first.Patch!, null);
        batch.Journal.Entries.Add(persisted);
        if (changed == "dependencies")
        {
            batch.Request = batch.Request with
            {
                CurrentDependencies = ContractFixture.Dependencies with { PolicyHash = "changed" }
            };
        }
        else
        {
            var boundary = changed switch
            {
                "source-start" => first.Source.Start!.Value,
                "patch-start" => first.Patch!.Start,
                "plan-expiry" => batch.Request.Plan.Content.ExpiresAt,
                _ => batch.Request.Approval.ExpiresAt
            };
            batch.Clock.Advance(boundary - ContractFixture.Now);
        }

        var results = await batch.ExecuteAsync();

        var recovered = Assert.Single(results, result => result.ItemId == first.Id);
        Assert.Equal(state == JournalState.Verified ? ExecutionOutcome.Verified : ExecutionOutcome.Unknown, recovered.Outcome);
        Assert.Equal(state == JournalState.Verified, recovered.OrganizerVerified);
        Assert.False(recovered.DeliveryVerified);
        Assert.Equal(state == JournalState.Verified ? ExecutionOutcome.Blocked : ExecutionOutcome.Paused,
            Assert.Single(results, result => result.ItemId == "item-2").Outcome);
        Assert.Empty(batch.Provider.Reads);
        Assert.Empty(batch.Provider.Writes);
        Assert.Equal(persisted, Assert.Single(batch.Journal.Entries));
    }

    [Theory]
    [InlineData(true, JournalState.Intent)]
    [InlineData(true, JournalState.Dispatched)]
    [InlineData(false, JournalState.Intent)]
    [InlineData(false, JournalState.Dispatched)]
    public async Task FutureThresholdReachedDuringJournalWorkWithholdsWriteAndHoldsNextItem(
        bool sourceReachesThreshold, JournalState boundary)
    {
        var sourceStart = ContractFixture.Now.AddMinutes(sourceReachesThreshold ? 1 : 10);
        var patchStart = ContractFixture.Now.AddMinutes(sourceReachesThreshold ? 10 : 1);
        var first = ContractFixture.Item with
        {
            Source = ContractFixture.Source with
            {
                Start = sourceStart,
                End = sourceStart.AddHours(1),
                RawStart = new(sourceStart.UtcDateTime.ToString("s", CultureInfo.InvariantCulture), "UTC"),
                RawEnd = new(sourceStart.AddHours(1).UtcDateTime.ToString("s", CultureInfo.InvariantCulture), "UTC")
            },
            Patch = ContractFixture.Patch with { Start = patchStart, End = patchStart.AddHours(1) }
        };
        var batch = new BoundaryBatch(first);
        batch.Journal.AfterAppendAsync = async entry =>
        {
            if (entry.ItemId == first.Id && entry.State == boundary)
            {
                await Task.Yield();
                batch.Clock.Advance(TimeSpan.FromMinutes(1));
            }
        };

        var results = await batch.ExecuteAsync();

        var withheld = Assert.Single(results, r => r.ItemId == first.Id);
        Assert.Contains(withheld.Outcome, new[] { ExecutionOutcome.Blocked, ExecutionOutcome.Paused });
        Assert.False(withheld.OrganizerVerified);
        Assert.False(withheld.DeliveryVerified);
        Assert.Contains(batch.Journal.Entries, e => e.ItemId == first.Id && e.State == boundary);
        Assert.Empty(batch.Provider.Writes);
        AssertNextItemHeld(batch, results);
    }

    [Theory]
    [InlineData(JournalState.Intent, false)]
    [InlineData(JournalState.Intent, true)]
    [InlineData(JournalState.Dispatched, false)]
    [InlineData(JournalState.Dispatched, true)]
    [InlineData(JournalState.Unknown, false)]
    [InlineData(JournalState.Unknown, true)]
    public async Task RecoveredUncertainAttemptHoldsFreshItemRegardlessOfPlanOrder(
        JournalState state, bool uncertainItemFirst)
    {
        var batch = new BoundaryBatch();
        var uncertainId = uncertainItemFirst ? "item-1" : "item-2";
        var freshId = uncertainItemFirst ? "item-2" : "item-1";
        var item = batch.Request.Plan.Content.Items.Single(i => i.Id == uncertainId);
        var persisted = new JournalEntry(Guid.NewGuid(), batch.Request.Plan.Digest, uncertainId,
            state, item.Source.Guard!, item.Patch!, null);
        batch.Journal.Entries.Add(persisted);

        var results = await batch.ExecuteAsync();

        Assert.Equal(ExecutionOutcome.Unknown, Assert.Single(results, r => r.ItemId == uncertainId).Outcome);
        Assert.Equal(ExecutionOutcome.Paused, Assert.Single(results, r => r.ItemId == freshId).Outcome);
        Assert.Empty(batch.Provider.Writes);
        Assert.Empty(batch.Provider.Reads);
        Assert.Equal(persisted, Assert.Single(batch.Journal.Entries));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemScopedConflictAllowsNextIndependentItem(bool recovered)
    {
        var batch = new BoundaryBatch();
        if (recovered)
        {
            batch.Journal.Entries.Add(new(Guid.NewGuid(), batch.Request.Plan.Digest, "item-1",
                JournalState.Conflict, ContractFixture.Source.Guard!, ContractFixture.Patch, "conflict"));
        }
        else
        {
            batch.Provider.FirstMutationOutcome = MutationOutcome.Conflict;
        }

        var results = await batch.ExecuteAsync();

        Assert.Equal(ExecutionOutcome.Conflict, Assert.Single(results, r => r.ItemId == "item-1").Outcome);
        Assert.Equal(ExecutionOutcome.Verified, Assert.Single(results, r => r.ItemId == "item-2").Outcome);
        Assert.Equal(recovered ? 1 : 2, batch.Provider.Writes.Count);
        Assert.Equal("event-2", batch.Provider.Writes[^1].Source.Id);
        Assert.Contains(batch.Journal.Entries, e => e.ItemId == "item-2" && e.State == JournalState.Verified);
    }

    [Theory]
    [InlineData(false, JournalState.Intent)]
    [InlineData(false, JournalState.Dispatched)]
    [InlineData(true, JournalState.Intent)]
    [InlineData(true, JournalState.Dispatched)]
    public async Task ControlChangeAfterDurableRecordingWithholdsWriteAndHoldsNextItem(
        bool cancel, JournalState boundary)
    {
        using var cancellation = new CancellationTokenSource();
        var batch = new BoundaryBatch();
        batch.Journal.AfterAppendAsync = async entry =>
        {
            if (entry.ItemId == "item-1" && entry.State == boundary)
            {
                await Task.Yield();
                if (cancel)
                {
                    cancellation.Cancel();
                }
                else
                {
                    batch.Control.PauseRequested = true;
                }
            }
        };

        var results = await batch.ExecuteAsync(cancellation.Token);

        Assert.Equal(ExecutionOutcome.Paused, Assert.Single(results, r => r.ItemId == "item-1").Outcome);
        Assert.Contains(batch.Journal.Entries, e => e.ItemId == "item-1" && e.State == boundary);
        Assert.Empty(batch.Provider.Writes);
        AssertNextItemHeld(batch, results);
    }

    [Fact]
    public async Task AcceptedMutationWithFailedOutcomeAppendIsUnknownAndHoldsNextItemOnRestart()
    {
        var batch = new BoundaryBatch();
        batch.Journal.FailState = JournalState.Verified;

        var results = await batch.ExecuteAsync();

        var uncertain = Assert.Single(results, r => r.ItemId == "item-1");
        Assert.Equal(ExecutionOutcome.Unknown, uncertain.Outcome);
        Assert.False(uncertain.OrganizerVerified);
        Assert.False(uncertain.DeliveryVerified);
        Assert.Equal("event", Assert.Single(batch.Provider.Writes).Source.Id);
        Assert.Equal<string>(["event", "event"], batch.Provider.Reads);
        Assert.Equal(JournalState.Dispatched, batch.Journal.Entries[^1].State);
        Assert.DoesNotContain(batch.Journal.Entries, e => e.State == JournalState.Verified);
        AssertNextItemHeld(batch, results);

        batch.Journal.FailState = null;
        var restarted = await batch.ExecuteAsync();

        Assert.Equal(ExecutionOutcome.Unknown, Assert.Single(restarted, r => r.ItemId == "item-1").Outcome);
        Assert.Single(batch.Provider.Writes);
        Assert.Equal(2, batch.Provider.Reads.Count);
        AssertNextItemHeld(batch, restarted);
    }

    private static void AssertNextItemHeld(BoundaryBatch batch, ImmutableArray<ExecutionResult> results)
    {
        Assert.Equal(ExecutionOutcome.Paused, Assert.Single(results, r => r.ItemId == "item-2").Outcome);
        Assert.DoesNotContain(batch.Provider.Reads, id => id == "event-2");
        Assert.DoesNotContain(batch.Journal.Entries, e => e.ItemId == "item-2");
    }

    private sealed class BoundaryBatch
    {
        internal ManualExecutionClock Clock { get; } = new();
        internal BoundaryProvider Provider { get; }
        internal BoundaryJournal Journal { get; } = new();
        internal ExecutionControl Control { get; } = new();
        internal ExecutionRequest Request { get; set; }

        internal BoundaryBatch(PlanItem? first = null)
        {
            ImmutableArray<PlanItem> items = [first ?? ContractFixture.Item, ContractFixture.ItemFor("item-2", "event-2")];
            var plan = PlanBuilder.Build(ContractFixture.Draft with { Items = items });
            var approval = PlanApproval.Approve(plan, "operator", ["item-1", "item-2"],
                ContractFixture.Now, ContractFixture.Now.AddMinutes(30));
            Assert.True(PlanApproval.Validate(plan, approval, ContractFixture.Now, ContractFixture.Dependencies).Valid);
            Request = new(plan, approval, ContractFixture.Dependencies, ContractFixture.Admission);
            Provider = new(items);
        }

        internal Task<ImmutableArray<ExecutionResult>> ExecuteAsync(CancellationToken cancellationToken = default)
            => new PlanExecutor(Provider, Provider, Journal, Control, Clock).ExecuteAsync(Request, cancellationToken);
    }

    private sealed class BoundaryJournal : IExecutionJournal
    {
        internal List<JournalEntry> Entries { get; } = [];
        internal Func<JournalEntry, Task>? AfterAppendAsync { get; set; }
        internal JournalState? FailState { get; set; }
        internal Func<Task>? BeforeReadAsync { get; set; }

        public Task PrepareAsync(RemediationPlan plan, Approval approval, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default)
        {
            if (entry.State == FailState)
            {
                throw new IOException("Outcome could not be durably recorded.");
            }
            Entries.Add(entry);
            if (AfterAppendAsync is { } callback)
            {
                await callback(entry);
            }
        }

        public async Task<ImmutableArray<JournalEntry>> ReadAsync(string planDigest, CancellationToken cancellationToken = default)
        {
            if (BeforeReadAsync is { } callback) { await callback(); }
            return Entries.Where(e => e.PlanDigest == planDigest).ToImmutableArray();
        }
    }

    private sealed class BoundaryProvider(ImmutableArray<PlanItem> items) : ICalendarReader, ICalendarWriter
    {
        internal List<string> Reads { get; } = [];
        internal List<GuardedWrite> Writes { get; } = [];
        internal MutationOutcome FirstMutationOutcome { get; set; } = MutationOutcome.Accepted;

        public Task<SourceSnapshot?> ReadAsync(CalendarIdentity identity, string eventId, CancellationToken cancellationToken = default)
        {
            Reads.Add(eventId);
            var item = items.Single(i => i.Source.Identity == identity && i.Source.Id == eventId);
            var source = item.Source;
            if (Writes.Any(w => w.Source.Id == eventId))
            {
                var patch = item.Patch!;
                source = source with
                {
                    Start = patch.Start,
                    End = patch.End,
                    RawStart = new(patch.Start.UtcDateTime.ToString("s", CultureInfo.InvariantCulture), "UTC"),
                    RawEnd = new(patch.End.UtcDateTime.ToString("s", CultureInfo.InvariantCulture), "UTC"),
                    Version = "v2",
                    Guard = "\"v2\""
                };
            }
            return Task.FromResult<SourceSnapshot?>(source);
        }

        public Task<MutationResult> WriteAsync(GuardedWrite write, CancellationToken cancellationToken = default)
        {
            Writes.Add(write);
            return Task.FromResult(new MutationResult(Writes.Count == 1 ? FirstMutationOutcome : MutationOutcome.Accepted,
                "boundary-attempt"));
        }

        public Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Execution must use the approved exact source identities.");
    }
}
