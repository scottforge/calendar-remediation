using System.Text.Json;
using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Graph.Discovery;

internal enum DiscoveryPageAction { ReadPage, Stop }
internal enum DiscoveryObservationAction { ReadDetail, Skip, Stop }

internal sealed record DiscoveryObservationDecision(DiscoveryObservationAction Action, SourceSnapshot? Source = null);

internal sealed class DiscoveryState(DiscoveryScope scope)
{
    private readonly List<string> holds = [];
    private readonly HashSet<string> zones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceSnapshot> views = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SourceSnapshot> events = new(StringComparer.Ordinal);
    private readonly HashSet<string> visited = new(StringComparer.Ordinal);
    private int pages;
    private int details;
    private int observations;

    public DiscoveryPageAction AdmitPage(string address)
    {
        var hold = pages >= scope.Budget.Pages ? "page-budget"
            : !visited.Add(address) ? "paging-cycle"
            : !GraphApi.IsCalendarViewUri(address) ? "invalid-next-link"
            : null;
        if (hold is null) { return DiscoveryPageAction.ReadPage; }
        AddHold(hold);
        return DiscoveryPageAction.Stop;
    }

    // Admission reserves an address, but only a successful fetch consumes a page.
    public void RecordPageFetched() => pages++;

    public DiscoveryObservationDecision AdmitObservation(JsonElement observation)
    {
        // Over-budget observations must stop before mapping even a malformed payload.
        return AdmitObservation(observations >= scope.Budget.Events ? null : GraphEventMapper.Map(scope.Identity, observation));
    }

    public DiscoveryObservationDecision AdmitObservation(SourceSnapshot? source)
    {
        if (++observations > scope.Budget.Events)
        {
            AddHold("event-budget");
            return new(DiscoveryObservationAction.Stop);
        }
        if (source is null)
        {
            AddHold("invalid-response");
            return new(DiscoveryObservationAction.Skip);
        }
        if (views.TryGetValue(source.Id, out var previous))
        {
            if (!SourceSnapshotEvidence.Matches(previous, source)) { AddHold("conflicting-observation"); }
            return new(DiscoveryObservationAction.Skip);
        }
        views.Add(source.Id, source);
        if (details >= scope.Budget.Details)
        {
            AddHold("detail-budget");
            return new(DiscoveryObservationAction.Stop);
        }
        details++;
        return new(DiscoveryObservationAction.ReadDetail, source);
    }

    public void RecordDetail(DiscoveryDetailDecision decision)
    {
        if (decision.Hold is not null) { AddHold(decision.Hold); }
        events.Add(decision.Snapshot.Id, decision.Snapshot);
    }

    public void AddHold(string hold) => holds.Add(hold);

    public void AddZones(IEnumerable<string> aliases) => zones.UnionWith(aliases);

    public DiscoveryResult Snapshot() => new(scope, holds.Count == 0,
        [.. events.Values.OrderBy(e => e.Id, StringComparer.Ordinal)],
        [.. zones.Order(StringComparer.Ordinal)], [.. holds.Distinct(StringComparer.Ordinal)], pages, details);
}
