using System.Text.Json;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Feasibility;

internal enum ConditionalProbeOutcomeKind
{
    ObservedStaleRejection,
    StaleAccepted,
    StaleAcceptedReadbackUnknown,
    Inconclusive,
    Unknown
}

internal static class ConditionalProbeOutcome
{
    internal static ConditionalProbeOutcomeKind Classify(int? status, bool? competitorPreserved)
    {
        if (status is >= 200 and < 300)
        {
            return competitorPreserved is null ? ConditionalProbeOutcomeKind.StaleAcceptedReadbackUnknown
                : ConditionalProbeOutcomeKind.StaleAccepted;
        }
        if (status is null || competitorPreserved is null)
        {
            return ConditionalProbeOutcomeKind.Unknown;
        }
        return status == 412 && competitorPreserved == true ? ConditionalProbeOutcomeKind.ObservedStaleRejection
            : ConditionalProbeOutcomeKind.Inconclusive;
    }

    internal static ConditionalProbeResult CreateResult(int? status, bool? competitorPreserved,
        JsonElement before, JsonElement? after, GraphExchange[] exchanges)
    {
        var kind = Classify(status, competitorPreserved);
        var outcome = kind switch
        {
            ConditionalProbeOutcomeKind.ObservedStaleRejection => "Observed stale rejection on this disposable appointment only",
            ConditionalProbeOutcomeKind.StaleAccepted => "FAILED: stale guard was accepted",
            ConditionalProbeOutcomeKind.StaleAcceptedReadbackUnknown => "FAILED: stale guard was accepted. Subsequent readback is unknown. Never replay.",
            ConditionalProbeOutcomeKind.Inconclusive => "Inconclusive: rejection or preservation not established",
            _ => "UNKNOWN: dispatch or readback uncertain. Hold and inspect manually. Never replay."
        };
        return new(outcome, status == 412 && competitorPreserved is not null, status is >= 200 and < 300,
            competitorPreserved == true,
            competitorPreserved is null
                ? "Unproven. Desired fields or an immediate unchanged read cannot establish attribution."
                : "Response and organizer readback only. Delivery, actor attribution after loss, and general recoverability are unproven.",
            before, after, exchanges);
    }
}
