using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Core.Planning;

public sealed record PlanItem(string Id, SourceSnapshot Source, ConfirmedIntent Intent, AssessmentDisposition Disposition,
    ExactPatch? Patch, DecisionDependencies Dependencies, string NotificationUncertainty);
public sealed record PlanDraft(string Id, DiscoveryScope Scope, bool DiscoveryComplete,
    ImmutableArray<PlanItem> Items, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record RemediationPlan(PlanDraft Content, string CanonicalJson, string Digest);
public sealed record Approval(string Operator, string PlanId, string Digest,
    ImmutableArray<string> SelectedItemIds, DateTimeOffset ApprovedAt, DateTimeOffset ExpiresAt);
public sealed record ApprovalDecision(bool Valid, ImmutableArray<string> Reasons);

public static class PlanBuilder
{
    public static RemediationPlan Build(PlanDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.DiscoveryComplete) { throw new InvalidOperationException("Complete discovery is required."); }
        if (string.IsNullOrWhiteSpace(draft.Id) || draft.Items.IsDefaultOrEmpty || draft.ExpiresAt <= draft.CreatedAt
            || draft.Scope.Start.Offset != TimeSpan.Zero || draft.Scope.End.Offset != TimeSpan.Zero || draft.Scope.End <= draft.Scope.Start
            || draft.Items.Any(i => string.IsNullOrWhiteSpace(i.Id)) || draft.Items.Select(i => i.Id).Distinct(StringComparer.Ordinal).Count() != draft.Items.Length
            || draft.Items.Select(i => (i.Source.Identity, i.Source.Id)).Distinct().Count() != draft.Items.Length)
        {
            throw new ArgumentException("Invalid plan scope, lifetime, or item identities.", nameof(draft));
        }
        foreach (var item in draft.Items)
        {
            if (item.Source.Identity != draft.Scope.Identity || item.Intent.Identity != item.Source.Identity
                || item.Intent.EventId != item.Source.Id || item.Intent.SourceVersion != item.Source.Version
                || string.IsNullOrWhiteSpace(item.Intent.EvidenceId) || string.IsNullOrWhiteSpace(item.Intent.ConfirmedBy)
                || item.Intent.ConfirmedAt > draft.CreatedAt
                || (item.Disposition == AssessmentDisposition.Change && (item.Patch is null || item.Patch.End <= item.Patch.Start))
                || item.Source.Unresolved.IsDefault || item.Source.Attendees.IsDefault
                || (item.Disposition != AssessmentDisposition.Change && item.Patch is not null))
            {
                throw new ArgumentException("Item is not bound to its confirmed source or exact patch.", nameof(draft));
            }
        }
        var content = draft with { Items = [.. draft.Items.OrderBy(i => i.Id, StringComparer.Ordinal)] };
        // Explicit format version and deterministic property order form the persisted digest contract.
        // Serialize the complete typed content, including all decision evidence and source unknowns.
        var canonical = JsonSerializer.Serialize(new { schemaVersion = 1, content });
        return new(content, canonical, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))));
    }

    internal static bool IsIntact(RemediationPlan plan)
    {
        try
        {
            var rebuilt = Build(plan.Content);
            return rebuilt.CanonicalJson == plan.CanonicalJson && rebuilt.Digest == plan.Digest;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool HasDependencies(DecisionDependencies dependencies) =>
        !string.IsNullOrWhiteSpace(dependencies.IntentHash) && !string.IsNullOrWhiteSpace(dependencies.PolicyHash)
        && !string.IsNullOrWhiteSpace(dependencies.SourceRulesHash) && !string.IsNullOrWhiteSpace(dependencies.TargetRulesHash)
        && !string.IsNullOrWhiteSpace(dependencies.MappingHash) && !string.IsNullOrWhiteSpace(dependencies.AdoptionHash)
        && !string.IsNullOrWhiteSpace(dependencies.AdmissionHash);

    internal static bool CanSelect(PlanItem item, DateTimeOffset now, DiscoveryScope scope) =>
        HasEligibleSource(item.Source)
        && HasDecisionEvidence(item, now)
        && IsSourceInExecutionScope(item.Source, now, scope)
        && HasSelectableDisposition(item, now, scope);

    private static bool HasEligibleSource(SourceSnapshot source) =>
        source.Start is not null && source.End is not null && source.End > source.Start
        && source.IsOrganizer == true && source.IsCancelled == false && source.IsAllDay == false
        && source.Kind == EventKind.SingleInstance && source.SeriesMasterId is null
        && !string.IsNullOrWhiteSpace(source.Version) && !string.IsNullOrWhiteSpace(source.Guard)
        && source.Guard != "*" && !string.IsNullOrWhiteSpace(source.Organizer)
        && !source.Attendees.IsDefault
        && source.Attendees.All(a => a.Kind == AttendeeKind.Internal && !string.IsNullOrWhiteSpace(a.Address))
        && !source.Unresolved.IsDefault && source.Unresolved.IsEmpty;

    private static bool HasDecisionEvidence(PlanItem item, DateTimeOffset now) =>
        HasDependencies(item.Dependencies) && item.Intent.ConfirmedAt <= now
        && item.Intent.LocalStart.Kind == DateTimeKind.Unspecified && !string.IsNullOrWhiteSpace(item.Intent.Zone);

    private static bool IsSourceInExecutionScope(SourceSnapshot source, DateTimeOffset now, DiscoveryScope scope) =>
        source.Start > now && source.Start > scope.ExecutionCutoff
        && source.Start >= scope.Start && source.Start < scope.End;

    private static bool HasSelectableDisposition(PlanItem item, DateTimeOffset now, DiscoveryScope scope) =>
        item.Disposition switch
        {
            AssessmentDisposition.NoOp => item.Patch is null,
            AssessmentDisposition.Change => item.Patch is { } patch && patch.Start > now && patch.Start > scope.ExecutionCutoff
                && patch.End - patch.Start == item.Source.End - item.Source.Start && !string.IsNullOrWhiteSpace(patch.Zone)
                && item.Intent.Mode == Intent.PreservationMode.PreserveLocalStart && patch.Zone == item.Intent.Zone,
            _ => false
        };
}

public static class PlanApproval
{
    public static Approval Approve(RemediationPlan plan, string operatorId, ImmutableArray<string> selectedItemIds,
        DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (!PlanBuilder.IsIntact(plan) || string.IsNullOrWhiteSpace(operatorId) || selectedItemIds.IsDefaultOrEmpty
            || selectedItemIds.Distinct(StringComparer.Ordinal).Count() != selectedItemIds.Length
            || selectedItemIds.Any(id => !plan.Content.Items.Any(i => i.Id == id))
            || plan.Content.Items.Where(i => selectedItemIds.Contains(i.Id)).Any(i => !PlanBuilder.CanSelect(i, now, plan.Content.Scope))
            || now < plan.Content.CreatedAt || expiresAt <= now || expiresAt > plan.Content.ExpiresAt)
        {
            throw new ArgumentException("Approval must bind an intact live plan, an operator, and an exact selection.");
        }
        return new(operatorId, plan.Content.Id, plan.Digest, [.. selectedItemIds.Order(StringComparer.Ordinal)], now, expiresAt);
    }

    public static ApprovalDecision Validate(RemediationPlan plan, Approval approval, DateTimeOffset now,
        DecisionDependencies currentDependencies)
    {
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (!PlanBuilder.IsIntact(plan) || plan.Digest != approval.Digest || plan.Content.Id != approval.PlanId)
        {
            reasons.Add("plan-integrity");
        }
        if (string.IsNullOrWhiteSpace(approval.Operator) || approval.SelectedItemIds.IsDefaultOrEmpty
            || approval.SelectedItemIds.Distinct(StringComparer.Ordinal).Count() != approval.SelectedItemIds.Length
            || approval.SelectedItemIds.Any(id => !plan.Content.Items.Any(i => i.Id == id)))
        {
            reasons.Add("invalid-selection-or-operator");
            return new(false, reasons.ToImmutable());
        }
        if (now < approval.ApprovedAt || approval.ApprovedAt < plan.Content.CreatedAt || now >= approval.ExpiresAt
            || now >= plan.Content.ExpiresAt || approval.ExpiresAt > plan.Content.ExpiresAt || approval.ExpiresAt <= approval.ApprovedAt)
        {
            reasons.Add("approval-expired-or-not-yet-valid");
        }
        if (plan.Content.Items.Where(i => approval.SelectedItemIds.Contains(i.Id)).Any(i => i.Dependencies != currentDependencies))
        {
            reasons.Add("decision-dependencies-changed");
        }
        if (plan.Content.Items.Where(i => approval.SelectedItemIds.Contains(i.Id)).Any(i => !PlanBuilder.CanSelect(i, now, plan.Content.Scope)))
        {
            reasons.Add("item-not-executable");
        }
        return new(reasons.Count == 0, reasons.ToImmutable());
    }
}
