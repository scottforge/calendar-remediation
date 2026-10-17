namespace CalendarRemediation.Host.Review.Models;

public sealed class DemoMeeting(string id, string title, string category, DateTimeOffset start, int duration, string[] attendees, string hypothesis)
{
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Category { get; } = category;
    public DateTimeOffset Start { get; } = start;
    public int Duration { get; } = duration;
    public string[] Attendees { get; } = attendees;
    public string Hypothesis { get; } = hypothesis;
    public string Notes { get; init; } = "";
    public DemoIntent Intent { get; internal set; }
    public bool IsSelected { get; internal set; }
    public bool HasManualDecision { get; internal set; }
    public DemoAssessment Assessment { get; internal set; } = DemoAssessment.NotAssessed;
    public DemoRecurrence Recurrence { get; init; }
    public bool IsSupported => Recurrence != DemoRecurrence.Complex;
    public string RecurrenceScope => Recurrence switch
    {
        DemoRecurrence.Weekly => "Weekly series: November 10, 17 and 24, 2026 only. All three occurrences, no exceptions.",
        DemoRecurrence.Complex => "Unsupported series: moved occurrences and cancellations. Exact effects cannot be determined.",
        _ => "Single meeting only"
    };
}
