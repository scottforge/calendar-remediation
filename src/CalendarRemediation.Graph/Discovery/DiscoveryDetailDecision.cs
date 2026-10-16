using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

internal sealed record DiscoveryDetailDecision(SourceSnapshot Snapshot, string? Hold)
{
    public static DiscoveryDetailDecision Reconcile(SourceSnapshot source, SourceSnapshot? detail)
    {
        if (detail is null)
        {
            return new(source with { Unresolved = source.Unresolved.Add("detail-read-unavailable") }, "detail read unavailable");
        }
        return new(detail, SourceSnapshotEvidence.Matches(source, detail) ? null : "conflicting-observation");
    }
}
