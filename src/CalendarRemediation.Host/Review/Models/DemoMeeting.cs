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
    public DemoIntent Intent { get; internal set; }
}
