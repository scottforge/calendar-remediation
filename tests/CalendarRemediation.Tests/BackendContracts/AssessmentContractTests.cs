using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Intent;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class AssessmentContractTests
{
    [Fact]
    public void ConfirmedNineLocalMovesOldMinusSeven1600ZToNewMinusSix1500Z()
    {
        var rules = new FixtureRules();
        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, ContractFixture.Context);

        Assert.Equal(AssessmentDisposition.Change, result.Disposition);
        Assert.Equal(ContractFixture.At("2030-01-02T15:00:00Z"), result.Patch!.Start);
        Assert.Equal(ContractFixture.At("2030-01-02T16:00:00Z"), result.Patch.End);
        Assert.Equal(ContractFixture.Dependencies, result.Dependencies);
        var call = Assert.Single(rules.Calls);
        Assert.Equal(ContractFixture.Intent.LocalStart, call.Local);
        Assert.Equal(ContractFixture.Policy, call.Policy);
        Assert.Equal("Fixture/NewMinus06", call.Zone);
    }

    [Fact]
    public void AbsoluteIntentPreserves1600ZWithoutConsultingWallClockRules()
    {
        var rules = new FixtureRules(new(ResolutionKind.MissingRules, []));
        var context = ContractFixture.Context with { Intent = ContractFixture.Intent with { Mode = PreservationMode.PreserveAbsoluteInstant } };
        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        Assert.Equal(AssessmentDisposition.NoOp, result.Disposition);
        Assert.Null(result.Patch);
        Assert.Empty(rules.Calls);
    }

    [Fact]
    public void AlreadyCorrectLocalStartIsNoop()
    {
        var source = ContractFixture.Source with
        {
            Start = ContractFixture.Patch.Start,
            End = ContractFixture.Patch.End,
            RawStart = new("2030-01-02T15:00:00", "UTC"),
            RawEnd = new("2030-01-02T16:00:00", "UTC")
        };
        var context = ContractFixture.Context with { Discovery = ContractFixture.Discovery with { Events = [source] } };
        var result = new MeetingAssessor(new FixtureRules()).Assess(source, context);

        Assert.Equal(AssessmentDisposition.NoOp, result.Disposition);
        Assert.Null(result.Patch);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("rules")]
    [InlineData("adoption")]
    public void MissingDecisionEvidenceCannotBeInferredFromUtcOrZoneLabel(string missing)
    {
        var context = missing switch
        {
            "intent" => ContractFixture.Context with { Intent = null },
            "rules" => ContractFixture.Context with { Policy = null },
            _ => ContractFixture.Context with { Policy = ContractFixture.Policy with { AdoptionVerified = false } }
        };
        var result = new MeetingAssessor(new FixtureRules()).Assess(ContractFixture.Source, context);

        Assert.Equal(AssessmentDisposition.Unresolved, result.Disposition);
        Assert.Null(result.Patch);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData("not-owner")]
    [InlineData("all-day")]
    [InlineData("recurrence")]
    [InlineData("resource")]
    [InlineData("external")]
    [InlineData("cancelled")]
    [InlineData("past")]
    [InlineData("unknown-attendee")]
    public void PilotExclusionsRemainVisibleWithoutExecutablePatch(string category)
    {
        var source = category switch
        {
            "not-owner" => ContractFixture.Source with { IsOrganizer = false },
            "all-day" => ContractFixture.Source with { IsAllDay = true },
            "recurrence" => ContractFixture.Source with { Kind = EventKind.Occurrence, SeriesMasterId = "master" },
            "resource" => ContractFixture.Source with { Attendees = [new("room@example.test", AttendeeKind.Resource)] },
            "external" => ContractFixture.Source with { Attendees = [new("person@outside.test", AttendeeKind.External)] },
            "cancelled" => ContractFixture.Source with { IsCancelled = true },
            "past" => ContractFixture.Source with
            {
                Start = ContractFixture.Now.AddHours(-2),
                End = ContractFixture.Now.AddHours(-1),
                RawStart = new("2029-12-31T22:00:00", "UTC"),
                RawEnd = new("2029-12-31T23:00:00", "UTC")
            },
            _ => ContractFixture.Source with { Attendees = [new("person@example.test", AttendeeKind.Unknown)] }
        };
        var context = ContractFixture.Context with { Discovery = ContractFixture.Discovery with { Events = [source] } };
        var result = new MeetingAssessor(new FixtureRules()).Assess(source, context);

        var expected = category switch
        {
            "not-owner" or "cancelled" or "past" => AssessmentDisposition.Ineligible,
            "unknown-attendee" => AssessmentDisposition.Unresolved,
            _ => AssessmentDisposition.Unsupported
        };
        Assert.Equal(expected, result.Disposition);
        Assert.Equal(source.Id, result.Source.Id);
        Assert.Null(result.Patch);
        Assert.NotEmpty(result.Reasons);
    }

    [Theory]
    [InlineData(ResolutionKind.Gap)]
    [InlineData(ResolutionKind.Overlap)]
    public void NonuniqueLocalResolutionRequiresNewExplicitIntent(ResolutionKind kind)
    {
        var candidates = kind == ResolutionKind.Gap ? Array.Empty<DateTimeOffset>()
            : new[] { ContractFixture.Patch.Start, ContractFixture.Patch.Start.AddHours(1) };
        var result = new MeetingAssessor(new FixtureRules(new(kind, [.. candidates])))
            .Assess(ContractFixture.Source, ContractFixture.Context);

        Assert.Equal(AssessmentDisposition.Unresolved, result.Disposition);
        Assert.Null(result.Patch);
        Assert.NotEmpty(result.Reasons);
    }

    [Fact]
    public void ExplicitOverlapSelectionUsesTheSelectedCandidateAndElapsedDuration()
    {
        var selected = ContractFixture.Patch.Start.AddHours(-1);
        var rules = new FixtureRules(new(ResolutionKind.Overlap, [selected, ContractFixture.Patch.Start]));
        var context = ContractFixture.Context with { Intent = ContractFixture.Intent with { SelectedOverlapInstant = selected } };
        var result = new MeetingAssessor(rules).Assess(ContractFixture.Source, context);

        Assert.Equal(AssessmentDisposition.Change, result.Disposition);
        Assert.Equal(selected, result.Patch!.Start);
        Assert.Equal(selected.AddHours(1), result.Patch.End);
    }

    [Fact]
    public void ZeroElapsedDurationIsUnresolved()
    {
        var source = ContractFixture.Source with { End = ContractFixture.Source.Start, RawEnd = ContractFixture.Source.RawStart };
        var context = ContractFixture.Context with { Discovery = ContractFixture.Discovery with { Events = [source] } };
        var result = new MeetingAssessor(new FixtureRules()).Assess(source, context);

        Assert.Equal(AssessmentDisposition.Unresolved, result.Disposition);
        Assert.Null(result.Patch);
    }

    [Fact]
    public void IntentForAnotherSourceVersionOrMailboxDoesNotAuthorizeAssessment()
    {
        var context = ContractFixture.Context with { Intent = ContractFixture.Intent with { SourceVersion = "old-version" } };
        var result = new MeetingAssessor(new FixtureRules()).Assess(ContractFixture.Source, context);

        Assert.Equal(AssessmentDisposition.Unresolved, result.Disposition);
        Assert.Null(result.Patch);
        var otherMailbox = ContractFixture.Context with
        {
            Intent = ContractFixture.Intent with { Identity = ContractFixture.Identity with { MailboxId = "other-mailbox" } }
        };
        var crossMailbox = new MeetingAssessor(new FixtureRules()).Assess(ContractFixture.Source, otherMailbox);
        Assert.Equal(AssessmentDisposition.Unresolved, crossMailbox.Disposition);
        Assert.Null(crossMailbox.Patch);
    }
}
