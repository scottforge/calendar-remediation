namespace CalendarRemediation.Core.Feasibility;

public static class FeasibilityGate
{
    public static string ArtifactKey(ArtifactReference reference) => reference.Path + "|" + reference.Sha256;

    public static GateAssessment Assess(FeasibilityEvidence evidence, DateTimeOffset now,
        IReadOnlySet<string> verifiedArtifactHashes)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(verifiedArtifactHashes);
        if (evidence.FeasibilityDecision is null || evidence.RuleEvidenceManifest is null
            || evidence.WorkloadProfile is null || evidence.CategoryAdmissionMatrix is null)
        {
            return new(ProductVerdict.Blocked, false, [], ["All four evidence records are required."]);
        }
        var decision = evidence.FeasibilityDecision;
        var rules = evidence.RuleEvidenceManifest;
        var workload = evidence.WorkloadProfile;
        var matrix = evidence.CategoryAdmissionMatrix;
        var blockers = new List<string>();
        if (decision.SchemaVersion != 1 || rules.SchemaVersion != 1 || workload.SchemaVersion != 1 || matrix.SchemaVersion != 1)
        {
            blockers.Add("Unsupported evidence schema.");
        }

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

        bool ArtifactsValid(ArtifactReference[]? artifacts) => artifacts is { Length: > 0 }
            && artifacts.All(a => a is not null && !string.IsNullOrWhiteSpace(a.Path)
                && a.Sha256 is { Length: 64 } && a.Sha256.All(Uri.IsHexDigit)
                && verifiedArtifactHashes.Contains(ArtifactKey(a)));
        bool Signed(string? person, DateTimeOffset? at) => !string.IsNullOrWhiteSpace(person)
            && at is { } date && date != default && date <= now;
        bool ClaimValid(EvidenceClaim claim) => claim is not null && Enum.IsDefined(claim.Gate) && claim.State == EvidenceState.ObservedPass
            && claim.TenantId == decision.TenantId && claim.MailboxId == decision.MailboxId
            && !string.IsNullOrWhiteSpace(claim.Owner) && claim.ObservedAt != default && claim.ObservedAt <= now
            && !string.IsNullOrWhiteSpace(claim.Method) && !string.IsNullOrWhiteSpace(claim.Finding)
            && Signed(claim.ReviewedBy, claim.ReviewedAt) && claim.ReviewedAt >= claim.ObservedAt
            && ArtifactsValid(claim.Artifacts);
        // Equality is valid at the recorded timestamp precision. An approval must never predate a dependency.
        static bool AvailableBy(EvidenceClaim claim, DateTimeOffset? approval) => claim is not null
            && approval is { } at && claim.ObservedAt <= at && claim.ReviewedAt is { } reviewedAt && reviewedAt <= at;
        bool CategoryApproved(CategoryAdmission category) => Signed(category.ApprovedBy, category.ApprovedAt)
            && category.Claims is { Length: > 0 }
            && category.Claims.All(c => ClaimValid(c) && AvailableBy(c, category.ApprovedAt));

        if ((decision.Claims ?? []).Any(c => c is null || !Enum.IsDefined(c.Gate) || !Enum.IsDefined(c.State))
            || (matrix.Categories ?? []).Any(c => c is null || string.IsNullOrWhiteSpace(c.Category)))
        {
            blockers.Add("Malformed evidence claims or categories.");
        }
        var claims = (decision.Claims ?? []).Where(c => c is not null).ToArray();
        var results = Enum.GetValues<Gate>().Select(gate =>
        {
            var matching = claims.Where(c => c.Gate == gate).ToArray();
            var passed = matching.Length == 1 && ClaimValid(matching[0]);
            return new GateResult(gate, passed, passed ? "Reviewed observation with verified artifact integrity."
                : "Requires exactly one scoped, reviewed passing observation and verified artifacts.");
        }).ToArray();
        if (!Signed(rules.ApprovedBy, rules.ApprovedAt) || string.IsNullOrWhiteSpace(rules.BaselinePolicy)
            || string.IsNullOrWhiteSpace(rules.PreservationPolicy) || string.IsNullOrWhiteSpace(rules.ExplicitCorrectionPolicy)
            || rules.Jurisdictions is not { Length: > 0 } || rules.Jurisdictions.Any(string.IsNullOrWhiteSpace)
            || !ArtifactsValid(rules.AuthoritativeSources) || !ArtifactsValid(rules.SourceRules)
            || !ArtifactsValid(rules.TargetRules) || !ArtifactsValid(rules.ProviderMappings))
        {
            blockers.Add("Rule policy, authoritative sources, exact source/target artifacts and mappings need approval and integrity verification.");
        }

