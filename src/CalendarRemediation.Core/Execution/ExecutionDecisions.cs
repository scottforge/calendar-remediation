using System.Collections.Immutable;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Execution;

internal enum BatchContinuation { Continue, Hold }

internal sealed record ItemExecutionDecision(ExecutionOutcome Outcome, string Reason,
    BatchContinuation Batch = BatchContinuation.Continue)
{
    public ExecutionResult ToResult(PlanItem item) =>
        new(item.Id, Outcome, Reason, Outcome == ExecutionOutcome.Verified, false);

    public static ItemExecutionDecision FromFinalState(JournalState state)
    {
        var outcome = state switch
        {
            JournalState.Verified => ExecutionOutcome.Verified,
            JournalState.Conflict => ExecutionOutcome.Conflict,
            JournalState.Rejected => ExecutionOutcome.Rejected,
            _ => ExecutionOutcome.Unknown
        };
        return new(outcome, outcome == ExecutionOutcome.Verified
            ? "organizer-fields-readback-matches" : "mutation-held-or-rejected",
            outcome == ExecutionOutcome.Unknown ? BatchContinuation.Hold : BatchContinuation.Continue);
    }
}

internal sealed record ExecutionHistory(ImmutableDictionary<string, ItemExecutionDecision> Items,
    BatchContinuation Batch)
{
    public static ExecutionHistory Classify(ImmutableArray<JournalEntry> entries)
    {
        var items = ImmutableDictionary.CreateBuilder<string, ItemExecutionDecision>(StringComparer.Ordinal);
        var batch = BatchContinuation.Continue;
        foreach (var item in entries.GroupBy(e => e.ItemId))
        {
            // A later terminal attempt cannot clear an older incomplete attempt.
            var uncertain = item.GroupBy(e => e.AttemptId)
                .Any(attempt => attempt.Last().State is JournalState.Intent or JournalState.Dispatched or JournalState.Unknown);
            var decision = ItemExecutionDecision.FromFinalState(uncertain ? JournalState.Unknown : item.Last().State);
            items.Add(item.Key, decision with
            {
                Reason = decision.Outcome == ExecutionOutcome.Verified
                    ? "persisted-organizer-fields-verified" : "persisted-attempt-not-replayed"
            });
            if (decision.Batch == BatchContinuation.Hold)
            {
                // Uncertainty holds every fresh dispatch, irrespective of selection order.
                batch = BatchContinuation.Hold;
            }
        }
        return new(items.ToImmutable(), batch);
    }
}

internal static class DispatchReadiness
{
    // Null means ready. Every refusal carries its result and explicit batch continuation.
    public static ItemExecutionDecision? BeforeIntent(ExecutionRequest request, PlanItem item,
        SourceSnapshot current, DateTimeOffset now)
    {
        return ApprovalValid(request, now) && TimingAndAdmissionValid(request, item, current, now)
            ? null : new(ExecutionOutcome.Blocked, "dispatch-revalidation-failed");
    }

    public static ItemExecutionDecision? BeforeWrite(ExecutionRequest request, PlanItem item,
        SourceSnapshot current, DateTimeOffset now, bool controlsRequested)
    {
        if (controlsRequested)
        {
            return new(ExecutionOutcome.Paused, "dispatch-withheld", BatchContinuation.Hold);
        }
        // Validate time explicitly before approval, whose item-selectability check also
        // rejects past starts. Time failures retain the dispatch-revalidation reason.
        if (!TimingAndAdmissionValid(request, item, current, now))
        {
            return new(ExecutionOutcome.Blocked, "dispatch-revalidation-failed", BatchContinuation.Hold);
        }
        return ApprovalValid(request, now)
            ? null : new(ExecutionOutcome.Paused, "dispatch-withheld", BatchContinuation.Hold);
    }

    private static bool ApprovalValid(ExecutionRequest request, DateTimeOffset now) =>
        PlanApproval.Validate(request.Plan, request.Approval, now, request.CurrentDependencies).Valid;

    private static bool TimingAndAdmissionValid(ExecutionRequest request, PlanItem item,
        SourceSnapshot current, DateTimeOffset now) =>
        item.Patch is { } patch && current.Start > now
        && current.Start > request.Plan.Content.Scope.ExecutionCutoff && patch.Start > now
        && patch.Start > request.Plan.Content.Scope.ExecutionCutoff
        && WriteEligibility.IsAdmitted(current, request.Admission, item.Dependencies.AdmissionHash);
}

internal static class OrganizerReadback
{
    public static JournalState Classify(MutationOutcome mutation, SourceSnapshot source, ExactPatch patch,
        SourceSnapshot? observed, WriteAdmission? admission, string admissionHash) => mutation switch
        {
            MutationOutcome.Conflict => JournalState.Conflict,
            MutationOutcome.Rejected => JournalState.Rejected,
            MutationOutcome.Accepted when observed is not null
                && observed.Identity == source.Identity && observed.Id == source.Id
                && observed.Start == patch.Start && observed.End == patch.End
                && WriteEligibility.IsAdmitted(observed, admission, admissionHash)
                && observed.Organizer == source.Organizer && observed.Attendees.SequenceEqual(source.Attendees) => JournalState.Verified,
            _ => JournalState.Unknown
        };
}
