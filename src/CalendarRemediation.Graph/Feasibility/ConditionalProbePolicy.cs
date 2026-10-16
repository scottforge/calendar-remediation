using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Feasibility;

internal static class ConditionalProbePolicy
{
    internal static void ValidatePrepareInput(string tenantId, string mailboxId, string marker,
        string experimentOwner, DateTimeOffset experimentStopDate, DateTimeOffset now)
    {
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(mailboxId, out _) || !Guid.TryParse(marker, out _))
        {
            throw new ArgumentException("Tenant, mailbox and disposable marker must be GUIDs.");
        }
        if (string.IsNullOrWhiteSpace(experimentOwner) || experimentStopDate <= now
            || experimentStopDate - now > TimeSpan.FromDays(14))
        {
            throw new ArgumentException("A named experiment owner and a future stop date within 14 days are required.");
        }
    }

    internal static void ValidatePrepared(PreparedProbe prepared, DateTimeOffset proposedStart,
        DateTimeOffset proposedEnd, DateTimeOffset now)
    {
        if (prepared.PreparedAt == default || prepared.PreparedAt > now || prepared.ExpiresAt <= now
            || prepared.ExpiresAt <= prepared.PreparedAt
            || prepared.ExpiresAt - prepared.PreparedAt > TimeSpan.FromMinutes(30)
            || string.IsNullOrWhiteSpace(prepared.ExperimentOwner) || prepared.ExperimentStopDate <= now
            || prepared.ExpiresAt > prepared.ExperimentStopDate
            || prepared.ExperimentStopDate - prepared.PreparedAt > TimeSpan.FromDays(14)
            || proposedStart <= now || proposedEnd <= proposedStart)
        {
            throw new InvalidOperationException("Probe expired or proposed future interval is invalid.");
        }
        ValidateDisposable(prepared.Source, prepared.EventId, prepared.Marker, now);
        ValidateGuard(prepared.SourceETag);
        if (prepared.SourceETag != GraphJson.Text(prepared.Source, "@odata.etag"))
        {
            throw new InvalidDataException("Prepared guard does not match source observation.");
        }
    }

    internal static void ValidateDisposable(JsonElement item, string eventId, string marker, DateTimeOffset now)
    {
        if (!Guid.TryParse(marker, out _) || string.IsNullOrWhiteSpace(eventId) || GraphJson.Text(item, "id") != eventId
            || GraphJson.Text(item, "subject") != "PHASE0-DISPOSABLE:" + marker
            || GraphJson.Boolean(item, "isOrganizer") != true || GraphJson.Boolean(item, "isAllDay") != false
            || GraphJson.Boolean(item, "isCancelled") != false
            || !GraphJson.TryArray(item, "attendees", out var attendees) || attendees.GetArrayLength() != 0
            || GraphJson.Text(item, "type") != "singleInstance"
            || string.IsNullOrWhiteSpace(GraphJson.Text(item, "changeKey")))
        {
            throw new InvalidOperationException("Only a marked, organizer-owned attendee-free timed single-instance disposable appointment is allowed.");
        }
        var start = Time(item, "start");
        var end = Time(item, "end");
        if (start <= now || end <= start)
        {
            throw new InvalidOperationException("Disposable probe requires a valid future interval.");
        }
    }

    internal static void ValidateCompetingEdit(PreparedProbe prepared, JsonElement competitor)
    {
        var currentTag = GraphJson.Text(competitor, "@odata.etag");
        ValidateGuard(currentTag);
        var changeKey = GraphJson.Text(competitor, "changeKey");
        if (string.IsNullOrWhiteSpace(changeKey))
        {
            throw new InvalidDataException("Competing observation requires a change key.");
        }
        var sameSchedule = SameTime(competitor, prepared.Source);
        if (Time(competitor, "end") <= Time(competitor, "start"))
        {
            throw new InvalidOperationException("Competing observation requires a valid interval.");
        }
        if (currentTag == prepared.SourceETag || changeKey == GraphJson.Text(prepared.Source, "changeKey")
            || sameSchedule)
        {
            throw new InvalidOperationException("Introduce a competing start/end edit in Outlook before the stale-write test.");
        }
    }

    internal static void ValidateDispatch(PreparedProbe prepared, DateTimeOffset now)
    {
        if (prepared.ExpiresAt <= now || prepared.ExperimentStopDate <= now)
        {
            throw new InvalidOperationException("Experiment expired before dispatch. The durable attempt remains consumed.");
        }
    }

    internal static void ValidatePatch(JsonElement competitor, JsonElement patch)
    {
        if (SameTime(competitor, patch))
        {
            throw new InvalidOperationException("Proposed patch must differ from the competing edit.");
        }
    }

    internal static void ValidateGuard(string? etag)
    {
        if (!EntityTagHeaderValue.TryParse(etag, out var tag) || tag.Tag == "*")
        {
            throw new InvalidDataException("A valid source ETag is required. A change key is not substituted for a guard.");
        }
    }

    private static bool SameTime(JsonElement a, JsonElement b)
    {
        var aStart = Time(a, "start");
        var aEnd = Time(a, "end");
        var bStart = Time(b, "start");
        var bEnd = Time(b, "end");
        return aStart == bStart && aEnd == bEnd;
    }

    private static DateTimeOffset Time(JsonElement item, string field)
    {
        return GraphEventParser.ParseTime(item, field).Instant
            ?? throw new InvalidDataException("Disposable probe requires valid explicit UTC scheduling observations.");
    }
}
