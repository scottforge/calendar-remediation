using System.Text.Json;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Core.Planning;
using CalendarRemediation.Core.Reporting;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class ReportContractTests
{
    [Fact]
    public void VersionedReportPreservesIncompleteScopeAndDistinguishesProposalFromExecution()
    {
        var plan = ContractFixture.Plan;
        var discovery = ContractFixture.Discovery with { Complete = false, Holds = ["detail read unavailable"] };
        using var json = JsonDocument.Parse(ReportSerializer.Serialize(new(discovery, plan, [])));

        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(json.RootElement.GetProperty("discovery").GetProperty("complete").GetBoolean());
        Assert.Equal(plan.Digest, json.RootElement.GetProperty("planDigest").GetString());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("Change", item.GetProperty("disposition").GetString());
        Assert.Equal("Proposed", item.GetProperty("outcome").GetString());
        Assert.False(item.GetProperty("organizerVerified").GetBoolean());
        Assert.False(item.GetProperty("deliveryVerified").GetBoolean());
        Assert.Contains("detail read unavailable", json.RootElement.GetProperty("discovery").GetProperty("holds").ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VerifiedConflictAndUnknownRemainSeparateWithoutChangingPlanDigest()
    {
        var first = ContractFixture.Item;
        var plan = PlanBuilder.Build(ContractFixture.Draft with
        {
            Items = [first, ContractFixture.ItemFor("item-2", "event-2"), ContractFixture.ItemFor("item-3", "event-3")]
        });
        var report = new ReportInput(ContractFixture.Discovery, plan,
        [
            new("item-1", ExecutionOutcome.Verified, "organizer readback matches", true, false),
            new("item-2", ExecutionOutcome.Conflict, "source guard rejected", false, false),
            new("item-3", ExecutionOutcome.Unknown, "response lost, held", false, false)
        ]);
        using var json = JsonDocument.Parse(ReportSerializer.Serialize(report));
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal<string?>(["Verified", "Conflict", "Unknown"], items.Select(i => i.GetProperty("outcome").GetString()));
        Assert.True(items[0].GetProperty("organizerVerified").GetBoolean());
        Assert.All(items, i => Assert.False(i.GetProperty("deliveryVerified").GetBoolean()));
        Assert.Equal(plan.Digest, json.RootElement.GetProperty("planDigest").GetString());
    }

    [Fact]
    public void OperationalFreeTextIsNotABackdoorForCredentialsOrUnselectedContent()
    {
        var plan = ContractFixture.Plan;
        var result = new ExecutionResult("item-1", ExecutionOutcome.Unknown,
            "Authorization: Bearer credential-must-not-persist Subject: private-subject Body: private-description", false, false);
        var json = ReportSerializer.Serialize(new(ContractFixture.Discovery, plan, [result]));

        Assert.DoesNotContain("credential-must-not-persist", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-subject", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-description", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("Unknown", Assert.Single(document.RootElement.GetProperty("items").EnumerateArray()).GetProperty("outcome").GetString());
    }
}
