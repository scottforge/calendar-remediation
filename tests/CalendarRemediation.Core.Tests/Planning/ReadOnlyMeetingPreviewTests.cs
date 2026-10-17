using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Tests.Planning;

public sealed class ReadOnlyMeetingPreviewTests
{
    private static readonly TimeZoneInfo MinusSix = TimeZoneInfo.CreateCustomTimeZone(
        "Fixture/PreviewMinus06", TimeSpan.FromHours(-6), "Preview minus six", "Preview minus six");

    [Fact]
    public void LocalCorrectionUsesTargetOffsetAndPreservesExactElapsedDuration()
    {
        var source = ContractFixture.Source;
        source = source with { End = source.Start!.Value.AddMinutes(73).AddTicks(1234) };

        var preview = ReadOnlyMeetingPreview.Calculate(source,
            new DateTime(2030, 1, 2, 9, 0, 0), MinusSix, false, ContractFixture.Now);

        Assert.Equal(PreviewDisposition.Proposed, preview.Disposition);
        Assert.Equal(source.Start, preview.CurrentStart);
        Assert.Equal(source.End, preview.CurrentEnd);
        Assert.Equal(ContractFixture.At("2030-01-02T15:00:00Z"), preview.ProposedStart);
        Assert.Equal(ContractFixture.At("2030-01-02T15:00:00Z").AddMinutes(73).AddTicks(1234), preview.ProposedEnd);
        Assert.Equal(MinusSix.Id, preview.TimeZoneId);
        Assert.False(string.IsNullOrWhiteSpace(preview.Reason));
    }

    [Fact]
    public void MatchingLocalTimeIsUnchanged()
    {
        var preview = ReadOnlyMeetingPreview.Calculate(ContractFixture.Source,
            new DateTime(2030, 1, 2, 10, 0, 0), MinusSix, false, ContractFixture.Now);

        Assert.Equal(PreviewDisposition.Unchanged, preview.Disposition);
        Assert.Equal(ContractFixture.Source.Start, preview.CurrentStart);
        Assert.Equal(ContractFixture.Source.End, preview.CurrentEnd);
    }

    [Theory]
    [InlineData(3, 10, 2, 30)]
    [InlineData(11, 3, 1, 30)]
    public void PreserveInstantIgnoresInvalidOrAmbiguousLocalTime(int month, int day, int hour, int minute)
    {
        var preview = ReadOnlyMeetingPreview.Calculate(ContractFixture.Source,
            new DateTime(2030, month, day, hour, minute, 0), DaylightZone(), true, ContractFixture.Now);

        Assert.Equal(PreviewDisposition.Unchanged, preview.Disposition);
        Assert.Equal(ContractFixture.Source.Start, preview.CurrentStart);
        Assert.Equal(ContractFixture.Source.End, preview.CurrentEnd);
    }

    [Theory]
    [InlineData(3, 10, 2, 30)]
    [InlineData(11, 3, 1, 30)]
    public void GapAndOverlapRequireResolution(int month, int day, int hour, int minute)
    {
        var preview = ReadOnlyMeetingPreview.Calculate(ContractFixture.Source,
            new DateTime(2030, month, day, hour, minute, 0), DaylightZone(), false, ContractFixture.Now);

        Assert.Equal(PreviewDisposition.Unresolved, preview.Disposition);
        AssertNoProposal(preview);
    }

    [Fact]
    public void DurationAcrossDaylightTransitionIsElapsedTimeRatherThanWallClockTime()
    {
        var source = ContractFixture.Source with
        {
            Start = ContractFixture.At("2030-03-10T09:00:00Z"),
            End = ContractFixture.At("2030-03-10T11:00:00Z")
        };

        var preview = ReadOnlyMeetingPreview.Calculate(source,
            new DateTime(2030, 3, 10, 1, 30, 0), DaylightZone(), false, ContractFixture.Now);

        Assert.Equal(PreviewDisposition.Proposed, preview.Disposition);
        Assert.Equal(ContractFixture.At("2030-03-10T07:30:00Z"), preview.ProposedStart);
        Assert.Equal(ContractFixture.At("2030-03-10T09:30:00Z"), preview.ProposedEnd);
    }

