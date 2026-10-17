using System.Collections.Immutable;
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
    DecisionDependencies CurrentDependencies, WriteAdmission? Admission);

// One call may dispatch each selected item at most once. Recovery never blindly replays intent.
public sealed class PlanExecutor(ICalendarReader reader, ICalendarWriter writer, IExecutionJournal journal,
    IExecutionControl control, TimeProvider? timeProvider = null)
{
    private static readonly ConditionalWeakTable<IExecutionJournal, SemaphoreSlim> JournalGates = new();
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

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
        var selected = request.Plan.Content.Items.Where(i => !request.Approval.SelectedItemIds.IsDefault
            && request.Approval.SelectedItemIds.Contains(i.Id)).ToArray();
        if (!PlanApproval.ValidateBinding(request.Plan, request.Approval).Valid)
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
        var recovery = ExecutionHistory.Classify(history);
        var batch = recovery.Batch;
        var results = ImmutableArray.CreateBuilder<ExecutionResult>();
        foreach (var item in selected)
        {
            if (recovery.Items.TryGetValue(item.Id, out var recovered))
            {
                results.Add(recovered.ToResult(item));
                continue;
            }
            if (batch == BatchContinuation.Hold || ControlsRequested(ct))
            {
                results.Add(Result(item, ExecutionOutcome.Paused, "execution-paused"));
                continue;
            }
            if (!PlanApproval.Validate(request.Plan, request.Approval, clock.GetUtcNow(), request.CurrentDependencies).Valid)
            {
                results.Add(Result(item, ExecutionOutcome.Blocked, "approval-invalid"));
                continue;
            }
            var decision = await ExecuteFreshItemAsync(request, item, clock.GetUtcNow, ct).ConfigureAwait(false);
            batch = decision.Batch;
            results.Add(decision.ToResult(item));
        }
        return results.ToImmutable();
    }

    private async Task<ItemExecutionDecision> ExecuteFreshItemAsync(ExecutionRequest request, PlanItem item,
        Func<DateTimeOffset> now, CancellationToken ct)
    {
        if (item.Disposition == AssessmentDisposition.NoOp)
        {
            return new(ExecutionOutcome.Skipped, "no-op");
        }
        if (item.Disposition != AssessmentDisposition.Change || item.Patch is null
            || !WriteEligibility.IsAdmitted(item.Source, request.Admission, item.Dependencies.AdmissionHash))
        {
            return new(ExecutionOutcome.Blocked, "item-not-admitted");
        }
        SourceSnapshot? current;
        try { current = await reader.ReadAsync(item.Source.Identity, item.Source.Id, ct).ConfigureAwait(false); }
        catch (Exception)
        {
            return new(ExecutionOutcome.Blocked, "source-read-unavailable", BatchContinuation.Hold);
        }
        // A nullable read does not distinguish absence from provider failure.
        if (current is null)
        {
            return new(ExecutionOutcome.Blocked, "source-read-unavailable", BatchContinuation.Hold);
        }
        if (!SourceSnapshotEvidence.Matches(current, item.Source))
        {
            return new(ExecutionOutcome.Conflict, "source-changed");
        }
        if (DispatchReadiness.BeforeIntent(request, item, current, now()) is { } blocked)
        {
            return blocked;
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
            // An append that throws may still have committed durable intent.
            return new(ExecutionOutcome.Blocked, "durable-intent-unavailable", BatchContinuation.Hold);
        }

        // Sample time again after durable I/O, directly before the only guarded write.
        if (DispatchReadiness.BeforeWrite(request, item, current, now(), ControlsRequested(ct)) is { } withheld)
        {
            return withheld;
        }
        return await DispatchAndRecordAsync(request, item, current, attempt, ct).ConfigureAwait(false);
    }

    private async Task<ItemExecutionDecision> DispatchAndRecordAsync(ExecutionRequest request, PlanItem item,
        SourceSnapshot current, JournalEntry attempt, CancellationToken ct)
    {
        MutationResult mutation;
        try
        {
            mutation = await writer.WriteAsync(new(current, attempt.Patch, request.Admission, attempt.AttemptId), ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Once the writer is entered even cancellation can mean the server committed.
            mutation = new(MutationOutcome.Unknown, null);
        }
        SourceSnapshot? observed = null;
        if (mutation.Outcome == MutationOutcome.Accepted)
        {
            try { observed = await reader.ReadAsync(current.Identity, current.Id, ct).ConfigureAwait(false); }
            catch (Exception)
            {
                // Missing independent readback leaves the mutation uncertain.
                observed = null;
            }
        }
        var state = OrganizerReadback.Classify(mutation.Outcome, current, attempt.Patch, observed,
            request.Admission, item.Dependencies.AdmissionHash);
        try
        {
            // Retain outcome even when user cancellation interrupted dispatch/readback.
            // The journal owns its own bounded durable I/O timeout.
            await journal.AppendAsync(attempt with { State = state, CorrelationId = mutation.CorrelationId }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            state = JournalState.Unknown;
        }
        return ItemExecutionDecision.FromFinalState(state);
    }

    private bool ControlsRequested(CancellationToken ct) =>
        ct.IsCancellationRequested || control.PauseRequested || control.StopRequested;

    private static ExecutionResult Result(PlanItem item, ExecutionOutcome outcome, string reason) =>
        new(item.Id, outcome, reason, outcome == ExecutionOutcome.Verified, false);
}
