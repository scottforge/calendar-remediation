using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class DiscoveryDecisionTests
{
    [Fact]
    public void PageAdmissionReservesAddressWithoutCountingAnUnfinishedFetch()
    {
        var state = new DiscoveryState(ContractFixture.Scope);
        var address = GraphApi.CalendarView(ContractFixture.Scope.Start, ContractFixture.Scope.End);

        Assert.Equal(DiscoveryPageAction.ReadPage, state.AdmitPage(address));
        Assert.Equal(0, state.Snapshot().Pages);
        Assert.Equal(DiscoveryPageAction.Stop, state.AdmitPage(address));

        var result = state.Snapshot();
        Assert.False(result.Complete);
        Assert.Equal("paging-cycle", Assert.Single(result.Holds));
        Assert.Equal(0, result.Pages);
        Assert.Equal(0, result.DetailReads);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void FetchedPageExhaustsPageBudgetBeforeAdmittingAnotherAddress()
    {
        var scope = ContractFixture.Scope with { Budget = ContractFixture.Scope.Budget with { Pages = 1 } };
        var state = new DiscoveryState(scope);
        Assert.Equal(DiscoveryPageAction.ReadPage, state.AdmitPage(GraphApi.CalendarView(scope.Start, scope.End)));
        state.RecordPageFetched();

        Assert.Equal(DiscoveryPageAction.Stop, state.AdmitPage(GraphApi.CalendarViewUrl + "?$skiptoken=more"));

        Assert.Equal(1, state.Snapshot().Pages);
        Assert.Equal("page-budget", Assert.Single(state.Snapshot().Holds));
    }

    [Fact]
    public void MalformedObservationConsumesEventBudgetWithoutReservingDetailWork()
    {
        var scope = ContractFixture.Scope with { Budget = ContractFixture.Scope.Budget with { Events = 1 } };
        var state = new DiscoveryState(scope);

        Assert.Equal(DiscoveryObservationAction.Skip, state.AdmitObservation(null).Action);
        Assert.Equal(DiscoveryObservationAction.Stop, state.AdmitObservation(ContractFixture.Source).Action);

        var result = state.Snapshot();
        Assert.Contains("invalid-response", result.Holds);
        Assert.Contains("event-budget", result.Holds);
        Assert.Equal(2, result.Holds.Length);
        Assert.Equal(0, result.DetailReads);
        Assert.Empty(result.Events);
    }

    [Fact]
    public void MatchingDuplicateAtDetailLimitIsSkippedButNewIdentityStops()
    {
        var scope = ContractFixture.Scope with { Budget = ContractFixture.Scope.Budget with { Details = 1 } };
        var state = new DiscoveryState(scope);
        var source = ContractFixture.Source;
        var first = state.AdmitObservation(source);
        Assert.Equal(DiscoveryObservationAction.ReadDetail, first.Action);
        Assert.Same(source, first.Source);
        state.RecordDetail(DiscoveryDetailDecision.Reconcile(source, source));
        var equivalent = source with
        {
            Attendees = [.. source.Attendees],
            Unresolved = [.. source.Unresolved]
        };

        Assert.Equal(DiscoveryObservationAction.Skip, state.AdmitObservation(equivalent).Action);
        Assert.True(state.Snapshot().Complete);
        Assert.Equal(DiscoveryObservationAction.Stop, state.AdmitObservation(source with { Id = "another-event" }).Action);

        var result = state.Snapshot();
        Assert.Equal("detail-budget", Assert.Single(result.Holds));
        Assert.Equal(1, result.DetailReads);
        Assert.Same(source, Assert.Single(result.Events));
    }

    [Fact]
    public void DuplicateWithChangedRawEvidenceConflictsWithoutAnotherDetailReservation()
    {
        var state = new DiscoveryState(ContractFixture.Scope);
        var source = ContractFixture.Source;
        Assert.Equal(DiscoveryObservationAction.ReadDetail, state.AdmitObservation(source).Action);
        state.RecordDetail(DiscoveryDetailDecision.Reconcile(source, source));
        var changed = source with { Zones = source.Zones with { OriginalStart = "different-original-zone" } };

        Assert.Equal(DiscoveryObservationAction.Skip, state.AdmitObservation(changed).Action);

        var result = state.Snapshot();
        Assert.Equal("conflicting-observation", Assert.Single(result.Holds));
        Assert.Equal(1, result.DetailReads);
        Assert.Same(source, Assert.Single(result.Events));
    }

    [Fact]
    public void UnavailableDetailAddsHoldWithoutLosingExistingUnresolvedEvidence()
    {
        var source = ContractFixture.Source with { Unresolved = ["original-zone-unknown"] };

        var decision = DiscoveryDetailDecision.Reconcile(source, null);

        Assert.Equal("detail read unavailable", decision.Hold);
        Assert.True(SourceSnapshotEvidence.Matches(source with
        {
            Unresolved = ["original-zone-unknown", "detail-read-unavailable"]
        }, decision.Snapshot));
        Assert.Equal("original-zone-unknown", Assert.Single(source.Unresolved));
    }

    [Fact]
    public void ConflictingDetailIsRetainedWithHoldInsteadOfSilentlyKeepingViewEvidence()
    {
        var source = ContractFixture.Source;
        var detail = source with { Version = "new-version", Guard = "\"new-version\"" };

        var decision = DiscoveryDetailDecision.Reconcile(source, detail);

        Assert.Equal("conflicting-observation", decision.Hold);
        Assert.Same(detail, decision.Snapshot);
    }

    [Fact]
    public void ContinuationPreservesOpaqueAddressAndDistinguishesEndFromInvalidLink()
    {
        const string address = GraphApi.CalendarViewUrl + "?$skiptoken=opaque%2B%2F%3D&custom=one+two";
        using var nextPage = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["@odata.nextLink"] = address
        }));
        using var lastPage = JsonDocument.Parse("{}");
        using var invalidPage = JsonDocument.Parse("{\"@odata.nextLink\":null}");

        var next = DiscoveryContinuation.Parse(nextPage.RootElement);

        Assert.Equal(DiscoveryContinuationKind.Next, next.Kind);
        Assert.Equal(address, next.Address);
        var end = DiscoveryContinuation.Parse(lastPage.RootElement);
        Assert.Equal(DiscoveryContinuationKind.End, end.Kind);
        Assert.Null(end.Address);
        var invalid = DiscoveryContinuation.Parse(invalidPage.RootElement);
        Assert.Equal(DiscoveryContinuationKind.Invalid, invalid.Kind);
        Assert.Null(invalid.Address);
    }
}