    [Theory]
    [InlineData("past")]
    [InlineData("all-day")]
    [InlineData("not-organizer")]
    [InlineData("occurrence")]
    [InlineData("exception")]
    [InlineData("series-master")]
    [InlineData("missing-start")]
    [InlineData("missing-end")]
    [InlineData("reversed")]
    [InlineData("zero-duration")]
    public void IneligibleSourceNeverProducesProposedEndpoints(string scenario)
    {
        var source = ContractFixture.Source;
        source = scenario switch
        {
            "past" => source with { Start = ContractFixture.Now.AddHours(-2), End = ContractFixture.Now.AddHours(-1) },
            "all-day" => source with { IsAllDay = true },
            "not-organizer" => source with { IsOrganizer = false },
            "occurrence" => source with { Kind = EventKind.Occurrence },
            "exception" => source with { Kind = EventKind.Exception },
            "series-master" => source with { Kind = EventKind.SeriesMaster },
            "missing-start" => source with { Start = null },
            "missing-end" => source with { End = null },
            "reversed" => source with { End = source.Start!.Value.AddTicks(-1) },
            "zero-duration" => source with { End = source.Start },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var preview = ReadOnlyMeetingPreview.Calculate(source,
            new DateTime(2030, 1, 2, 9, 0, 0), MinusSix, false, ContractFixture.Now);

        Assert.True(preview.Disposition is PreviewDisposition.Unresolved or PreviewDisposition.Unsupported);
        AssertNoProposal(preview);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void SourceStartMustBeStrictlyInTheFuture(int ticksFromNow)
    {
        var source = ContractFixture.Source with
        {
            Start = ContractFixture.Now.AddTicks(ticksFromNow),
            End = ContractFixture.Now.AddHours(1)
        };

        var preview = ReadOnlyMeetingPreview.Calculate(source,
            new DateTime(2030, 1, 2, 9, 0, 0), MinusSix, false, ContractFixture.Now);

        if (ticksFromNow > 0)
        {
            Assert.Equal(PreviewDisposition.Proposed, preview.Disposition);
        }
        else
        {
            AssertNoProposal(preview);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ProposedStartMustBeStrictlyInTheFuture(int ticksFromNow)
    {
        var intendedStart = ContractFixture.Now.UtcDateTime.AddTicks(ticksFromNow);
        intendedStart = DateTime.SpecifyKind(intendedStart, DateTimeKind.Unspecified);

        var preview = ReadOnlyMeetingPreview.Calculate(ContractFixture.Source,
            intendedStart, TimeZoneInfo.Utc, false, ContractFixture.Now);

        if (ticksFromNow > 0)
        {
            Assert.Equal(PreviewDisposition.Proposed, preview.Disposition);
            Assert.Equal(ContractFixture.Now.AddTicks(1), preview.ProposedStart);
            Assert.Equal(ContractFixture.Now.AddHours(1).AddTicks(1), preview.ProposedEnd);
        }
        else
        {
            AssertNoProposal(preview);
        }
    }

    [Fact]
    public void EndOverflowIsUnresolvedWithoutPartialProposal()
    {
        var preview = ReadOnlyMeetingPreview.Calculate(ContractFixture.Source,
            new DateTime(9999, 12, 31, 23, 30, 0), TimeZoneInfo.Utc, false, ContractFixture.Now);

        Assert.Equal(PreviewDisposition.Unresolved, preview.Disposition);
        AssertNoProposal(preview);
    }

    private static void AssertNoProposal(MeetingTimePreview preview)
    {
        Assert.NotEqual(PreviewDisposition.Proposed, preview.Disposition);
        Assert.Null(preview.ProposedStart);
        Assert.Null(preview.ProposedEnd);
        Assert.False(string.IsNullOrWhiteSpace(preview.Reason));
    }

    private static TimeZoneInfo DaylightZone()
    {
        var start = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 3, 2, DayOfWeek.Sunday);
        var end = TimeZoneInfo.TransitionTime.CreateFloatingDateRule(new DateTime(1, 1, 1, 2, 0, 0), 11, 1, DayOfWeek.Sunday);
        var rule = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2030, 1, 1), new DateTime(2030, 12, 31), TimeSpan.FromHours(1), start, end);
        return TimeZoneInfo.CreateCustomTimeZone("Fixture/PreviewDaylight", TimeSpan.FromHours(-6),
            "Preview daylight", "Preview standard", "Preview daylight", [rule]);
    }
}
