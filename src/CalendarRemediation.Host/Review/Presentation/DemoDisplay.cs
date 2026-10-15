using System.Globalization;
using CalendarRemediation.Host.Review.Models;

namespace CalendarRemediation.Host.Review.Presentation;

public static class DemoDisplay
{
    public static CultureInfo Culture { get; } = CultureInfo.GetCultureInfo("en-US");

    public static DateTimeOffset ProposedStart(DemoMeeting meeting, DemoIntent intent, DemoLocation location) => intent == DemoIntent.Local
        ? meeting.Start.AddHours(location.SourceOffset - location.TargetOffset) : meeting.Start;
    public static string SourceTime(DemoMeeting meeting, DemoLocation location) => LocalTime(meeting.Start, location.SourceOffset);
    public static string TargetTime(DemoMeeting meeting, DemoIntent intent, DemoLocation location) => LocalTime(ProposedStart(meeting, intent, location), location.TargetOffset);
    public static string LocalTime(DateTimeOffset instant, int offset) => instant.ToOffset(TimeSpan.FromHours(offset)).ToString("h:mm tt", Culture);
    public static string UtcTime(DateTimeOffset instant) => instant.ToUniversalTime().ToString("HH:mm 'UTC'", Culture);
    public static string Offset(int hours) => $"{(hours < 0 ? "−" : "+")}{Math.Abs(hours):D2}:00";
    public static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part[0]));
    public static string StatusLabel(DemoMeeting meeting) => meeting.Intent switch
    {
        DemoIntent.Local => "Keep local time",
        DemoIntent.Instant => "Keep instant",
        DemoIntent.Skip => "Skipped",
        _ => "Needs review"
    };
    public static string StatusClass(DemoMeeting meeting) => meeting.Intent switch
    {
        DemoIntent.Local or DemoIntent.Instant => "green",
        DemoIntent.Skip => "neutral",
        _ => "amber"
    };
    public static string IntentNote(DemoIntent intent) => intent switch
    {
        DemoIntent.Local => "Your demo confirmation: preserve local start. This enters the preview as a time update.",
        DemoIntent.Instant => "Your demo confirmation: preserve the absolute instant. This enters the preview as a no-change decision.",
        DemoIntent.Skip => "Skipped by you. This meeting is excluded from approval and simulation.",
        _ => "No confirmed intent. The comparison shows no update, and this meeting stays outside the batch."
    };
}
