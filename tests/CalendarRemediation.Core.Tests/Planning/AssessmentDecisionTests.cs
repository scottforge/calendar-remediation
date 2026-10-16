using CalendarRemediation.Core.Intent;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Tests.Planning;

public sealed class AssessmentDecisionTests
{
    [Theory]
    [InlineData("default-attendees", AssessmentDisposition.Unresolved, "incomplete-source-evidence")]
    [InlineData("default-unresolved", AssessmentDisposition.Unresolved, "incomplete-source-evidence")]
    [InlineData("not-organizer", AssessmentDisposition.Ineligible, "not-future-organizer-meeting")]
    [InlineData("cancelled", AssessmentDisposition.Ineligible, "not-future-organizer-meeting")]
    [InlineData("past", AssessmentDisposition.Ineligible, "not-future-organizer-meeting")]
    [InlineData("unsupported", AssessmentDisposition.Unsupported, "outside-pilot-category")]
    [InlineData("incomplete-discovery", AssessmentDisposition.Unresolved, "incomplete-source-evidence")]
    [InlineData("unobserved", AssessmentDisposition.Unresolved, "source-not-observed-or-evidence-changed")]
    [InlineData("default-observations", AssessmentDisposition.Unresolved, "source-not-observed")]
    [InlineData("conflicting-observation", AssessmentDisposition.Unresolved, "source-not-observed-or-evidence-changed")]
    public void EarlierSourceFailureWinsOverLaterFailuresWithoutResolvingRules(
        string failure, AssessmentDisposition disposition, string reason)
    {
        var source = failure switch
        {
            "default-attendees" => ContractFixture.Source with { Attendees = default, IsOrganizer = false, IsAllDay = true },
            "default-unresolved" => ContractFixture.Source with { Unresolved = default, IsCancelled = true, IsAllDay = true },
            "not-organizer" => ContractFixture.Source with { IsOrganizer = false, IsAllDay = true },
            "cancelled" => ContractFixture.Source with { IsCancelled = true, IsAllDay = true },
            "past" => ContractFixture.Source with { Start = ContractFixture.Now, IsAllDay = true },
            "unsupported" => ContractFixture.Source with { IsAllDay = true },
            _ => ContractFixture.Source
        };
        var discovery = ContractFixture.Discovery with { Events = [source] };
        discovery = failure switch
        {
            "unobserved" => discovery with { Events = [] },
            "default-observations" => discovery with { Events = default },
            "conflicting-observation" => discovery with { Events = [source, source with { Version = "changed" }] },
            _ => discovery with { Complete = false }
        };
        var context = ContractFixture.Context with { Discovery = discovery, Intent = null, Policy = null };
        var rules = new FixtureRules();

        var result = new MeetingAssessor(rules).Assess(source, context);

        AssertHeld(result, disposition, reason);
        Assert.Same(source, result.Source);
        Assert.Empty(rules.Calls);
    }

