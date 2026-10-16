using System.Collections.Immutable;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Intent;

namespace CalendarRemediation.Core.Planning;

public sealed record ConfirmedIntent(string EvidenceId, CalendarIdentity Identity, string EventId, string SourceVersion,
    PreservationMode Mode, DateTime LocalStart, string Zone, string ConfirmedBy,
    DateTimeOffset ConfirmedAt, DateTimeOffset? SelectedOverlapInstant = null);
// Hashes identify the exact reviewed manifests for this plan, including its selected intent set.
public sealed record DecisionDependencies(string IntentHash, string PolicyHash, string SourceRulesHash,
    string TargetRulesHash, string MappingHash, string AdoptionHash, string AdmissionHash);
public sealed record RulePolicy(string Id, bool Approved, bool AdoptionVerified,
    DateTimeOffset EffectiveFrom, DecisionDependencies Dependencies);
public enum ResolutionKind { Unique, Gap, Overlap, MissingRules }
public sealed record LocalResolution(ResolutionKind Kind, ImmutableArray<DateTimeOffset> Candidates);
public interface IRuleResolver
{
    LocalResolution Resolve(DateTime localStart, string zone, RulePolicy policy);
}
// This is explicitly supplied reviewed evidence, never automatic category admission.
public sealed record WriteAdmission(CalendarIdentity Identity, string Category, bool Admitted,
    bool AtomicGuardProven, string GuardHeader, string EvidenceHash, string ReviewedBy);
public enum AssessmentDisposition { Change, NoOp, Unresolved, Unsupported, Ineligible }
public sealed record ExactPatch(DateTimeOffset Start, DateTimeOffset End, string Zone);
public sealed record Assessment(SourceSnapshot Source, AssessmentDisposition Disposition,
    ExactPatch? Patch, ImmutableArray<string> Reasons, DecisionDependencies? Dependencies);
public sealed record AssessmentContext(DiscoveryResult Discovery, ConfirmedIntent? Intent,
    RulePolicy? Policy, WriteAdmission? Admission, DateTimeOffset Now);

public sealed class MeetingAssessor(IRuleResolver rules)
{
    public Assessment Assess(SourceSnapshot source, AssessmentContext context)
    {
        Assessment Hold(AssessmentDisposition disposition, string reason) => new(source, disposition, null, [reason], context.Policy?.Dependencies);
        if (AssessmentEvidence.CheckSource(source, context) is { } sourceHold)
        {
            return Hold(sourceHold.Disposition, sourceHold.Reason);
        }
        var intent = context.Intent;
        var policy = context.Policy;
        if (intent is null || policy is null || !AssessmentEvidence.HasCurrentDecision(source, context, intent, policy))
        {
            return Hold(AssessmentDisposition.Unresolved, "missing-or-stale-decision-evidence");
        }
        if (intent.Mode == PreservationMode.PreserveAbsoluteInstant)
        {
            return Hold(AssessmentDisposition.NoOp, "absolute-instant-preserved");
        }
        if (intent.Mode != PreservationMode.PreserveLocalStart || intent.LocalStart.Kind != DateTimeKind.Unspecified
            || !context.Discovery.SupportedZones.Contains(intent.Zone, StringComparer.Ordinal))
        {
            return Hold(AssessmentDisposition.Unresolved, "unsupported-intent");
        }
        var resolution = rules.Resolve(intent.LocalStart, intent.Zone, policy);
        if (AssessmentTarget.SelectStart(resolution, intent.SelectedOverlapInstant) is not { } start)
        {
            return Hold(AssessmentDisposition.Unresolved, "nonunique-or-missing-rules");
        }
        var patch = AssessmentTarget.ProjectDuration(start, source.End!.Value - source.Start!.Value, intent.Zone);
        if (patch is null)
        {
            return Hold(AssessmentDisposition.Unresolved, "invalid-target-duration");
        }
        if (start < policy.EffectiveFrom)
        {
            return Hold(AssessmentDisposition.Unresolved, "target-outside-policy");
        }
        if (start <= context.Now || start <= context.Discovery.Scope.ExecutionCutoff)
        {
            return Hold(AssessmentDisposition.Ineligible, "target-not-future");
        }
        return start == source.Start && patch.End == source.End
            ? Hold(AssessmentDisposition.NoOp, "already-correct")
            : new(source, AssessmentDisposition.Change, patch, [], policy.Dependencies);
    }
}
