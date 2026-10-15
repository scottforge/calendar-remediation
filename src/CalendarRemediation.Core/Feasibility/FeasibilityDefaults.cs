namespace CalendarRemediation.Core.Feasibility;

public static class FeasibilityDefaults
{
    public static FeasibilityEvidence Create(DateTimeOffset now) => new(
        new(1, "", "", "", now.AddDays(14), "/v1.0/me/events/{immutableId}", "v1.0",
            ProductVerdict.Blocked, null, null, [],
            "If the exact Graph guard fails, an owner may spend at most two working days checking documented supported alternatives. No alternative is established."),
        new(1,
            "A source-rule reconstruction is a candidate only. Require independent intent confirmation and route contradictions to individual review.",
            "Ask the user to choose local start or absolute instant. Preserve elapsed duration. Resolve gaps and overlaps explicitly.",
            "Explicit correction does not require legislation change but requires confirmed intent and reproducible applicable target rules.",
            [], [], [], [], [], null, null),
        new(1, now, now, false, 0, 0, 0, 0, 0, 0, null, 0, 0, 0, 0, 0, "Not measured", [], null, null),
        new(1, [new("OrganizerInternalAttendeesSingleInstance", false, null, null,
            "Not observed", "Delivery and acceptance are unproven", []),
            new("ExternalAttendees", false, null, null, "Not observed", "Separate evidence required", []),
            new("Resources", false, null, null, "Excluded", "Separate product scope required", []),
            new("Recurrence", false, null, null, "Blocked required later milestone", "History and exceptions unproven", [])]));
}
