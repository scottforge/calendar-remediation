using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Core.Planning;

public enum PreviewDisposition { Proposed, Unchanged, Unresolved, Unsupported }

public sealed record MeetingTimePreview(PreviewDisposition Disposition,
    DateTimeOffset? CurrentStart, DateTimeOffset? CurrentEnd,
    DateTimeOffset? ProposedStart, DateTimeOffset? ProposedEnd, string TimeZoneId, string Reason);

public static class ReadOnlyMeetingPreview
{
    public static MeetingTimePreview Calculate(SourceSnapshot source, DateTime intendedLocalStart,
        TimeZoneInfo timeZone, bool preserveInstant, DateTimeOffset now)
    {
        MeetingTimePreview Reject(PreviewDisposition disposition, string reason) =>
            new(disposition, source.Start, source.End, null, null, timeZone.Id, reason);

        if (source.IsOrganizer == false || source.IsCancelled == true || source.IsAllDay == true
            || source.Kind is EventKind.Occurrence or EventKind.Exception or EventKind.SeriesMaster
            || !string.IsNullOrEmpty(source.SeriesMasterId))
        {
            return Reject(PreviewDisposition.Unsupported, "Choose an organizer-owned, active, timed single meeting.");
        }
        if (source.IsOrganizer != true || source.IsCancelled != false || source.IsAllDay != false
            || source.Kind != EventKind.SingleInstance)
        {
            return Reject(PreviewDisposition.Unresolved, "The meeting's ownership, status, or type is unknown.");
        }
        if (source.Start is not { } start || source.End is not { } end || end <= start)
        {
            return Reject(PreviewDisposition.Unresolved, "The meeting needs valid start and end times with a positive duration.");
        }
        if (start <= now)
        {
            return Reject(PreviewDisposition.Unsupported, "Choose a meeting that starts in the future.");
        }

        const string rules = "This read-only preview uses machine-installed time-zone rules, not provider-verified rules or an adopted policy.";
        if (preserveInstant)
        {
            return new(PreviewDisposition.Unchanged, start, end, start, end, timeZone.Id,
                "The meeting keeps its current instant and duration. " + rules);
        }
        if (intendedLocalStart.Kind != DateTimeKind.Unspecified)
        {
            return Reject(PreviewDisposition.Unresolved, "Enter a local date and time without an offset or UTC designation.");
        }
        try
        {
            if (timeZone.IsInvalidTime(intendedLocalStart))
            {
                return Reject(PreviewDisposition.Unresolved, "That local time does not exist because the clock moves forward. Choose another time.");
            }
            if (timeZone.IsAmbiguousTime(intendedLocalStart))
            {
                return Reject(PreviewDisposition.Unresolved, "That local time occurs twice because the clock moves back. Choose an unambiguous time.");
            }
            // DateTimeOffset validates the offset range without silently clamping boundary conversions.
            var proposedStart = new DateTimeOffset(intendedLocalStart, timeZone.GetUtcOffset(intendedLocalStart)).ToUniversalTime();
            var proposedEnd = proposedStart.Add(end - start);
            if (proposedStart <= now)
            {
                return Reject(PreviewDisposition.Unresolved, "Choose a proposed start time in the future.");
            }
            var unchanged = proposedStart == start;
            return new(unchanged ? PreviewDisposition.Unchanged : PreviewDisposition.Proposed,
                start, end, proposedStart, proposedEnd, timeZone.Id,
                (unchanged ? "The proposed time matches the current meeting. " : "The proposed time preserves the meeting's elapsed duration. ") + rules);
        }
        catch (ArgumentException)
        {
            return Reject(PreviewDisposition.Unresolved, "The proposed meeting time is outside the supported date range.");
        }
        catch (OverflowException)
        {
            return Reject(PreviewDisposition.Unresolved, "The proposed meeting time is outside the supported date range.");
        }
    }
}
