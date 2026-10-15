using System.Collections.Immutable;
using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Core.Reporting;

public sealed record ReportInput(DiscoveryResult Discovery, RemediationPlan Plan,
    ImmutableArray<ExecutionResult> Outcomes);

public static class ReportSerializer
{
    // The schema is a persistence allowlist, not serialization of arbitrary provider bodies.
    public static string Serialize(ReportInput input)
    {
        // Holds and reasons can originate in provider/model/user text. Only fixed codes cross
        // this persistence seam. Unknown holds retain their existence without their content.
        string SafeHold(string hold) => hold switch
        {
            "detail read unavailable" => hold,
            "identity-mismatch" or "calendar-mismatch" or "read-unavailable" or "invalid-response"
                or "event-budget" or "page-budget" or "detail-budget" or "duration-budget"
                or "conflicting-observation" or "invalid-next-link" or "paging-cycle" => hold,
            _ => "unresolved-discovery-hold"
        };
        return JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            discovery = new { complete = input.Discovery.Complete, holds = input.Discovery.Holds.Select(SafeHold).Distinct().ToArray() },
            planDigest = input.Plan.Digest,
            items = input.Plan.Content.Items.Select(item =>
            {
                var outcome = input.Outcomes.LastOrDefault(o => o.ItemId == item.Id);
                return new
                {
                    itemId = item.Id,
                    disposition = item.Disposition.ToString(),
                    outcome = (outcome?.Outcome ?? ExecutionOutcome.Proposed).ToString(),
                    organizerVerified = outcome is { Outcome: ExecutionOutcome.Verified, OrganizerVerified: true },
                    verificationScope = outcome is { Outcome: ExecutionOutcome.Verified, OrganizerVerified: true } ? "OrganizerFields" : "None",
                    // This executor has no independent scheduling-proof seam. UTC fields and
                    // original-zone provenance cannot establish provider scheduling semantics.
                    schedulingVerified = false,
                    deliveryVerified = false
                };
            }).ToArray()
        });
    }
}
