using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Core.Planning;

internal sealed record AssessmentHold(AssessmentDisposition Disposition, string Reason);

internal static class AssessmentEvidence
{
    internal static AssessmentHold? CheckSource(SourceSnapshot source, AssessmentContext context)
    {
        if (source.Attendees.IsDefault || source.Unresolved.IsDefault)
        {
            return new(AssessmentDisposition.Unresolved, "incomplete-source-evidence");
        }
        if (source.IsOrganizer == false || source.IsCancelled == true || source.Start <= context.Discovery.Scope.ExecutionCutoff || source.Start <= context.Now)
        {
            return new(AssessmentDisposition.Ineligible, "not-future-organizer-meeting");
        }
        if (source.IsAllDay == true || source.Kind is EventKind.Occurrence or EventKind.Exception or EventKind.SeriesMaster
            || source.SeriesMasterId is not null || source.Attendees.Any(a => a.Kind is AttendeeKind.External or AttendeeKind.Resource))
        {
            return new(AssessmentDisposition.Unsupported, "outside-pilot-category");
        }
        if (!context.Discovery.Complete || !context.Discovery.Holds.IsEmpty || source.Identity != context.Discovery.Scope.Identity
            || source.Start is null || source.End is null || source.End <= source.Start || source.Start < context.Discovery.Scope.Start
            || source.Start >= context.Discovery.Scope.End || source.IsOrganizer is null || source.IsCancelled is null || source.IsAllDay is null
            || source.Kind == EventKind.Unknown || !source.Unresolved.IsEmpty || source.Attendees.Any(a => a.Kind == AttendeeKind.Unknown))
        {
            return new(AssessmentDisposition.Unresolved, "incomplete-source-evidence");
        }
        return CheckObservation(source, context.Discovery);
    }

    private static AssessmentHold? CheckObservation(SourceSnapshot source, DiscoveryResult discovery)
    {
        if (discovery.Events.IsDefault)
        {
            return new(AssessmentDisposition.Unresolved, "source-not-observed");
        }
        var observations = discovery.Events.Where(e => e.Identity == source.Identity && e.Id == source.Id).ToArray();
        return observations.Length == 0 || observations.Any(e => !SourceSnapshotEvidence.Matches(e, source))
            ? new(AssessmentDisposition.Unresolved, "source-not-observed-or-evidence-changed")
            : null;
    }

    internal static bool HasCurrentDecision(SourceSnapshot source, AssessmentContext context, ConfirmedIntent intent, RulePolicy policy) =>
        intent.Identity == source.Identity && intent.EventId == source.Id && intent.SourceVersion == source.Version
        && !string.IsNullOrWhiteSpace(intent.EvidenceId) && !string.IsNullOrWhiteSpace(intent.ConfirmedBy) && intent.ConfirmedAt <= context.Now
        && !string.IsNullOrWhiteSpace(intent.Zone)
        && policy.Approved && policy.AdoptionVerified && !(source.Start < policy.EffectiveFrom)
        && PlanBuilder.HasDependencies(policy.Dependencies)
        && WriteEligibility.IsAdmitted(source, context.Admission, policy.Dependencies.AdmissionHash);
}
