using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Core.Planning;
using CalendarRemediation.Core.Reporting;
using CalendarRemediation.Core.Tests.Execution;

namespace CalendarRemediation.Core.Tests.Planning;

public sealed class ReviewerFindingContractTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AssessmentRequiresTheSourceVersionToHaveBeenObservedInCompleteDiscovery(bool observedDifferentVersion)
    {
        var source = ContractFixture.Source;
        var discovery = ContractFixture.Discovery with
        {
            Events = observedDifferentVersion
                ? [source with { Version = "v2", Guard = "\"v2\"" }]
                : [],
            DetailReads = observedDifferentVersion ? 1 : 0
        };
        var context = ContractFixture.Context with { Discovery = discovery };
        var result = new MeetingAssessor(new FixtureRules()).Assess(source, context);

        Assert.Equal(AssessmentDisposition.Unresolved, result.Disposition);
        Assert.Null(result.Patch);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public async Task MatchingOrganizerFieldsDoNotReportUnprovedSchedulingSemantics()
    {
        // The accepted write and independent UTC readback prove the original field-level
        // success assertions. Neither this reader nor the executor verifies provider rule
        // adoption or the approved target-zone scheduling semantics. Original-zone labels
        // remain creation provenance and are deliberately not used as current-zone proof.
        var rig = new ExecutionRig();
        var outcomes = await rig.ExecuteAsync();
        var result = Assert.Single(outcomes);

        Assert.Equal(ExecutionOutcome.Verified, result.Outcome);
        Assert.True(result.OrganizerVerified);
        Assert.False(result.DeliveryVerified);
        Assert.Single(rig.Writer.Writes);
        Assert.Equal(2, rig.Reader.Reads);

        using var report = JsonDocument.Parse(ReportSerializer.Serialize(new(ContractFixture.Discovery, rig.Request.Plan, outcomes)));
        var item = Assert.Single(report.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Verified", item.GetProperty("outcome").GetString());
        Assert.True(item.GetProperty("organizerVerified").GetBoolean());
        Assert.True(item.TryGetProperty("schedulingVerified", out var schedulingVerified),
            "A real field-verified execution must explicitly report whether scheduling semantics were verified.");
        Assert.False(schedulingVerified.GetBoolean());
        Assert.Equal("OrganizerFields", item.GetProperty("verificationScope").GetString());
        Assert.False(item.GetProperty("deliveryVerified").GetBoolean());
    }

    [Fact]
    public async Task UnknownMutationPausesTheNextApprovedEligibleItemWithoutDispatch()
    {
        var batch = new ReviewerBatch();
        var outcomes = await batch.ExecuteAsync();

        Assert.Equal(ExecutionOutcome.Unknown, Assert.Single(outcomes, o => o.ItemId == "item-1").Outcome);
        Assert.Equal<string>(["event"], batch.Provider.Writes);
        Assert.Equal(ExecutionOutcome.Paused, Assert.Single(outcomes, o => o.ItemId == "item-2").Outcome);
        Assert.DoesNotContain(batch.Journal.Entries, e => e.ItemId == "item-2" && e.State == JournalState.Dispatched);
    }

    [Fact]
    public async Task UncertainSourceReadPausesTheNextApprovedEligibleItemWithoutDispatch()
    {
        var batch = new ReviewerBatch();
        batch.Provider.FailFirstSourceRead = true;
        batch.Provider.FirstMutationOutcome = MutationOutcome.Accepted;
        var outcomes = await batch.ExecuteAsync();

        Assert.Equal(ExecutionOutcome.Blocked, Assert.Single(outcomes, o => o.ItemId == "item-1").Outcome);
        Assert.Empty(batch.Provider.Writes);
        Assert.Equal(ExecutionOutcome.Paused, Assert.Single(outcomes, o => o.ItemId == "item-2").Outcome);
        Assert.Equal<string>(["event"], batch.Provider.Reads);
    }
}

internal sealed class ReviewerBatch
{
    internal ReviewerProvider Provider { get; }
    internal MemoryJournal Journal { get; } = new([]);
    private readonly ExecutionRequest _request;

    internal ReviewerBatch()
    {
        ImmutableArray<PlanItem> items = [ContractFixture.Item, ContractFixture.ItemFor("item-2", "event-2")];
        var plan = PlanBuilder.Build(ContractFixture.Draft with { Items = items });
        var approval = PlanApproval.Approve(plan, "operator", ["item-1", "item-2"],
            ContractFixture.Now, ContractFixture.Now.AddMinutes(30));
        Assert.True(PlanApproval.Validate(plan, approval, ContractFixture.Now, ContractFixture.Dependencies).Valid);
        Provider = new(items);
        _request = new(plan, approval, ContractFixture.Dependencies, ContractFixture.Admission, ContractFixture.Now);
    }

    internal Task<ImmutableArray<ExecutionResult>> ExecuteAsync()
        => new PlanExecutor(Provider, Provider, Journal, new ExecutionControl()).ExecuteAsync(_request);
}

// The provider returns each approved item's exact source before dispatch and its exact
// changed organizer fields afterwards. Thus the second item remains independently
// eligible and would succeed if an erroneous executor continued after uncertainty.
internal sealed class ReviewerProvider(ImmutableArray<PlanItem> items) : ICalendarReader, ICalendarWriter
{
    internal List<string> Reads { get; } = [];
    internal List<string> Writes { get; } = [];
    internal bool FailFirstSourceRead { get; set; }
    internal MutationOutcome FirstMutationOutcome { get; set; } = MutationOutcome.Unknown;

    public Task<SourceSnapshot?> ReadAsync(CalendarIdentity identity, string eventId, CancellationToken cancellationToken = default)
    {
        Reads.Add(eventId);
        if (FailFirstSourceRead && eventId == "event")
        {
            return Task.FromException<SourceSnapshot?>(new HttpRequestException("Mailbox provider read is unavailable."));
        }
        var item = items.Single(i => i.Source.Id == eventId && i.Source.Identity == identity);
        var source = item.Source;
        if (Writes.Contains(eventId))
        {
            source = source with
            {
                Start = item.Patch!.Start,
                End = item.Patch.End,
                RawStart = new(item.Patch.Start.UtcDateTime.ToString("s", CultureInfo.InvariantCulture), "UTC"),
                RawEnd = new(item.Patch.End.UtcDateTime.ToString("s", CultureInfo.InvariantCulture), "UTC"),
                Version = "v2",
                Guard = "\"v2\""
            };
        }
        return Task.FromResult<SourceSnapshot?>(source);
    }

    public Task<MutationResult> WriteAsync(GuardedWrite write, CancellationToken cancellationToken = default)
    {
        Writes.Add(write.Source.Id);
        return Task.FromResult(new MutationResult(Writes.Count == 1 ? FirstMutationOutcome : MutationOutcome.Accepted,
            "reviewer-attempt-" + Writes.Count.ToString(CultureInfo.InvariantCulture)));
    }

    public Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("The approved exact-source executor must not rediscover scope.");
}
