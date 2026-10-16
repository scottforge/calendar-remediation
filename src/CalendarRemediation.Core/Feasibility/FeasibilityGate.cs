namespace CalendarRemediation.Core.Feasibility;

public static class FeasibilityGate
{
    private const string RequiredCategory = "OrganizerInternalAttendeesSingleInstance";

    public static string ArtifactKey(ArtifactReference reference) => reference.Path + "|" + reference.Sha256;

    public static GateAssessment Assess(FeasibilityEvidence evidence, DateTimeOffset now,
        IReadOnlySet<string> verifiedArtifactHashes)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(verifiedArtifactHashes);

        if (!HasRequiredRecords(evidence))
        {
            return new(ProductVerdict.Blocked, false, [], ["All four evidence records are required."]);
        }

        var decision = evidence.FeasibilityDecision;
        var rules = evidence.RuleEvidenceManifest;
        var workload = evidence.WorkloadProfile;
        var matrix = evidence.CategoryAdmissionMatrix;
        var blockers = new List<string>();

        if (decision.SchemaVersion != 1 || rules.SchemaVersion != 1
            || workload.SchemaVersion != 1 || matrix.SchemaVersion != 1)
        {
            blockers.Add("Unsupported evidence schema.");
        }

        ValidateDecision(decision, now, blockers);
        ValidateInputShape(decision, matrix, blockers);

        var claims = (decision.Claims ?? []).Where(claim => claim is not null).ToArray();
        bool ClaimIsValid(EvidenceClaim claim) => IsValidClaim(claim, decision, now, verifiedArtifactHashes);
        var gateResults = AssessGates(claims, ClaimIsValid);

        if (!IsValidRuleManifest(rules, now, verifiedArtifactHashes))
        {
            blockers.Add("Rule policy, authoritative sources, exact source/target artifacts and mappings need approval and integrity verification.");
        }

        if (!IsValidWorkload(workload, now, verifiedArtifactHashes))
        {
            blockers.Add("Representative complete workload, review effort and accepted measured budgets are required.");
        }

        var categories = (matrix.Categories ?? []).Where(category => category is not null).ToArray();
        bool CategoryIsApproved(CategoryAdmission category) => IsApprovedCategory(category, now, ClaimIsValid);
        ValidateCategories(categories, now, CategoryIsApproved, blockers);
        blockers.AddRange(gateResults.Where(result => !result.Passed)
            .Select(result => $"{result.Gate}: {result.Reason}"));

        if (!HasHumanGoDecision(decision, claims, rules, workload, categories, now))
        {
            blockers.Add("An explicit human go decision at or after all evidence reviews, category admissions, policy and workload approvals is required. Model privacy approval is included in the evidence reviews.");
        }

        var failedGuard = claims.Any(claim => claim.Gate == Gate.AtomicConditionalWrite
            && claim.State == EvidenceState.ObservedFail);
        if (failedGuard)
        {
            blockers.Add("Conditional-write failure stops automatic-write investment pending a new product decision and separately proven adapter.");
        }

