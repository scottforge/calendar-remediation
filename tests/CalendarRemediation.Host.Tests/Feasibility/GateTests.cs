using CalendarRemediation.Core.Feasibility;
using CalendarRemediation.Host.Feasibility;

namespace CalendarRemediation.Host.Tests.Feasibility;

public sealed class GateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly ArtifactReference Artifact = new("observation.json", new string('a', 64));
    private static readonly HashSet<string> Verified = [FeasibilityGate.ArtifactKey(Artifact)];

    [Fact]
    public void DefaultEvidenceNeverGrantsExit()
    {
        var result = FeasibilityGate.Assess(FeasibilityDefaults.Create(Now), Now, Verified);
        Assert.Equal(ProductVerdict.Blocked, result.Verdict);
        Assert.False(result.ExitAllowed);
        Assert.All(result.Gates, g => Assert.False(g.Passed));
    }

    [Fact]
    public void NullAndUnknownNestedClaimsReturnBlocked()
    {
        var evidence = Passing();
        var malformed = new[]
        {
            evidence with { WorkloadProfile = null! },
            evidence with { FeasibilityDecision = evidence.FeasibilityDecision with { Claims = [null!] } },
            evidence with { CategoryAdmissionMatrix = new(1, [null!]) },
            evidence with { FeasibilityDecision = evidence.FeasibilityDecision with
            {
                Claims = evidence.FeasibilityDecision.Claims.Concat([evidence.FeasibilityDecision.Claims[0] with { Gate = (Gate)999 }]).ToArray()
            } }
        };
        Assert.All(malformed, e => Assert.False(FeasibilityGate.Assess(e, Now, Verified).ExitAllowed));
    }

    [Fact]
    public void CompleteReviewedSyntheticEvidenceCanPassOnlyWithHumanGo()
    {
        var evidence = Passing();
        Assert.True(FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
        var withoutDecision = evidence with { FeasibilityDecision = evidence.FeasibilityDecision with { DecisionBy = null } };
        Assert.False(FeasibilityGate.Assess(withoutDecision, Now, Verified).ExitAllowed);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void CategoryAdmissionMustNotPredateItsLastObservationAndReview(int ticks, bool expectedExit)
    {
        var evidence = Passing();
        var availableAt = Now.AddHours(-1);
        var category = evidence.CategoryAdmissionMatrix.Categories[0];
        category = category with
        {
            ApprovedAt = availableAt.AddTicks(ticks),
            Claims = category.Claims.Select(c => c with { ObservedAt = availableAt, ReviewedAt = availableAt }).ToArray()
        };
        evidence = evidence with { CategoryAdmissionMatrix = new(1, [category]) };
        Assert.Equal(expectedExit, FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
    }

    [Fact]
    public void CategoryApprovalAfterObservationButBeforeReviewIsBlocked()
    {
        var evidence = Passing();
        var category = evidence.CategoryAdmissionMatrix.Categories[0] with { ApprovedAt = Now.AddHours(-1).AddTicks(-1) };
        evidence = evidence with { CategoryAdmissionMatrix = new(1, [category]) };
        Assert.False(FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
    }

    public static IEnumerable<object[]> DecisionEvidenceBoundaries => Enum.GetValues<Gate>()
        .SelectMany(gate => new[] { -1, 0, 1 }.Select(ticks => new object[] { gate, ticks }));

    [Theory]
    [MemberData(nameof(DecisionEvidenceBoundaries))]
    public void ProductDecisionMustNotPredateAnyReliedOnReviewIncludingModelApproval(Gate gate, int reviewTicks)
    {
        var evidence = WithEarlierDecision();
        var decisionAt = evidence.FeasibilityDecision.DecisionAt!.Value;
        evidence = evidence with
        {
            FeasibilityDecision = evidence.FeasibilityDecision with
            {
                Claims = evidence.FeasibilityDecision.Claims.Select(c => c.Gate == gate
                    ? c with { ReviewedAt = decisionAt.AddTicks(reviewTicks) } : c).ToArray()
            }
        };
        Assert.Equal(reviewTicks <= 0, FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
    }

    [Theory]
    [InlineData("policy", -1)]
    [InlineData("policy", 0)]
    [InlineData("policy", 1)]
    [InlineData("workload", -1)]
    [InlineData("workload", 0)]
    [InlineData("workload", 1)]
    [InlineData("category", -1)]
    [InlineData("category", 0)]
    [InlineData("category", 1)]
    public void ProductDecisionMustNotPredateDependencyApprovals(string dependency, int approvalTicks)
    {
        var evidence = WithEarlierDecision();
        var approvalAt = evidence.FeasibilityDecision.DecisionAt!.Value.AddTicks(approvalTicks);
        evidence = dependency switch
        {
            "policy" => evidence with { RuleEvidenceManifest = evidence.RuleEvidenceManifest with { ApprovedAt = approvalAt } },
            "workload" => evidence with { WorkloadProfile = evidence.WorkloadProfile with { AcceptedAt = approvalAt } },
            "category" => evidence with
            {
                CategoryAdmissionMatrix = new(1,
                [evidence.CategoryAdmissionMatrix.Categories[0] with { ApprovedAt = approvalAt }])
            },
            _ => throw new ArgumentException("Unexpected test dependency.", nameof(dependency))
        };
        Assert.Equal(approvalTicks <= 0, FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
    }

    [Fact]
    public void EqualInstantsWithDifferentOffsetsAndNullCategoryClaimsAreHandledSafely()
    {
        var evidence = WithEarlierDecision();
        evidence = evidence with
        {
            FeasibilityDecision = evidence.FeasibilityDecision with
            {
                DecisionAt = evidence.FeasibilityDecision.DecisionAt!.Value.ToOffset(TimeSpan.FromHours(5))
            }
        };
        Assert.True(FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
        var category = evidence.CategoryAdmissionMatrix.Categories[0];
        foreach (var claims in new EvidenceClaim[][] { null!, [null!], [category.Claims[0] with { ReviewedAt = null }] })
        {
            Assert.False(FeasibilityGate.Assess(evidence with
            {
                CategoryAdmissionMatrix = new(1, [category with { Claims = claims }])
            }, Now, Verified).ExitAllowed);
        }
    }

    private static FeasibilityEvidence WithEarlierDecision()
    {
        var evidence = Passing();
        var at = Now.AddMinutes(-30);
        return evidence with
        {
            FeasibilityDecision = evidence.FeasibilityDecision with { DecisionAt = at },
            RuleEvidenceManifest = evidence.RuleEvidenceManifest with { ApprovedAt = at },
            WorkloadProfile = evidence.WorkloadProfile with { AcceptedAt = at },
            CategoryAdmissionMatrix = new(1, [evidence.CategoryAdmissionMatrix.Categories[0] with { ApprovedAt = at }])
        };
    }

    [Theory]
    [InlineData(Gate.TenantIdentity)]
    [InlineData(Gate.ProtectedCache)]
    [InlineData(Gate.DefaultCalendar)]
    [InlineData(Gate.SupportedZones)]
    [InlineData(Gate.IdentityStability)]
    [InlineData(Gate.WindowsLaunch)]
    [InlineData(Gate.AtomicConditionalWrite)]
    [InlineData(Gate.Notifications)]
    [InlineData(Gate.Attribution)]
    [InlineData(Gate.ModelPrivacy)]
    [InlineData(Gate.RulePolicy)]
    [InlineData(Gate.Workload)]
    [InlineData(Gate.UsefulMeetingCategory)]
    public void EveryRequiredGateBlocksWhenPlanned(Gate gate)
    {
        var evidence = Passing();
        var claims = evidence.FeasibilityDecision.Claims.Select(c => c.Gate == gate ? c with { State = EvidenceState.Planned } : c).ToArray();
        evidence = evidence with { FeasibilityDecision = evidence.FeasibilityDecision with { Claims = claims } };
        Assert.False(FeasibilityGate.Assess(evidence, Now, Verified).ExitAllowed);
    }

    [Fact]
    public void WrongScopeDuplicateClaimsFutureReviewAndUnverifiedArtifactsFailClosed()
    {
        var evidence = Passing();
        var decision = evidence.FeasibilityDecision;
        var badClaims = new[]
        {
            decision.Claims.Select(c => c with { MailboxId = Guid.NewGuid().ToString() }).ToArray(),
            decision.Claims.Concat([decision.Claims[0]]).ToArray(),
            decision.Claims.Select(c => c with { ReviewedAt = Now.AddDays(1) }).ToArray(),
            decision.Claims.Select(c => c with { ObservedAt = Now.AddDays(1) }).ToArray(),
            decision.Claims.Select(c => c with { ReviewedBy = "" }).ToArray(),
            decision.Claims.Select(c => c with { Artifacts = [Artifact with { Path = "missing.json" }] }).ToArray()
        };
        foreach (var claims in badClaims)
        {
            Assert.False(FeasibilityGate.Assess(evidence with { FeasibilityDecision = decision with { Claims = claims } }, Now, Verified).ExitAllowed);
        }
        Assert.False(FeasibilityGate.Assess(evidence, Now, new HashSet<string>()).ExitAllowed);
    }

    [Fact]
    public void FailedGuardForcesNoGoEvenWhenHumanRequestsGo()
    {
        var evidence = Passing();
        evidence = evidence with
        {
            FeasibilityDecision = evidence.FeasibilityDecision with
            {
                Claims = evidence.FeasibilityDecision.Claims.Select(c => c.Gate == Gate.AtomicConditionalWrite ? c with { State = EvidenceState.ObservedFail } : c).ToArray()
            }
        };
        Assert.Equal(ProductVerdict.NoGo, FeasibilityGate.Assess(evidence, Now, Verified).Verdict);
    }

    [Fact]
    public void ExpiredTimeBoxMissingRulesIncompleteWorkloadAndAppointmentOnlyCannotPass()
    {
        var evidence = Passing();
        var invalid = new[]
        {
            evidence with { FeasibilityDecision = evidence.FeasibilityDecision with { StopDate = Now.AddSeconds(-1) } },
            evidence with { RuleEvidenceManifest = evidence.RuleEvidenceManifest with { SourceRules = [] } },
            evidence with { RuleEvidenceManifest = evidence.RuleEvidenceManifest with { ApprovedBy = null } },
            evidence with { WorkloadProfile = evidence.WorkloadProfile with { Complete = false } },
            evidence with { WorkloadProfile = evidence.WorkloadProfile with { ReviewSecondsPerMeeting = null } },
            evidence with { WorkloadProfile = evidence.WorkloadProfile with { DetailReads = 0 } },
            evidence with { WorkloadProfile = evidence.WorkloadProfile with { ElapsedSeconds = double.NaN } },
            evidence with { CategoryAdmissionMatrix = new(1, [evidence.CategoryAdmissionMatrix.Categories[0] with { Category = "AttendeeFree" }]) }
        };
        Assert.All(invalid, e => Assert.False(FeasibilityGate.Assess(e, Now, Verified).ExitAllowed));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(366, true)]
    [InlineData(367, false)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void WorkloadWindowMustBePositiveAndAtMost366Days(int days, bool expectedExit)
    {
        var workload = Passing().WorkloadProfile;
        AssertWorkload(workload with { End = workload.Start.AddDays(days) }, expectedExit);
    }

    [Fact]
    public void WorkloadWindowCannotExceed366DaysEvenByOneTick()
    {
        var workload = Passing().WorkloadProfile;
        AssertWorkload(workload with { End = workload.Start.AddDays(366).AddTicks(1) }, false);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("default-start")]
    [InlineData("zero-events")]
    [InlineData("zero-organizer-meetings")]
    [InlineData("organizer-meetings-exceed-events")]
    [InlineData("zero-pages")]
    [InlineData("detail-reads-below-events")]
    [InlineData("negative-throttles")]
    public void WorkloadRequiresCompleteRepresentativeCounts(string invalidField)
    {
        var workload = Passing().WorkloadProfile;
        workload = invalidField switch
        {
            "incomplete" => workload with { Complete = false },
            "default-start" => workload with { Start = default, End = default(DateTimeOffset).AddDays(1) },
            "zero-events" => workload with { Events = 0 },
            "zero-organizer-meetings" => workload with { OrganizerMeetings = 0 },
            "organizer-meetings-exceed-events" => workload with { OrganizerMeetings = workload.Events + 1 },
            "zero-pages" => workload with { Pages = 0 },
            "detail-reads-below-events" => workload with { DetailReads = workload.Events - 1 },
            "negative-throttles" => workload with { ThrottledResponses = -1 },
            _ => throw new ArgumentException("Unexpected workload field.", nameof(invalidField))
        };
        AssertWorkload(workload, false);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    [InlineData(double.NegativeInfinity, false)]
    [InlineData(0.5, true)]
    public void WorkloadElapsedAndReviewEffortMustEachBeFiniteAndPositive(double seconds, bool expectedExit)
    {
        var workload = Passing().WorkloadProfile;
        AssertWorkload(workload with { ElapsedSeconds = seconds }, expectedExit);
        AssertWorkload(workload with { ReviewSecondsPerMeeting = seconds }, expectedExit);
    }

    [Fact]
    public void WorkloadRequiresMeasuredReviewEffort()
    {
        AssertWorkload(Passing().WorkloadProfile with { ReviewSecondsPerMeeting = null }, false);
    }

    [Theory]
    [InlineData("events", -1, false)]
    [InlineData("events", 0, true)]
    [InlineData("pages", -1, false)]
    [InlineData("pages", 0, true)]
    [InlineData("detail-reads", -1, false)]
    [InlineData("detail-reads", 0, true)]
    [InlineData("runtime", -1, false)]
    [InlineData("runtime", 0, true)]
    public void WorkloadBudgetsMustCoverMeasuredUsage(string budget, int delta, bool expectedExit)
    {
        var workload = Passing().WorkloadProfile;
        workload = budget switch
        {
            "events" => workload with { ProposedMaxEvents = workload.Events + delta },
            "pages" => workload with { ProposedMaxPages = workload.Pages + delta },
            "detail-reads" => workload with { ProposedMaxDetailReads = workload.DetailReads + delta },
            "runtime" => workload with { ProposedRunSeconds = (int)workload.ElapsedSeconds + delta },
            _ => throw new ArgumentException("Unexpected workload budget.", nameof(budget))
        };
        AssertWorkload(workload, expectedExit);
    }

    [Theory]
    [InlineData("events", 10000, true)]
    [InlineData("events", 10001, false)]
    [InlineData("pages", 1000, true)]
    [InlineData("pages", 1001, false)]
    [InlineData("detail-reads", 10000, true)]
    [InlineData("detail-reads", 10001, false)]
    [InlineData("runtime", 3600, true)]
    [InlineData("runtime", 3601, false)]
    [InlineData("selected-writes", 0, false)]
    [InlineData("selected-writes", 2, true)]
    [InlineData("selected-writes", 3, false)]
    public void WorkloadBudgetsRespectInclusivePolicyCaps(string budget, int value, bool expectedExit)
    {
        var workload = Passing().WorkloadProfile;
        workload = budget switch
        {
            "events" => workload with { ProposedMaxEvents = value },
            "pages" => workload with { ProposedMaxPages = value },
            "detail-reads" => workload with { ProposedMaxDetailReads = value },
            "runtime" => workload with { ProposedRunSeconds = value },
            "selected-writes" => workload with { ProposedSelectedWrites = value },
            _ => throw new ArgumentException("Unexpected workload budget.", nameof(budget))
        };
        AssertWorkload(workload, expectedExit);
    }

    [Theory]
    [InlineData("missing-context")]
    [InlineData("missing-measurements")]
    [InlineData("unverified-measurement")]
    [InlineData("missing-operator")]
    [InlineData("missing-acceptance")]
    [InlineData("future-acceptance")]
    public void WorkloadRequiresVerifiedMeasurementsAndSignedAcceptance(string missingEvidence)
    {
        var workload = Passing().WorkloadProfile;
        workload = missingEvidence switch
        {
            "missing-context" => workload with { MeasurementContext = " " },
            "missing-measurements" => workload with { Measurements = [] },
            "unverified-measurement" => workload with { Measurements = [Artifact with { Path = "unverified-workload.json" }] },
            "missing-operator" => workload with { AcceptedBy = " " },
            "missing-acceptance" => workload with { AcceptedAt = null },
            "future-acceptance" => workload with { AcceptedAt = Now.AddTicks(1) },
            _ => throw new ArgumentException("Unexpected workload evidence.", nameof(missingEvidence))
        };
        AssertWorkload(workload, false);
    }

    private static void AssertWorkload(WorkloadProfile workload, bool expectedExit)
    {
        var evidence = Passing();
        var baseline = FeasibilityGate.Assess(evidence, Now, Verified);
        Assert.Equal(ProductVerdict.Go, baseline.Verdict);
        Assert.True(baseline.ExitAllowed);
        Assert.Empty(baseline.Blockers);

        // Acceptance stays at or before the fixture's human decision, except when testing future acceptance.
        var result = FeasibilityGate.Assess(evidence with { WorkloadProfile = workload }, Now, Verified);
        const string workloadBlocker = "Representative complete workload, review effort and accepted measured budgets are required.";
        Assert.Equal(expectedExit ? ProductVerdict.Go : ProductVerdict.Blocked, result.Verdict);
        Assert.Equal(expectedExit, result.ExitAllowed);
        Assert.All(result.Gates, gate => Assert.True(gate.Passed));
        if (expectedExit)
        {
            Assert.Empty(result.Blockers);
        }
        else
        {
            Assert.Contains(workloadBlocker, result.Blockers);
        }
    }

    [Fact]
    public void ArtifactImportDetectsTampering()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "observation.json");
            File.WriteAllText(path, "synthetic observation");
            var artifact = EvidenceFiles.Reference(path, directory);
            var passing = Passing();
            var json = System.Text.Json.JsonSerializer.Serialize(passing, EvidenceFiles.JsonOptions)
                .Replace(Artifact.Sha256, artifact.Sha256, StringComparison.Ordinal);
            var evidencePath = Path.Combine(directory, "evidence.json");
            File.WriteAllText(evidencePath, json);
            Assert.True(EvidenceFiles.Assess(evidencePath, Now).ExitAllowed);
            File.WriteAllText(path, "modified after review");
            Assert.False(EvidenceFiles.Assess(evidencePath, Now).ExitAllowed);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static FeasibilityEvidence Passing()
    {
        // Entirely synthetic. This fixture is not provider or policy proof.
        var tenant = "11111111-1111-1111-1111-111111111111";
        var mailbox = "22222222-2222-2222-2222-222222222222";
        var claims = Enum.GetValues<Gate>().Select(g => new EvidenceClaim(g, EvidenceState.ObservedPass,
            tenant, mailbox, "Synthetic owner", Now.AddHours(-2), "Synthetic test method", "Synthetic finding",
            [Artifact], "Synthetic reviewer", Now.AddHours(-1))).ToArray();
        return new(new(1, tenant, mailbox, "Synthetic owner", Now.AddDays(1), "/v1.0/me/events/{immutableId}", "v1.0",
            ProductVerdict.Go, "Synthetic decision maker", Now, claims, "Bounded investigation"),
            new(1, "Candidate baseline requires confirmation", "Ask preservation choice", "Require target rules", ["Synthetic"],
                [Artifact], [Artifact], [Artifact], [Artifact], "Synthetic policy owner", Now),
            new(1, Now, Now.AddDays(30), true, 2, 1, 1, 2, 0, 5, 30, 2, 1, 2, 1, 60, "Synthetic calendar", [Artifact], "Synthetic operator", Now),
            new(1, [new("OrganizerInternalAttendeesSingleInstance", true, "Synthetic category owner", Now,
                "Synthetic notification observation", "Delivery is not guaranteed", claims)]));
    }
}