        if (!workload.Complete || workload.Start == default || workload.Start >= workload.End
            || workload.End - workload.Start > TimeSpan.FromDays(366)
            || workload.Events <= 0 || workload.OrganizerMeetings <= 0 || workload.OrganizerMeetings > workload.Events
            || workload.Pages <= 0 || workload.DetailReads < workload.Events || workload.ThrottledResponses < 0
            || !double.IsFinite(workload.ElapsedSeconds) || workload.ElapsedSeconds <= 0
            || workload.ReviewSecondsPerMeeting is not > 0 || !double.IsFinite(workload.ReviewSecondsPerMeeting.Value)
            || workload.ProposedMaxEvents < workload.Events || workload.ProposedMaxPages < workload.Pages
            || workload.ProposedMaxDetailReads < workload.DetailReads || workload.ProposedSelectedWrites <= 0
            || workload.ProposedMaxEvents > 10000 || workload.ProposedMaxPages > 1000
            || workload.ProposedMaxDetailReads > 10000 || workload.ProposedRunSeconds > 3600
            || workload.ProposedSelectedWrites > workload.ProposedMaxEvents
            || workload.ProposedRunSeconds < workload.ElapsedSeconds || string.IsNullOrWhiteSpace(workload.MeasurementContext)
            || !ArtifactsValid(workload.Measurements) || !Signed(workload.AcceptedBy, workload.AcceptedAt))
        {
            blockers.Add("Representative complete workload, review effort and accepted measured budgets are required.");
        }

        var categories = (matrix.Categories ?? []).Where(c => c is not null).ToArray();
        var admittedCategories = categories.Where(c => c.Admitted).ToArray();
        if (admittedCategories.Any(c => !CategoryApproved(c)))
        {
            blockers.Add("Category admission must be signed at or after all relied-on observations and reviews.");
        }
        if (categories.GroupBy(c => c.Category, StringComparer.Ordinal).Any(g => g.Count() != 1)
            || !categories.Any(c => c.Category == "OrganizerInternalAttendeesSingleInstance" && c.Admitted
                && CategoryApproved(c) && !string.IsNullOrWhiteSpace(c.NotificationConsequences)
                && !string.IsNullOrWhiteSpace(c.DeliveryUncertainty)
                && c.Claims.Any(x => x.Gate == Gate.Notifications)
                && c.Claims.Any(x => x.Gate == Gate.AtomicConditionalWrite)))
        {
            blockers.Add("An independently reviewed internal-attendee single-instance admission is required.");
        }

        blockers.AddRange(results.Where(r => !r.Passed).Select(r => $"{r.Gate}: {r.Reason}"));
        if (decision.RequestedVerdict != ProductVerdict.Go || !Signed(decision.DecisionBy, decision.DecisionAt)
            || !claims.All(c => AvailableBy(c, decision.DecisionAt))
            || rules.ApprovedAt is not { } policyAt || policyAt > decision.DecisionAt
            || workload.AcceptedAt is not { } workloadAt || workloadAt > decision.DecisionAt
            || admittedCategories.Any(c => c.ApprovedAt is not { } categoryAt || categoryAt > decision.DecisionAt))
        {
            blockers.Add("An explicit human go decision at or after all evidence reviews, category admissions, policy and workload approvals is required. Model privacy approval is included in the evidence reviews.");
        }

        var failedGuard = claims.Any(c => c.Gate == Gate.AtomicConditionalWrite && c.State == EvidenceState.ObservedFail);
        if (failedGuard)
        {
            blockers.Add("Conditional-write failure stops automatic-write investment pending a new product decision and separately proven adapter.");
        }

        var verdict = decision.RequestedVerdict == ProductVerdict.NoGo || failedGuard ? ProductVerdict.NoGo
            : blockers.Count == 0 ? ProductVerdict.Go : ProductVerdict.Blocked;
        return new(verdict, verdict == ProductVerdict.Go, results, blockers.ToArray());
    }
}