        var verdict = ResolveVerdict(decision.RequestedVerdict, failedGuard, blockers.Count);
        return new(verdict, verdict == ProductVerdict.Go, gateResults, blockers.ToArray());
    }

    private static bool HasRequiredRecords(FeasibilityEvidence evidence) =>
        evidence.FeasibilityDecision is not null
        && evidence.RuleEvidenceManifest is not null
        && evidence.WorkloadProfile is not null
        && evidence.CategoryAdmissionMatrix is not null;

    private static void ValidateDecision(FeasibilityDecision decision, DateTimeOffset now, List<string> blockers)
    {
        if (!Guid.TryParse(decision.TenantId, out _) || !Guid.TryParse(decision.MailboxId, out _))
        {
            blockers.Add("A configured tenant and mailbox object ID are required.");
        }

        if (string.IsNullOrWhiteSpace(decision.ExperimentOwner) || decision.StopDate == default
            || decision.Endpoint != "/v1.0/me/events/{immutableId}" || decision.ApiVersion != "v1.0")
        {
            blockers.Add("The experiment requires an owner, stop date and exact supported endpoint.");
        }

        if (decision.RequestedVerdict == ProductVerdict.Go && now > decision.StopDate)
        {
            blockers.Add("The experiment time box has expired. Renew and review the decision explicitly.");
        }
    }

    private static void ValidateInputShape(FeasibilityDecision decision, CategoryAdmissionMatrix matrix,
        List<string> blockers)
    {
        var hasMalformedClaim = (decision.Claims ?? []).Any(claim => claim is null
            || !Enum.IsDefined(claim.Gate) || !Enum.IsDefined(claim.State));
        var hasMalformedCategory = (matrix.Categories ?? []).Any(category => category is null
            || string.IsNullOrWhiteSpace(category.Category));

        if (hasMalformedClaim || hasMalformedCategory)
        {
            blockers.Add("Malformed evidence claims or categories.");
        }
    }

    private static GateResult[] AssessGates(EvidenceClaim[] claims, Func<EvidenceClaim, bool> isValid)
    {
        const string passedReason = "Reviewed observation with verified artifact integrity.";
        const string failedReason = "Requires exactly one scoped, reviewed passing observation and verified artifacts.";

        return Enum.GetValues<Gate>().Select(gate =>
        {
            var matches = claims.Where(claim => claim.Gate == gate).ToArray();
            var passed = matches.Length == 1 && isValid(matches[0]);
            return new GateResult(gate, passed, passed ? passedReason : failedReason);
        }).ToArray();
    }

    private static bool IsValidClaim(EvidenceClaim claim, FeasibilityDecision decision, DateTimeOffset now,
        IReadOnlySet<string> verifiedArtifactHashes) =>
        claim is not null
        && Enum.IsDefined(claim.Gate)
        && claim.State == EvidenceState.ObservedPass
        && claim.TenantId == decision.TenantId
        && claim.MailboxId == decision.MailboxId
        && !string.IsNullOrWhiteSpace(claim.Owner)
        && claim.ObservedAt != default
        && claim.ObservedAt <= now
        && !string.IsNullOrWhiteSpace(claim.Method)
        && !string.IsNullOrWhiteSpace(claim.Finding)
        && IsSignedBy(claim.ReviewedBy, claim.ReviewedAt, now)
        && claim.ReviewedAt >= claim.ObservedAt
        && AreArtifactsVerified(claim.Artifacts, verifiedArtifactHashes);

    private static bool IsApprovedCategory(CategoryAdmission category, DateTimeOffset now,
        Func<EvidenceClaim, bool> isValidClaim) =>
        IsSignedBy(category.ApprovedBy, category.ApprovedAt, now)
        && category.Claims is { Length: > 0 }
        && category.Claims.All(claim => isValidClaim(claim) && IsAvailableBy(claim, category.ApprovedAt));

    private static void ValidateCategories(CategoryAdmission[] categories, DateTimeOffset now,
        Func<CategoryAdmission, bool> isApproved, List<string> blockers)
    {
        var admitted = categories.Where(category => category.Admitted).ToArray();
        if (admitted.Any(category => !isApproved(category)))
        {
            blockers.Add("Category admission must be signed at or after all relied-on observations and reviews.");
        }

        var hasDuplicate = categories.GroupBy(category => category.Category, StringComparer.Ordinal)
            .Any(group => group.Count() != 1);
        var hasRequiredAdmission = categories.Any(category => IsRequiredAdmission(category, isApproved));
        if (hasDuplicate || !hasRequiredAdmission)
        {
            blockers.Add("An independently reviewed internal-attendee single-instance admission is required.");
        }
    }

    private static bool IsRequiredAdmission(CategoryAdmission category, Func<CategoryAdmission, bool> isApproved) =>
        category.Category == RequiredCategory
        && category.Admitted
        && isApproved(category)
        && !string.IsNullOrWhiteSpace(category.NotificationConsequences)
        && !string.IsNullOrWhiteSpace(category.DeliveryUncertainty)
        && category.Claims.Any(claim => claim is not null && claim.Gate == Gate.Notifications)
        && category.Claims.Any(claim => claim is not null && claim.Gate == Gate.AtomicConditionalWrite);

    private static bool IsValidRuleManifest(RuleEvidenceManifest rules, DateTimeOffset now,
        IReadOnlySet<string> verifiedArtifactHashes) =>
        rules.SchemaVersion == 1
        && IsSignedBy(rules.ApprovedBy, rules.ApprovedAt, now)
        && !string.IsNullOrWhiteSpace(rules.BaselinePolicy)
        && !string.IsNullOrWhiteSpace(rules.PreservationPolicy)
        && !string.IsNullOrWhiteSpace(rules.ExplicitCorrectionPolicy)
        && rules.Jurisdictions is { Length: > 0 }
        && rules.Jurisdictions.All(jurisdiction => !string.IsNullOrWhiteSpace(jurisdiction))
        && AreArtifactsVerified(rules.AuthoritativeSources, verifiedArtifactHashes)
        && AreArtifactsVerified(rules.SourceRules, verifiedArtifactHashes)
        && AreArtifactsVerified(rules.TargetRules, verifiedArtifactHashes)
        && AreArtifactsVerified(rules.ProviderMappings, verifiedArtifactHashes);

    private static bool IsValidWorkload(WorkloadProfile workload, DateTimeOffset now,
        IReadOnlySet<string> verifiedArtifactHashes) =>
        workload.SchemaVersion == 1
        && HasRepresentativeWorkloadObservation(workload)
        && HasCredibleWorkloadMeasurements(workload)
        && WorkloadBudgetCoversMeasuredDemand(workload)
        && IsWorkloadBudgetWithinPilotLimits(workload)
        && AreArtifactsVerified(workload.Measurements, verifiedArtifactHashes)
        && IsSignedBy(workload.AcceptedBy, workload.AcceptedAt, now);

    private static bool HasRepresentativeWorkloadObservation(WorkloadProfile workload) =>
        workload.Complete
        && workload.Start != default
        && workload.Start < workload.End
        && workload.End - workload.Start <= TimeSpan.FromDays(366)
        && workload.Events > 0
        && workload.OrganizerMeetings > 0
        && workload.OrganizerMeetings <= workload.Events;

    private static bool HasCredibleWorkloadMeasurements(WorkloadProfile workload) =>
        workload.Pages > 0
        && workload.DetailReads >= workload.Events
        && workload.ThrottledResponses >= 0
        && double.IsFinite(workload.ElapsedSeconds)
        && workload.ElapsedSeconds > 0
        && workload.ReviewSecondsPerMeeting is > 0
        && double.IsFinite(workload.ReviewSecondsPerMeeting.Value)
        && !string.IsNullOrWhiteSpace(workload.MeasurementContext);

    private static bool WorkloadBudgetCoversMeasuredDemand(WorkloadProfile workload) =>
        workload.ProposedMaxEvents >= workload.Events
        && workload.ProposedMaxPages >= workload.Pages
        && workload.ProposedMaxDetailReads >= workload.DetailReads
        && workload.ProposedRunSeconds >= workload.ElapsedSeconds;

    private static bool IsWorkloadBudgetWithinPilotLimits(WorkloadProfile workload) =>
        workload.ProposedSelectedWrites > 0
        && workload.ProposedMaxEvents <= 10000
        && workload.ProposedMaxPages <= 1000
        && workload.ProposedMaxDetailReads <= 10000
        && workload.ProposedRunSeconds <= 3600
        && workload.ProposedSelectedWrites <= workload.ProposedMaxEvents;

    private static bool HasHumanGoDecision(FeasibilityDecision decision, EvidenceClaim[] claims,
        RuleEvidenceManifest rules, WorkloadProfile workload, CategoryAdmission[] categories, DateTimeOffset now) =>
        decision.RequestedVerdict == ProductVerdict.Go
        && IsSignedBy(decision.DecisionBy, decision.DecisionAt, now)
        && claims.All(claim => IsAvailableBy(claim, decision.DecisionAt))
        && IsApprovedBy(rules.ApprovedAt, decision.DecisionAt)
        && IsApprovedBy(workload.AcceptedAt, decision.DecisionAt)
        && categories.Where(category => category.Admitted)
            .All(category => IsApprovedBy(category.ApprovedAt, decision.DecisionAt));

    private static bool AreArtifactsVerified(ArtifactReference[]? artifacts,
        IReadOnlySet<string> verifiedArtifactHashes) =>
        artifacts is { Length: > 0 }
        && artifacts.All(artifact => artifact is not null
            && !string.IsNullOrWhiteSpace(artifact.Path)
            && artifact.Sha256 is { Length: 64 }
            && artifact.Sha256.All(Uri.IsHexDigit)
            && verifiedArtifactHashes.Contains(ArtifactKey(artifact)));

    private static bool IsSignedBy(string? person, DateTimeOffset? at, DateTimeOffset now) =>
        !string.IsNullOrWhiteSpace(person)
        && at is { } signedAt
        && signedAt != default
        && signedAt <= now;

    // Equality is valid at the recorded timestamp precision. An approval must never predate a dependency.
    private static bool IsAvailableBy(EvidenceClaim claim, DateTimeOffset? approval) =>
        approval is { } approvedAt
        && claim.ObservedAt <= approvedAt
        && claim.ReviewedAt is { } reviewedAt
        && reviewedAt <= approvedAt;

    private static bool IsApprovedBy(DateTimeOffset? approval, DateTimeOffset? decision) =>
        approval is { } approvedAt && decision is { } decisionAt && approvedAt <= decisionAt;

    private static ProductVerdict ResolveVerdict(ProductVerdict requested, bool failedGuard, int blockerCount) =>
        requested == ProductVerdict.NoGo || failedGuard
            ? ProductVerdict.NoGo
            : blockerCount == 0 ? ProductVerdict.Go : ProductVerdict.Blocked;
}