    [Theory]
    [InlineData("unapproved-policy")]
    [InlineData("unverified-adoption")]
    [InlineData("missing-dependency")]
    [InlineData("unproved-admission")]
    [InlineData("future-confirmation")]
    [InlineData("missing-confirmation")]
    [InlineData("source-before-policy")]
    public void AbsoluteIntentStillRequiresCurrentDecisionEvidence(string failure)
    {
        var context = ContractFixture.Context with
        {
            Intent = ContractFixture.Intent with { Mode = PreservationMode.PreserveAbsoluteInstant }
        };
        context = failure switch
        {
            "unapproved-policy" => context with { Policy = ContractFixture.Policy with { Approved = false } },
            "unverified-adoption" => context with { Policy = ContractFixture.Policy with { AdoptionVerified = false } },
            "missing-dependency" => context with
            {
                Policy = ContractFixture.Policy with { Dependencies = ContractFixture.Dependencies with { MappingHash = "" } }
            },
            "unproved-admission" => context with { Admission = ContractFixture.Admission with { AtomicGuardProven = false } },
            "future-confirmation" => context with { Intent = context.Intent! with { ConfirmedAt = ContractFixture.Now.AddTicks(1) } },
            "missing-confirmation" => context with { Intent = context.Intent! with { EvidenceId = " " } },
            "source-before-policy" => context with
            {
                Policy = ContractFixture.Policy with { EffectiveFrom = ContractFixture.Source.Start!.Value.AddTicks(1) }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(failure))
        };
        var rules = new FixtureRules();

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        AssertHeld(result, AssessmentDisposition.Unresolved, "missing-or-stale-decision-evidence");
        Assert.Empty(rules.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsoluteIntentDoesNotRequireSupportedLocalClockEvidence(bool absolute)
    {
        var context = ContractFixture.Context with
        {
            Intent = ContractFixture.Intent with
            {
                Mode = absolute ? PreservationMode.PreserveAbsoluteInstant : PreservationMode.PreserveLocalStart,
                LocalStart = DateTime.SpecifyKind(ContractFixture.Intent.LocalStart, DateTimeKind.Utc),
                Zone = "Unlisted/Zone"
            }
        };
        var rules = new FixtureRules(new(ResolutionKind.MissingRules, default));

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        AssertHeld(result, absolute ? AssessmentDisposition.NoOp : AssessmentDisposition.Unresolved,
            absolute ? "absolute-instant-preserved" : "unsupported-intent");
        Assert.Empty(rules.Calls);
    }

    [Theory]
    [InlineData(ResolutionKind.Overlap, "none")]
    [InlineData(ResolutionKind.Overlap, "outside")]
    [InlineData(ResolutionKind.Overlap, "first")]
    [InlineData(ResolutionKind.Overlap, "second")]
    [InlineData(ResolutionKind.Unique, "outside")]
    [InlineData(ResolutionKind.Unique, "first")]
    public void ExplicitSelectionMustAgreeWithTheResolverCandidates(ResolutionKind kind, string selection)
    {
        var first = ContractFixture.Patch.Start;
        var second = first.AddMinutes(30);
        DateTimeOffset? selected = selection switch
        {
            "none" => null,
            "outside" => first.AddHours(2),
            "first" => first,
            _ => second
        };
        var rules = new FixtureRules(new(kind, kind == ResolutionKind.Unique ? [first] : [first, second]));
        var context = ContractFixture.Context with { Intent = ContractFixture.Intent with { SelectedOverlapInstant = selected } };

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        if (selection is "none" or "outside")
        {
            AssertHeld(result, AssessmentDisposition.Unresolved, "nonunique-or-missing-rules");
        }
        else
        {
            Assert.Equal(AssessmentDisposition.Change, result.Disposition);
            Assert.Equal(selected!.Value, result.Patch!.Start);
            Assert.Equal(selected.Value.AddHours(1), result.Patch.End);
            Assert.Empty(result.Reasons);
        }
        Assert.Single(rules.Calls);
    }

    [Theory]
    [InlineData(ResolutionKind.Unique, "default")]
    [InlineData(ResolutionKind.Unique, "empty")]
    [InlineData(ResolutionKind.Unique, "multiple")]
    [InlineData(ResolutionKind.Overlap, "default")]
    [InlineData(ResolutionKind.Overlap, "empty")]
    [InlineData(ResolutionKind.Overlap, "single")]
    [InlineData(ResolutionKind.Gap, "default")]
    [InlineData(ResolutionKind.Gap, "single")]
    [InlineData(ResolutionKind.MissingRules, "default")]
    [InlineData(ResolutionKind.MissingRules, "single")]
    public void MissingOrMalformedRuleCandidatesFailClosed(ResolutionKind kind, string candidates)
    {
        var selected = ContractFixture.Patch.Start;
        var resolution = new LocalResolution(kind, candidates switch
        {
            "default" => default,
            "empty" => [],
            "single" => [selected],
            _ => [selected, selected.AddHours(1)]
        });
        var rules = new FixtureRules(resolution);
        var context = ContractFixture.Context with { Intent = ContractFixture.Intent with { SelectedOverlapInstant = selected } };

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        AssertHeld(result, AssessmentDisposition.Unresolved, "nonunique-or-missing-rules");
        Assert.Single(rules.Calls);
    }

    [Fact]
    public void ChangedStartPreservesExactElapsedDurationAndNormalizesPatchToUtc()
    {
        var duration = TimeSpan.FromMinutes(95).Add(TimeSpan.FromTicks(7));
        var source = ContractFixture.Source with
        {
            End = ContractFixture.Source.Start!.Value.Add(duration),
            RawEnd = new("2030-01-02T17:35:00.0000007", "UTC")
        };
        var target = ContractFixture.Patch.Start.ToOffset(TimeSpan.FromHours(-6));
        var rules = new FixtureRules(new(ResolutionKind.Unique, [target]));
        var context = ContractFixture.Context with { Discovery = ContractFixture.Discovery with { Events = [source] } };

        var result = new MeetingAssessor(rules).Assess(source, context);

        Assert.Equal(AssessmentDisposition.Change, result.Disposition);
        Assert.Equal(target.ToUniversalTime(), result.Patch!.Start);
        Assert.Equal(duration, result.Patch.End - result.Patch.Start);
        Assert.Equal(TimeSpan.Zero, result.Patch.Start.Offset);
        Assert.Equal(TimeSpan.Zero, result.Patch.End.Offset);
        Assert.Equal(ContractFixture.Intent.Zone, result.Patch.Zone);
        Assert.Single(rules.Calls);
    }

    [Fact]
    public void TargetEndOverflowIsHeldWithoutProducingAPatch()
    {
        var rules = new FixtureRules(new(ResolutionKind.Unique, [DateTimeOffset.MaxValue.AddMinutes(-30)]));

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, ContractFixture.Context);

        AssertHeld(result, AssessmentDisposition.Unresolved, "invalid-target-duration");
        Assert.Single(rules.Calls);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void TargetPolicyEffectiveInstantIsInclusive(long ticks)
    {
        var effective = ContractFixture.Now.AddHours(1);
        var rules = new FixtureRules(new(ResolutionKind.Unique, [effective.AddTicks(ticks)]));
        var context = ContractFixture.Context with { Policy = ContractFixture.Policy with { EffectiveFrom = effective } };

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        if (ticks < 0)
        {
            AssertHeld(result, AssessmentDisposition.Unresolved, "target-outside-policy");
        }
        else
        {
            Assert.Equal(AssessmentDisposition.Change, result.Disposition);
            Assert.Equal(effective.AddTicks(ticks), result.Patch!.Start);
        }
        Assert.Single(rules.Calls);
    }

    [Theory]
    [InlineData(false, -1)]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, -1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void TargetMustBeStrictlyAfterBothClockAndExecutionCutoff(bool cutoffIsLater, long ticks)
    {
        var boundary = ContractFixture.Now.AddHours(2);
        var context = ContractFixture.Context with
        {
            Now = cutoffIsLater ? ContractFixture.Now : boundary,
            Discovery = ContractFixture.Discovery with
            {
                Scope = ContractFixture.Scope with { ExecutionCutoff = cutoffIsLater ? boundary : ContractFixture.Now }
            }
        };
        var rules = new FixtureRules(new(ResolutionKind.Unique, [boundary.AddTicks(ticks)]));

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        if (ticks <= 0)
        {
            AssertHeld(result, AssessmentDisposition.Ineligible, "target-not-future");
        }
        else
        {
            Assert.Equal(AssessmentDisposition.Change, result.Disposition);
            Assert.Equal(boundary.AddTicks(ticks), result.Patch!.Start);
        }
        Assert.Single(rules.Calls);
    }

    [Fact]
    public void TargetOutsidePolicyWinsOverTargetNotFuture()
    {
        var rules = new FixtureRules(new(ResolutionKind.Unique, [ContractFixture.Now.AddTicks(-1)]));

        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, ContractFixture.Context);

        AssertHeld(result, AssessmentDisposition.Unresolved, "target-outside-policy");
        Assert.Single(rules.Calls);
    }

    private static void AssertHeld(Assessment result, AssessmentDisposition disposition, string reason)
    {
        Assert.Equal(disposition, result.Disposition);
        Assert.Null(result.Patch);
        Assert.Equal(reason, Assert.Single(result.Reasons));
    }
}
