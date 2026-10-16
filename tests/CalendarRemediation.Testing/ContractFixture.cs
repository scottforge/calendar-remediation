using System.Collections.Immutable;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Intent;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Testing;

public static class ContractFixture
{
    public static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    public static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    public static CalendarIdentity Identity => new("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222", "calendar");
    public static DiscoveryScope Scope => new(Identity, Now, Now.AddDays(30), Now, new(100, 10, 100, TimeSpan.FromMinutes(1)));
    public static SourceSnapshot Source => new(Identity, "event", "v1", "\"v1\"",
        At("2030-01-02T16:00:00Z"), At("2030-01-02T17:00:00Z"),
        new("2030-01-02T16:00:00", "UTC"), new("2030-01-02T17:00:00", "UTC"), true, "owner@example.test",
        [new("person@example.test", AttendeeKind.Internal)], false, false, EventKind.SingleInstance,
        null, new("Fixture/OldMinus07", "Fixture/OldMinus07", "UTC"), []);
    public static DecisionDependencies Dependencies => new("intent-hash", "policy-hash", "source-rules-hash",
        "target-rules-hash", "mapping-hash", "adoption-hash", "admission-hash");
    public static RulePolicy Policy => new("test-only-policy", true, true, Now, Dependencies);
    public static ConfirmedIntent Intent => new("confirmation-1", Identity, "event", "v1", PreservationMode.PreserveLocalStart,
        new DateTime(2030, 1, 2, 9, 0, 0, DateTimeKind.Unspecified), "Fixture/NewMinus06", "operator", Now);
    public static WriteAdmission Admission => new(Identity, "internal-attendee-single", true, true,
        "If-Match", "admission-hash", "test-reviewer");
    public static DiscoveryResult Discovery => new(Scope, true, [Source], ["UTC", "Fixture/NewMinus06"], [], 1, 1);
    public static AssessmentContext Context => new(Discovery, Intent, Policy, Admission, Now);
    public static ExactPatch Patch => new(At("2030-01-02T15:00:00Z"), At("2030-01-02T16:00:00Z"), "Fixture/NewMinus06");
    public static PlanItem Item => new("item-1", Source, Intent, AssessmentDisposition.Change, Patch, Dependencies,
        "Organizer update only. Delivery and attendee responses unverified.");
    public static PlanItem ItemFor(string itemId, string eventId) => Item with
    {
        Id = itemId,
        Source = Source with { Id = eventId },
        Intent = Intent with { EventId = eventId, EvidenceId = "confirmation-" + eventId }
    };
    public static PlanDraft Draft => new("plan-1", Scope, true, [Item], Now, Now.AddHours(1));

    // Execution and report tests consume real public plan output. Implement the builder
    // before these seams so a fabricated digest cannot bypass integrity validation.
    public static RemediationPlan Plan => PlanBuilder.Build(Draft);
    public static Approval Approval(RemediationPlan plan, ImmutableArray<string>? selected = null)
        => new("operator", plan.Content.Id, plan.Digest, selected ?? ["item-1"], Now, Now.AddMinutes(30));
}

public sealed class FixtureRules(LocalResolution? resolution = null) : IRuleResolver
{
    public List<(DateTime Local, string Zone, RulePolicy Policy)> Calls { get; } = [];

    public LocalResolution Resolve(DateTime localStart, string zone, RulePolicy policy)
    {
        Calls.Add((localStart, zone, policy));
        return resolution ?? new(ResolutionKind.Unique, [ContractFixture.At("2030-01-02T15:00:00Z")]);
    }
}
