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
        if (source.Attendees.IsDefault || source.Unresolved.IsDefault)
        {
            return Hold(AssessmentDisposition.Unresolved, "incomplete-source-evidence");
        }
        if (source.IsOrganizer == false || source.IsCancelled == true || source.Start <= context.Discovery.Scope.ExecutionCutoff || source.Start <= context.Now)
        {
            return Hold(AssessmentDisposition.Ineligible, "not-future-organizer-meeting");
        }
        if (source.IsAllDay == true || source.Kind is EventKind.Occurrence or EventKind.Exception or EventKind.SeriesMaster
            || source.SeriesMasterId is not null || source.Attendees.Any(a => a.Kind is AttendeeKind.External or AttendeeKind.Resource))
        {
            return Hold(AssessmentDisposition.Unsupported, "outside-pilot-category");
        }
        if (!context.Discovery.Complete || !context.Discovery.Holds.IsEmpty || source.Identity != context.Discovery.Scope.Identity
            || source.Start is null || source.End is null || source.End <= source.Start || source.Start < context.Discovery.Scope.Start
            || source.Start >= context.Discovery.Scope.End || source.IsOrganizer is null || source.IsCancelled is null || source.IsAllDay is null
            || source.Kind == EventKind.Unknown || !source.Unresolved.IsEmpty || source.Attendees.Any(a => a.Kind == AttendeeKind.Unknown))
        {
            return Hold(AssessmentDisposition.Unresolved, "incomplete-source-evidence");
        }
        if (context.Discovery.Events.IsDefault)
        {
            return Hold(AssessmentDisposition.Unresolved, "source-not-observed");
        }
        var observations = context.Discovery.Events.Where(e => e.Identity == source.Identity && e.Id == source.Id).ToArray();
        if (observations.Length == 0 || observations.Any(e => !SourceSnapshotEvidence.Matches(e, source)))
        {
            return Hold(AssessmentDisposition.Unresolved, "source-not-observed-or-evidence-changed");
        }
        var intent = context.Intent;
        var policy = context.Policy;
        if (intent is null || intent.Identity != source.Identity || intent.EventId != source.Id || intent.SourceVersion != source.Version
            || string.IsNullOrWhiteSpace(intent.EvidenceId) || string.IsNullOrWhiteSpace(intent.ConfirmedBy) || intent.ConfirmedAt > context.Now
            || string.IsNullOrWhiteSpace(intent.Zone)
            || policy is null || !policy.Approved || !policy.AdoptionVerified || source.Start < policy.EffectiveFrom
            || !PlanBuilder.HasDependencies(policy.Dependencies)
            || !WriteEligibility.IsAdmitted(source, context.Admission, policy.Dependencies.AdmissionHash))
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
        DateTimeOffset start;
        if (resolution.Kind == ResolutionKind.Unique && resolution.Candidates.Length == 1
            && (intent.SelectedOverlapInstant is null || intent.SelectedOverlapInstant == resolution.Candidates[0]))
        {
            start = resolution.Candidates[0];
        }
        else if (resolution.Kind == ResolutionKind.Overlap && intent.SelectedOverlapInstant is { } selected
            && resolution.Candidates.Length > 1 && resolution.Candidates.Contains(selected))
        {
            start = selected;
        }
        else
        {
            return Hold(AssessmentDisposition.Unresolved, "nonunique-or-missing-rules");
        }
        DateTimeOffset end;
        try { end = start.Add(source.End.Value - source.Start.Value); }
        catch (ArgumentOutOfRangeException) { return Hold(AssessmentDisposition.Unresolved, "invalid-target-duration"); }
        if (start < policy.EffectiveFrom)
        {
            return Hold(AssessmentDisposition.Unresolved, "target-outside-policy");
        }
        if (start <= context.Now || start <= context.Discovery.Scope.ExecutionCutoff)
        {
            return Hold(AssessmentDisposition.Ineligible, "target-not-future");
        }
        return start == source.Start && end == source.End
            ? Hold(AssessmentDisposition.NoOp, "already-correct")
            : new(source, AssessmentDisposition.Change, new(start.ToUniversalTime(), end.ToUniversalTime(), intent.Zone), [], policy.Dependencies);
    }
}
