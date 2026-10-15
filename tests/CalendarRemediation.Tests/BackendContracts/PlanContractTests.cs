using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class PlanContractTests
{
    [Fact]
    public void CanonicalDigestIsStableUnderItemOrderAndCulture()
    {
        var first = ContractFixture.Item;
        var second = ContractFixture.ItemFor("item-2", "event-2");
        var originalCulture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var a = PlanBuilder.Build(ContractFixture.Draft with { Items = [first, second] });
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");
            var b = PlanBuilder.Build(ContractFixture.Draft with { Items = [second, first] });
            Assert.Equal(a.CanonicalJson, b.CanonicalJson);
            Assert.Equal(a.Digest, b.Digest);
            Assert.Matches("^[a-fA-F0-9]{64}$", a.Digest);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(a.CanonicalJson))), a.Digest, ignoreCase: true);
            Assert.Contains("v1", a.CanonicalJson, StringComparison.Ordinal);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData("patch")]
    [InlineData("version")]
    [InlineData("rules")]
    public void ExecutableOrEvidenceChangesChangeDigest(string change)
    {
        var item = ContractFixture.Item;
        var changed = change switch
        {
            "patch" => item with { Patch = item.Patch! with { Start = item.Patch.Start.AddMinutes(1) } },
            "version" => item with
            {
                Source = item.Source with { Version = "v2", Guard = "\"v2\"" },
                Intent = item.Intent with { SourceVersion = "v2", EvidenceId = "confirmation-v2" }
            },
            _ => item with { Dependencies = item.Dependencies with { TargetRulesHash = "different-rule-artifact" } }
        };
        var before = PlanBuilder.Build(ContractFixture.Draft);
        var after = PlanBuilder.Build(ContractFixture.Draft with { Items = [changed] });

        Assert.NotEqual(before.Digest, after.Digest);
        var differentConfirmation = PlanBuilder.Build(ContractFixture.Draft with
        {
            Items = [item with { Intent = item.Intent with { ConfirmedBy = "another-operator", EvidenceId = "new-confirmation" } }]
        });
        Assert.NotEqual(before.Digest, differentConfirmation.Digest);
    }

    [Fact]
    public void IncompleteDiscoveryCannotProduceAnExecutablePlan()
    {
        Assert.Throws<InvalidOperationException>(() => PlanBuilder.Build(ContractFixture.Draft with { DiscoveryComplete = false }));
    }

    [Fact]
    public void ApprovalIsBoundToExactSelectedItemsAndOperator()
    {
        var plan = PlanBuilder.Build(ContractFixture.Draft with
        {
            Items = [ContractFixture.Item, ContractFixture.ItemFor("item-2", "event-2")]
        });
        var approval = PlanApproval.Approve(plan, "alice", ["item-2"], ContractFixture.Now, ContractFixture.Now.AddMinutes(10));

        Assert.Equal<string>(["item-2"], approval.SelectedItemIds);
        Assert.Equal("alice", approval.Operator);
        Assert.Equal(plan.Digest, approval.Digest);
        Assert.Equal(plan.Content.Id, approval.PlanId);
        Assert.True(PlanApproval.Validate(plan, approval, ContractFixture.Now, ContractFixture.Dependencies).Valid);
    }

    [Fact]
    public void ApprovalCannotSelectAnItemOutsideTheExactPlan()
    {
        var plan = ContractFixture.Plan;
        Assert.Throws<ArgumentException>(() => PlanApproval.Approve(plan, "alice", ["unknown-item"], ContractFixture.Now, ContractFixture.Now.AddMinutes(10)));
    }

    [Fact]
    public void ApprovalExpiresAtTheBoundaryAndCannotOutlivePlan()
    {
        var plan = ContractFixture.Plan;
        var expiry = ContractFixture.Now.AddMinutes(10);
        var approval = PlanApproval.Approve(plan, "alice", ["item-1"], ContractFixture.Now, expiry);

        Assert.False(PlanApproval.Validate(plan, approval, expiry, ContractFixture.Dependencies).Valid);
        Assert.Throws<ArgumentException>(() => PlanApproval.Approve(plan, "alice", ["item-1"], ContractFixture.Now, plan.Content.ExpiresAt.AddTicks(1)));
    }

    [Fact]
    public void ChangedDependenciesOrTamperedDigestInvalidateApproval()
    {
        var plan = ContractFixture.Plan;
        var approval = ContractFixture.Approval(plan);

        Assert.False(PlanApproval.Validate(plan, approval, ContractFixture.Now,
            ContractFixture.Dependencies with { IntentHash = "new-confirmation" }).Valid);
        Assert.False(PlanApproval.Validate(plan, approval with { Digest = new string('0', 64) }, ContractFixture.Now,
            ContractFixture.Dependencies).Valid);
    }
}
