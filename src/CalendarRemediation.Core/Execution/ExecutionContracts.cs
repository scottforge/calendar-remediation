using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Execution;

public enum MutationOutcome { Accepted, Conflict, Rejected, Unknown }
public sealed record MutationResult(MutationOutcome Outcome, string? CorrelationId);
public sealed record GuardedWrite(SourceSnapshot Source, ExactPatch Patch, WriteAdmission? Admission,
    Guid AttemptId);
public interface ICalendarWriter
{
    Task<MutationResult> WriteAsync(GuardedWrite write, CancellationToken cancellationToken = default);
}
/// <summary>
/// Execution dispositions. Verified means an accepted mutation and independently matching
/// organizer fields, not verification of provider scheduling semantics or invitation delivery.
/// </summary>
public enum ExecutionOutcome { Proposed, Skipped, Blocked, Conflict, Rejected, Verified, Unknown, Paused }
/// <summary>Execution evidence limited to organizer-field readback and separately reported delivery.</summary>
/// <param name="ItemId">The exact approved plan item.</param>
/// <param name="Outcome">The execution disposition, with Verified limited to organizer fields.</param>
/// <param name="Reason">An operational reason code, not provider scheduling proof.</param>
/// <param name="OrganizerVerified">Whether accepted organizer fields independently matched the patch. This does not prove local scheduling semantics.</param>
/// <param name="DeliveryVerified">Whether delivery was independently verified. The current executor always returns false.</param>
public sealed record ExecutionResult(string ItemId, ExecutionOutcome Outcome, string Reason,
    bool OrganizerVerified, bool DeliveryVerified);
/// <summary>Durable attempt states. Verified records organizer-field verification only, not scheduling or delivery proof.</summary>
public enum JournalState { Intent, Dispatched, Verified, Conflict, Rejected, Unknown }
public sealed record JournalEntry(Guid AttemptId, string PlanDigest, string ItemId, JournalState State,
    string Guard, ExactPatch Patch, string? CorrelationId);
public interface IExecutionJournal
{
    // Idempotently persist the exact immutable plan/approval. Production adapters must
    // reject conflicting evidence, retain unresolved event holds across new plans, and
    // enforce a single execution owner across processes.
    Task PrepareAsync(RemediationPlan plan, Approval approval, CancellationToken cancellationToken = default);
    // Complete only after durable storage. Entries are append-only and ordered per attempt.
    // A throwing append may have committed, so recovery must read before doing anything else.
    Task AppendAsync(JournalEntry entry, CancellationToken cancellationToken = default);
    // Return entries in durable append order, including every incomplete attempt.
    Task<ImmutableArray<JournalEntry>> ReadAsync(string planDigest, CancellationToken cancellationToken = default);
}
public interface IExecutionControl
{
    bool PauseRequested { get; }
    bool StopRequested { get; }
}
public sealed record ExecutionRequest(RemediationPlan Plan, Approval Approval,
    DecisionDependencies CurrentDependencies, WriteAdmission? Admission, DateTimeOffset Now);

