using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Intent;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Tests.Planning;

public sealed class PlanSelectionTests
{
    [Theory]
    [InlineData(AssessmentDisposition.NoOp)]
    [InlineData(AssessmentDisposition.Change)]
    public void EligibleSelectionCanBeApprovedAndValidated(AssessmentDisposition disposition)
    {
        AssertAccepted(Item(disposition), ContractFixture.Scope);
    }

    [Theory]
    [InlineData(AssessmentDisposition.NoOp)]
    [InlineData(AssessmentDisposition.Change)]
    public void SourceScopeIncludesStartAndExcludesEnd(AssessmentDisposition disposition)
    {
        var item = Item(disposition);
        var start = item.Source.Start!.Value;

        AssertAccepted(item, ContractFixture.Scope with { Start = start });
        AssertRejected(item, ContractFixture.Scope with { Start = start.AddTicks(1) });
        AssertAccepted(item, ContractFixture.Scope with { End = start.AddTicks(1) });
        AssertRejected(item, ContractFixture.Scope with { End = start });
    }

    [Theory]
    [InlineData(AssessmentDisposition.NoOp, false)]
    [InlineData(AssessmentDisposition.NoOp, true)]
    [InlineData(AssessmentDisposition.Change, false)]
    [InlineData(AssessmentDisposition.Change, true)]
    public void SourceMustBeStrictlyAfterNowAndExecutionCutoff(AssessmentDisposition disposition, bool cutoff)
    {
        var item = Item(disposition);
        var boundary = ContractFixture.Now.AddMinutes(10);
        item = item with
        {
            Source = item.Source with { Start = boundary, End = boundary.AddHours(1) }
        };
        var scope = ContractFixture.Scope with { ExecutionCutoff = cutoff ? boundary : ContractFixture.Now };
        var now = cutoff ? ContractFixture.Now : boundary;

        AssertRejected(item, scope, now);
        AssertAccepted(item with
        {
            Source = item.Source with { Start = boundary.AddTicks(1), End = boundary.AddHours(1).AddTicks(1) }
        }, scope, now);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PatchMustBeStrictlyAfterNowAndExecutionCutoff(bool cutoff)
    {
        var item = ContractFixture.Item;
        var boundary = ContractFixture.Now.AddMinutes(10);
        item = item with { Patch = item.Patch! with { Start = boundary, End = boundary.AddHours(1) } };
        var scope = ContractFixture.Scope with { ExecutionCutoff = cutoff ? boundary : ContractFixture.Now };
        var now = cutoff ? ContractFixture.Now : boundary;

        AssertRejected(item, scope, now);
        AssertAccepted(item with
        {
            Patch = item.Patch! with { Start = boundary.AddTicks(1), End = boundary.AddHours(1).AddTicks(1) }
        }, scope, now);
    }

    [Theory]
    [InlineData(AssessmentDisposition.NoOp)]
    [InlineData(AssessmentDisposition.Change)]
    public void MissingSourceEndCannotBeSelected(AssessmentDisposition disposition)
    {
        var item = Item(disposition);
        AssertAccepted(item, ContractFixture.Scope);
        AssertRejected(item with { Source = item.Source with { End = null } }, ContractFixture.Scope);
    }

    [Theory]
    [InlineData("unknown-organizer")]
    [InlineData("unknown-cancellation")]
    [InlineData("unknown-all-day")]
    [InlineData("unknown-kind")]
    [InlineData("external-attendee")]
    [InlineData("missing-guard")]
    [InlineData("wildcard-guard")]
    [InlineData("unresolved-evidence")]
    public void UnsafeSourceEvidenceCannotBeSelected(string evidence)
    {
        var item = ContractFixture.Item;
        AssertAccepted(item, ContractFixture.Scope);
        var source = item.Source;
        source = evidence switch
        {
            "unknown-organizer" => source with { IsOrganizer = null },
            "unknown-cancellation" => source with { IsCancelled = null },
            "unknown-all-day" => source with { IsAllDay = null },
            "unknown-kind" => source with { Kind = EventKind.Unknown },
            "external-attendee" => source with { Attendees = [new("guest@example.test", AttendeeKind.External)] },
            "missing-guard" => source with { Guard = null },
            "wildcard-guard" => source with { Guard = "*" },
            "unresolved-evidence" => source with { Unresolved = ["organizer-state"] },
            _ => throw new ArgumentOutOfRangeException(nameof(evidence))
        };

        AssertRejected(item with { Source = source }, ContractFixture.Scope);
    }

    [Fact]
    public void MissingDecisionDependencyCannotBeSelected()
    {
        var item = ContractFixture.Item;
        AssertAccepted(item, ContractFixture.Scope);
        AssertRejected(item with
        {
            Dependencies = item.Dependencies with { AdmissionHash = " " }
        }, ContractFixture.Scope);
    }

    [Theory]
    [InlineData("duration")]
    [InlineData("zone")]
    [InlineData("blank-zone")]
    [InlineData("preservation-mode")]
    public void ChangeRequiresExactDurationAndConfirmedLocalZone(string mismatch)
    {
        var item = ContractFixture.Item;
        AssertAccepted(item, ContractFixture.Scope);
        var changed = mismatch switch
        {
            "duration" => item with { Patch = item.Patch! with { End = item.Patch.End.AddTicks(1) } },
            "zone" => item with { Patch = item.Patch! with { Zone = "Fixture/OldMinus07" } },
            "blank-zone" => item with { Patch = item.Patch! with { Zone = " " } },
            "preservation-mode" => item with { Intent = item.Intent with { Mode = PreservationMode.PreserveAbsoluteInstant } },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };

        AssertRejected(changed, ContractFixture.Scope);
    }

    [Theory]
    [InlineData(AssessmentDisposition.NoOp)]
    [InlineData(AssessmentDisposition.Change)]
    public void ValidationRechecksSourceTimeWhileApprovalIsStillLive(AssessmentDisposition disposition)
    {
        var start = ContractFixture.Now.AddMinutes(10);
        var item = Item(disposition);
        item = item with { Source = item.Source with { Start = start, End = start.AddHours(1) } };
        var plan = PlanBuilder.Build(ContractFixture.Draft with { Items = [item] });
        var approval = PlanApproval.Approve(plan, "operator", [item.Id], ContractFixture.Now, ContractFixture.Now.AddMinutes(30));

        Assert.True(PlanApproval.Validate(plan, approval, start.AddTicks(-1), item.Dependencies).Valid);
        var decision = PlanApproval.Validate(plan, approval, start, item.Dependencies);

        Assert.False(decision.Valid);
        Assert.Equal("item-not-executable", Assert.Single(decision.Reasons));
    }

    [Fact]
    public void ValidationRechecksPatchTimeWhileSourceAndApprovalAreStillFuture()
    {
        var start = ContractFixture.Now.AddMinutes(10);
        var item = ContractFixture.Item with
        {
            Patch = ContractFixture.Patch with { Start = start, End = start.AddHours(1) }
        };
        var plan = PlanBuilder.Build(ContractFixture.Draft with { Items = [item] });
        var approval = PlanApproval.Approve(plan, "operator", [item.Id], ContractFixture.Now, ContractFixture.Now.AddMinutes(30));

        Assert.True(PlanApproval.Validate(plan, approval, start.AddTicks(-1), item.Dependencies).Valid);
        var decision = PlanApproval.Validate(plan, approval, start, item.Dependencies);

        Assert.False(decision.Valid);
        Assert.Equal("item-not-executable", Assert.Single(decision.Reasons));
    }

    private static PlanItem Item(AssessmentDisposition disposition) => ContractFixture.Item with
    {
        Disposition = disposition,
        Patch = disposition == AssessmentDisposition.NoOp ? null : ContractFixture.Patch
    };

    private static void AssertAccepted(PlanItem item, DiscoveryScope scope, DateTimeOffset? now = null)
    {
        var at = now ?? ContractFixture.Now;
        var plan = PlanBuilder.Build(ContractFixture.Draft with { Scope = scope, Items = [item] });
        var approval = PlanApproval.Approve(plan, "operator", [item.Id], at, ContractFixture.Now.AddMinutes(30));
        var decision = PlanApproval.Validate(plan, approval, at, item.Dependencies);

        Assert.True(decision.Valid);
        Assert.Empty(decision.Reasons);
        Assert.Equal(item.Id, Assert.Single(approval.SelectedItemIds));
    }

    private static void AssertRejected(PlanItem item, DiscoveryScope scope, DateTimeOffset? now = null)
    {
        var at = now ?? ContractFixture.Now;
        var plan = PlanBuilder.Build(ContractFixture.Draft with { Scope = scope, Items = [item] });

        Assert.Throws<ArgumentException>(() => PlanApproval.Approve(plan, "operator", [item.Id], at, ContractFixture.Now.AddMinutes(30)));
        var decision = PlanApproval.Validate(plan, ContractFixture.Approval(plan), at, item.Dependencies);

        Assert.False(decision.Valid);
        Assert.Equal("item-not-executable", Assert.Single(decision.Reasons));
    }
}
