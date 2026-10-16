using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarRemediation.Graph.Feasibility;

namespace CalendarRemediation.Tests.AdapterContracts;

public sealed class ConditionalProbePolicyTests
{
    internal const string Marker = "33333333-3333-3333-3333-333333333333";
    internal static readonly DateTimeOffset Now = new(2029, 12, 31, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProposedStart = Now.AddDays(2);

    internal static PreparedProbe Prepared() => new(
        "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222",
        "event", Marker, Now, Now.AddMinutes(30), "\"v1\"", Event(),
        new("GET", "/me/events/event", Now, Guid.Empty, 200, null, null, 1),
        "Synthetic experiment owner", Now.AddDays(1));

    internal static JsonElement Event(Action<JsonObject>? edit = null, string version = "v1")
    {
        var item = JsonNode.Parse(ScriptedGraphHandler.Event(version: version, marker: Marker).GetRawText())!.AsObject();
        edit?.Invoke(item);
        return JsonSerializer.SerializeToElement(item);
    }

    [Fact]
    public void ValidPreparedObservationAndDispatchAreAllowedUntilTheLastTick()
    {
        var prepared = Prepared();
        ConditionalProbePolicy.ValidatePrepared(prepared, ProposedStart, ProposedStart.AddHours(1), Now);
        ConditionalProbePolicy.ValidateDispatch(prepared, prepared.ExpiresAt.AddTicks(-1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ExpirationIsExclusiveForPreparationAndDispatch(long ticksAfterExpiry)
    {
        var prepared = Prepared();
        var now = prepared.ExpiresAt.AddTicks(ticksAfterExpiry);
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidatePrepared(
            prepared, ProposedStart, ProposedStart.AddHours(1), now));
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateDispatch(prepared, now));
    }

    [Fact]
    public void FuturePreparationEvenByOneTickIsRejected()
    {
        var prepared = Prepared() with { PreparedAt = Now.AddTicks(1) };
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidatePrepared(
            prepared, ProposedStart, ProposedStart.AddHours(1), Now));
    }

    [Theory]
    [InlineData("missing-prepared-at")]
    [InlineData("expiry-cap")]
    [InlineData("expiry-after-stop")]
    [InlineData("stop-cap")]
    [InlineData("stopped")]
    [InlineData("owner")]
    public void PreparationRejectsInvalidExperimentLifetimeOrOwnership(string scenario)
    {
        var baseline = Prepared();
        var prepared = scenario switch
        {
            "missing-prepared-at" => baseline with { PreparedAt = default },
            "expiry-cap" => baseline with { ExpiresAt = Now.AddMinutes(30).AddTicks(1) },
            "expiry-after-stop" => baseline with { ExperimentStopDate = baseline.ExpiresAt.AddTicks(-1) },
            "stop-cap" => baseline with { ExperimentStopDate = Now.AddDays(14).AddTicks(1) },
            "stopped" => baseline with { ExperimentStopDate = Now },
            "owner" => baseline with { ExperimentOwner = " " },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidatePrepared(
            prepared, ProposedStart, ProposedStart.AddHours(1), Now));
    }

    [Fact]
    public void DispatchRejectsTheExactExperimentStopEvenIfExpiryIsLater()
    {
        var prepared = Prepared() with { ExperimentStopDate = Now.AddMinutes(10) };
        ConditionalProbePolicy.ValidateDispatch(prepared, prepared.ExperimentStopDate.AddTicks(-1));
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateDispatch(prepared, prepared.ExperimentStopDate));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(60, 60)]
    [InlineData(60, 59)]
    public void ProposedIntervalMustBeFutureAndStrictlyIncreasing(int startMinutes, int endMinutes)
    {
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidatePrepared(
            Prepared(), Now.AddMinutes(startMinutes), Now.AddMinutes(endMinutes), Now));
    }

    [Theory]
    [InlineData("change-key-only")]
    [InlineData("mismatched")]
    [InlineData("wildcard")]
    [InlineData("unquoted")]
    public void SourceGuardMustBeAnExactEntityTagNotAChangeKey(string scenario)
    {
        var source = Event(item =>
        {
            if (scenario == "change-key-only")
            {
                item.Remove("@odata.etag");
            }
            else
            {
                item["@odata.etag"] = scenario switch
                {
                    "wildcard" => "*",
                    "unquoted" => "v1",
                    _ => "\"v1\""
                };
            }
        });
        var prepared = Prepared() with
        {
            Source = source,
            SourceETag = scenario switch
            {
                "mismatched" => "\"other\"",
                "wildcard" => "*",
                "unquoted" => "v1",
                _ => "\"v1\""
            }
        };
        Assert.Throws<InvalidDataException>(() => ConditionalProbePolicy.ValidatePrepared(
            prepared, ProposedStart, ProposedStart.AddHours(1), Now));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void CompetingEditNeedsChangedGuardVersionAndSchedule(bool guardChanged, bool versionChanged, bool timeChanged)
    {
        var competitor = Event(item =>
        {
            item["@odata.etag"] = guardChanged ? "\"v2\"" : "\"v1\"";
            item["changeKey"] = versionChanged ? "v2" : "v1";
        }, timeChanged ? "v2" : "v1");
        if (guardChanged && versionChanged && timeChanged)
        {
            ConditionalProbePolicy.ValidateCompetingEdit(Prepared(), competitor);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateCompetingEdit(Prepared(), competitor));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("*")]
    [InlineData("v2")]
    public void CompetingGuardMustAlsoBeAUsableEntityTag(string? guard)
    {
        var competitor = Event(item => item["@odata.etag"] = guard, "v2");
        Assert.Throws<InvalidDataException>(() => ConditionalProbePolicy.ValidateCompetingEdit(Prepared(), competitor));
    }

    [Theory]
    [InlineData("id", "other")]
    [InlineData("subject", "ordinary appointment")]
    [InlineData("type", "occurrence")]
    [InlineData("changeKey", " ")]
    public void DisposableIdentityAndSingleInstanceRequirementsAreEnforced(string field, string value)
    {
        var source = Event(item => item[field] = value);
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateDisposable(source, "event", Marker, Now));
    }

    [Theory]
    [InlineData("isOrganizer", false)]
    [InlineData("isAllDay", true)]
    [InlineData("isCancelled", true)]
    public void DisposableMustBeOrganizerOwnedActiveAndTimed(string field, bool value)
    {
        var source = Event(item => item[field] = value);
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateDisposable(source, "event", Marker, Now));
    }

    [Fact]
    public void PreparedSourceMustStillBeDisposable()
    {
        var prepared = Prepared() with { Source = ScriptedGraphHandler.Event(marker: Marker, attendees: true) };
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidatePrepared(
            prepared, ProposedStart, ProposedStart.AddHours(1), Now));
    }

    [Theory]
    [InlineData("start", "2030-01-01", "UTC")]
    [InlineData("end", "2030-01-01", "UTC")]
    [InlineData("start", "09:00:00", "UTC")]
    [InlineData("start", "2030-01-01T09:00", "UTC")]
    [InlineData("start", "not-a-time", "UTC")]
    [InlineData("start", "2030-02-30T09:00:00", "UTC")]
    [InlineData("start", "2030-01-01T09:00:00", "Europe/London")]
    [InlineData("start", "2030-01-01T09:00:00+01:00", "UTC")]
    public void MalformedPartialOrNonUtcObservationsAreInvalidData(string field, string timestamp, string zone)
    {
        var source = Event(item => item[field] = new JsonObject { ["dateTime"] = timestamp, ["timeZone"] = zone });
        Assert.Throws<InvalidDataException>(() => ConditionalProbePolicy.ValidateDisposable(source, "event", Marker, Now));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    public void MissingSchedulingObservationIsInvalidData(string field)
    {
        var source = Event(item => item.Remove(field));
        Assert.Throws<InvalidDataException>(() => ConditionalProbePolicy.ValidateDisposable(source, "event", Marker, Now));
    }

    [Theory]
    [InlineData("2030-01-01T09:00:00")]
    [InlineData("2030-01-01T09:00:00Z")]
    [InlineData("2030-01-01T09:00:00.1234567")]
    [InlineData("2030-01-01T09:00:00+00:00")]
    public void CompleteUtcTimestampsAreValid(string timestamp)
    {
        var source = Event(item => item["start"]!["dateTime"] = timestamp);
        ConditionalProbePolicy.ValidateDisposable(source, "event", Marker, Now);
    }

    [Fact]
    public void DisposableStartAtNowIsAlreadyIneligible()
    {
        var start = new DateTimeOffset(2030, 1, 1, 9, 0, 0, TimeSpan.Zero);
        ConditionalProbePolicy.ValidateDisposable(Event(), "event", Marker, start.AddTicks(-1));
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateDisposable(Event(), "event", Marker, start));
    }

    [Theory]
    [InlineData("2030-01-01T09:00:00")]
    [InlineData("2030-01-01T08:59:59")]
    public void DisposableEndMustBeAfterStart(string end)
    {
        var source = Event(item => item["end"]!["dateTime"] = end);
        Assert.Throws<InvalidOperationException>(() => ConditionalProbePolicy.ValidateDisposable(source, "event", Marker, Now));
    }

    [Theory]
    [InlineData("start")]
    [InlineData("end")]
    public void CompetingEditDoesNotAcceptPartialSchedulingObservations(string field)
    {
        var competitor = Event(item => item[field]!["dateTime"] = "2030-01-01", "v2");
        Assert.Throws<InvalidDataException>(() => ConditionalProbePolicy.ValidateCompetingEdit(Prepared(), competitor));
    }

    [Theory]
    [InlineData(200, true, nameof(ConditionalProbeOutcomeKind.StaleAccepted))]
    [InlineData(200, false, nameof(ConditionalProbeOutcomeKind.StaleAccepted))]
    [InlineData(204, true, nameof(ConditionalProbeOutcomeKind.StaleAccepted))]
    [InlineData(299, false, nameof(ConditionalProbeOutcomeKind.StaleAccepted))]
    [InlineData(200, null, nameof(ConditionalProbeOutcomeKind.StaleAcceptedReadbackUnknown))]
    [InlineData(204, null, nameof(ConditionalProbeOutcomeKind.StaleAcceptedReadbackUnknown))]
    [InlineData(412, true, nameof(ConditionalProbeOutcomeKind.ObservedStaleRejection))]
    [InlineData(412, false, nameof(ConditionalProbeOutcomeKind.Inconclusive))]
    [InlineData(412, null, nameof(ConditionalProbeOutcomeKind.Unknown))]
    [InlineData(199, true, nameof(ConditionalProbeOutcomeKind.Inconclusive))]
    [InlineData(300, false, nameof(ConditionalProbeOutcomeKind.Inconclusive))]
    [InlineData(429, true, nameof(ConditionalProbeOutcomeKind.Inconclusive))]
    [InlineData(500, false, nameof(ConditionalProbeOutcomeKind.Inconclusive))]
    [InlineData(500, null, nameof(ConditionalProbeOutcomeKind.Unknown))]
    [InlineData(null, null, nameof(ConditionalProbeOutcomeKind.Unknown))]
    [InlineData(null, true, nameof(ConditionalProbeOutcomeKind.Unknown))]
    [InlineData(null, false, nameof(ConditionalProbeOutcomeKind.Unknown))]
    public void OutcomeRequiresBothResponseAndReadbackEvidence(int? status, bool? preserved, string expected)
    {
        Assert.Equal(expected, ConditionalProbeOutcome.Classify(status, preserved).ToString());
    }
}
