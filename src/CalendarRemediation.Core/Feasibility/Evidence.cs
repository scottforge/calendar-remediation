namespace CalendarRemediation.Core.Feasibility;

public enum Gate
{
    TenantIdentity, ProtectedCache, DefaultCalendar, SupportedZones, IdentityStability,
    WindowsLaunch, AtomicConditionalWrite, Notifications, Attribution, ModelPrivacy,
    RulePolicy, Workload, UsefulMeetingCategory
}

public enum EvidenceState { Planned, Inconclusive, ObservedPass, ObservedFail }
public enum ProductVerdict { Blocked, Go, NoGo }

public sealed record ArtifactReference(string Path, string Sha256);

// Claims require both observed evidence and an identified human reviewer.
// A file hash establishes integrity, not the truth of the claim.
public sealed record EvidenceClaim(
    Gate Gate, EvidenceState State, string TenantId, string MailboxId,
    string Owner, DateTimeOffset ObservedAt, string Method, string Finding,
    ArtifactReference[] Artifacts, string? ReviewedBy, DateTimeOffset? ReviewedAt);

public sealed record FeasibilityDecision(
    int SchemaVersion, string TenantId, string MailboxId, string ExperimentOwner,
    DateTimeOffset StopDate, string Endpoint, string ApiVersion,
    ProductVerdict RequestedVerdict, string? DecisionBy, DateTimeOffset? DecisionAt,
    EvidenceClaim[] Claims, string AlternativeInvestigation);

public sealed record RuleEvidenceManifest(
    int SchemaVersion, string BaselinePolicy, string PreservationPolicy,
    string ExplicitCorrectionPolicy, string[] Jurisdictions,
    ArtifactReference[] AuthoritativeSources, ArtifactReference[] SourceRules,
    ArtifactReference[] TargetRules, ArtifactReference[] ProviderMappings,
    string? ApprovedBy, DateTimeOffset? ApprovedAt);

public sealed record WorkloadProfile(
    int SchemaVersion, DateTimeOffset Start, DateTimeOffset End, bool Complete,
    int Events, int OrganizerMeetings, int Pages, int DetailReads,
    int ThrottledResponses, double ElapsedSeconds, double? ReviewSecondsPerMeeting,
    int ProposedMaxEvents, int ProposedMaxPages, int ProposedMaxDetailReads,
    int ProposedSelectedWrites, int ProposedRunSeconds, string MeasurementContext,
    ArtifactReference[] Measurements, string? AcceptedBy, DateTimeOffset? AcceptedAt);

public sealed record CategoryAdmission(
    string Category, bool Admitted, string? ApprovedBy, DateTimeOffset? ApprovedAt,
    string NotificationConsequences, string DeliveryUncertainty, EvidenceClaim[] Claims);

public sealed record CategoryAdmissionMatrix(int SchemaVersion, CategoryAdmission[] Categories);

public sealed record FeasibilityEvidence(
    FeasibilityDecision FeasibilityDecision, RuleEvidenceManifest RuleEvidenceManifest,
    WorkloadProfile WorkloadProfile, CategoryAdmissionMatrix CategoryAdmissionMatrix);

public sealed record GateResult(Gate Gate, bool Passed, string Reason);
public sealed record GateAssessment(ProductVerdict Verdict, bool ExitAllowed, GateResult[] Gates, string[] Blockers);