// One call may dispatch each selected item at most once. Recovery never blindly replays intent.
public sealed class PlanExecutor(ICalendarReader reader, ICalendarWriter writer, IExecutionJournal journal,
    IExecutionControl control)
{
    private static readonly ConditionalWeakTable<IExecutionJournal, SemaphoreSlim> JournalGates = new();

    public async Task<ImmutableArray<ExecutionResult>> ExecuteAsync(ExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        // Serialize local executors sharing a journal. Durable adapters must additionally enforce
        // a single execution owner across processes before accepting Prepare/Append operations.
        var gate = JournalGates.GetValue(journal, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ExecuteLockedAsync(request, cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<ImmutableArray<ExecutionResult>> ExecuteLockedAsync(ExecutionRequest request, CancellationToken ct)
    {
        var results = ImmutableArray.CreateBuilder<ExecutionResult>();
        var elapsed = Stopwatch.StartNew();
        DateTimeOffset Now() => request.Now.Add(elapsed.Elapsed);
        var selected = request.Plan.Content.Items.Where(i => request.Approval.SelectedItemIds.Contains(i.Id)).ToArray();
        ExecutionResult Result(PlanItem item, ExecutionOutcome outcome, string reason) => new(item.Id, outcome, reason, outcome == ExecutionOutcome.Verified, false);
        if (!PlanApproval.Validate(request.Plan, request.Approval, Now(), request.CurrentDependencies).Valid)
        {
            return [.. selected.Select(i => Result(i, ExecutionOutcome.Blocked, "approval-invalid"))];
        }
        ImmutableArray<JournalEntry> history;
        try
        {
            history = await journal.ReadAsync(request.Plan.Digest, ct).ConfigureAwait(false);
            await journal.PrepareAsync(request.Plan, request.Approval, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [.. selected.Select(i => Result(i, ExecutionOutcome.Blocked, "journal-unavailable"))];
        }
        // An uncertain persisted attempt has no proven item-specific failure scope.
        // Hold fresh dispatches even when that attempt sorts after another selected item.
        var halted = history.GroupBy(e => (e.ItemId, e.AttemptId))
            .Any(g => g.Last().State is JournalState.Intent or JournalState.Dispatched or JournalState.Unknown);
        foreach (var item in selected)
        {
            var previous = history.Where(e => e.ItemId == item.Id).ToArray();
            if (previous.Length != 0)
            {
                // Any incomplete or uncertain attempt holds the item, including an Intent that
                // might have been dispatched immediately before a crash. Never infer attribution.
                var uncertain = previous.GroupBy(e => e.AttemptId).Any(g => g.Last().State is JournalState.Intent or JournalState.Dispatched or JournalState.Unknown);
                var outcome = uncertain ? ExecutionOutcome.Unknown : previous[^1].State switch
                {
                    JournalState.Verified => ExecutionOutcome.Verified,
                    JournalState.Conflict => ExecutionOutcome.Conflict,
                    JournalState.Rejected => ExecutionOutcome.Rejected,
                    _ => ExecutionOutcome.Unknown
                };
                halted |= outcome == ExecutionOutcome.Unknown;
                results.Add(Result(item, outcome, outcome == ExecutionOutcome.Verified
                    ? "persisted-organizer-fields-verified" : "persisted-attempt-not-replayed"));
                continue;
            }
            if (halted || ct.IsCancellationRequested || control.PauseRequested || control.StopRequested)
            {
                results.Add(Result(item, ExecutionOutcome.Paused, "execution-paused"));
                continue;
            }
            if (item.Disposition == AssessmentDisposition.NoOp)
            {
                results.Add(Result(item, ExecutionOutcome.Skipped, "no-op"));
                continue;
            }
            if (item.Disposition != AssessmentDisposition.Change || item.Patch is null
                || !WriteEligibility.IsAdmitted(item.Source, request.Admission, item.Dependencies.AdmissionHash))
            {
                results.Add(Result(item, ExecutionOutcome.Blocked, "item-not-admitted"));
                continue;
            }
            SourceSnapshot? current;
            try { current = await reader.ReadAsync(item.Source.Identity, item.Source.Id, ct).ConfigureAwait(false); }
            catch (Exception)
            {
                halted = true;
                results.Add(Result(item, ExecutionOutcome.Blocked, "source-read-unavailable"));
                continue;
            }
            if (current is null)
            {
                // The reader's nullable result does not distinguish absence from provider failure.
                halted = true;
                results.Add(Result(item, ExecutionOutcome.Blocked, "source-read-unavailable"));
                continue;
            }
            if (!SourceSnapshotEvidence.Matches(current, item.Source))
            {
                results.Add(Result(item, ExecutionOutcome.Conflict, "source-changed"));
                continue;
            }
            if (!PlanApproval.Validate(request.Plan, request.Approval, Now(), request.CurrentDependencies).Valid
                || current.Start <= Now() || current.Start <= request.Plan.Content.Scope.ExecutionCutoff || item.Patch.Start <= Now()
                || !WriteEligibility.IsAdmitted(current, request.Admission, item.Dependencies.AdmissionHash))
            {
                results.Add(Result(item, ExecutionOutcome.Blocked, "dispatch-revalidation-failed"));
                continue;
            }
            var attempt = new JournalEntry(Guid.NewGuid(), request.Plan.Digest, item.Id, JournalState.Intent,
                current.Guard!, item.Patch, null);
            try
            {
                await journal.AppendAsync(attempt, ct).ConfigureAwait(false);
                await journal.AppendAsync(attempt with { State = JournalState.Dispatched }, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                halted = true;
                results.Add(Result(item, ExecutionOutcome.Blocked, "durable-intent-unavailable"));
                continue;
            }
            // Recheck after asynchronous journal work, directly before the only write call.
            if (ct.IsCancellationRequested || control.PauseRequested || control.StopRequested
                || !PlanApproval.Validate(request.Plan, request.Approval, Now(), request.CurrentDependencies).Valid)
            {
                halted = true;
                results.Add(Result(item, ExecutionOutcome.Paused, "dispatch-withheld"));
                continue;
            }
            MutationResult mutation;
            try
            {
                mutation = await writer.WriteAsync(new(current, item.Patch, request.Admission, attempt.AttemptId), ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Once the writer is entered even cancellation can mean the server committed.
                mutation = new(MutationOutcome.Unknown, null);
            }
            var state = mutation.Outcome switch
            {
                MutationOutcome.Conflict => JournalState.Conflict,
                MutationOutcome.Rejected => JournalState.Rejected,
                _ => JournalState.Unknown
            };
            if (mutation.Outcome == MutationOutcome.Accepted)
            {
                try
                {
                    var observed = await reader.ReadAsync(current.Identity, current.Id, ct).ConfigureAwait(false);
                    if (observed is not null && observed.Identity == current.Identity && observed.Id == current.Id
                        && observed.Start == item.Patch.Start && observed.End == item.Patch.End
                        && observed.Organizer == current.Organizer && observed.Attendees.SequenceEqual(current.Attendees)
                        && WriteEligibility.IsAdmitted(observed, request.Admission, item.Dependencies.AdmissionHash))
                    {
                        state = JournalState.Verified;
                    }
                }
                catch (Exception) { state = JournalState.Unknown; }
            }
            try
            {
                // Retain outcome even when user cancellation interrupted dispatch/readback.
                // The journal owns its own bounded durable I/O timeout.
                await journal.AppendAsync(attempt with { State = state, CorrelationId = mutation.CorrelationId }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                state = JournalState.Unknown;
                halted = true;
            }
            var final = state switch
            {
                JournalState.Verified => ExecutionOutcome.Verified,
                JournalState.Conflict => ExecutionOutcome.Conflict,
                JournalState.Rejected => ExecutionOutcome.Rejected,
                _ => ExecutionOutcome.Unknown
            };
            halted |= final == ExecutionOutcome.Unknown;
            results.Add(Result(item, final, final == ExecutionOutcome.Verified ? "organizer-fields-readback-matches" : "mutation-held-or-rejected"));
        }
        return results.ToImmutable();
    }
}
