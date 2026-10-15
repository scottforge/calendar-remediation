using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Feasibility;

public sealed record PreparedProbe(string TenantId, string MailboxId, string EventId, string Marker,
    DateTimeOffset PreparedAt, DateTimeOffset ExpiresAt, string SourceETag, JsonElement Source, GraphExchange Read,
    string ExperimentOwner, DateTimeOffset ExperimentStopDate);
public sealed record ProbeIntent(PreparedProbe Prepared, JsonElement Competitor, string Patch, DateTimeOffset At,
    Guid ClientRequestId, string Method, string Endpoint, string Prefer);
public sealed record ConditionalProbeResult(string Outcome, bool StaleRejected, bool StaleAccepted, bool CompetitorPreserved,
    string Attribution, JsonElement? Before, JsonElement? After, GraphExchange[] Exchanges);

// Deliberately separate from calendar discovery and never registered in the web application.
public sealed class ConditionalWriteProbe(GraphTransport transport)
{
    public async Task<PreparedProbe> PrepareAsync(string tenantId, string mailboxId, string eventId, string marker,
        string experimentOwner, DateTimeOffset experimentStopDate,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(mailboxId, out _) || !Guid.TryParse(marker, out _))
        {
            throw new ArgumentException("Tenant, mailbox and disposable marker must be GUIDs.");
        }
        if (string.IsNullOrWhiteSpace(experimentOwner) || experimentStopDate <= DateTimeOffset.UtcNow
            || experimentStopDate > DateTimeOffset.UtcNow.AddDays(14))
        {
            throw new ArgumentException("A named experiment owner and a future stop date within 14 days are required.");
        }
        var response = await ReadAsync(eventId, cancellationToken).ConfigureAwait(false);
        var source = response.Body!.Value;
        CheckDisposable(source, eventId, marker);
        var etag = CalendarReadProbe.Text(source, "@odata.etag");
        if (!EntityTagHeaderValue.TryParse(etag, out var tag) || tag.Tag == "*")
        {
            throw new InvalidDataException("No source ETag. A change key is not substituted for a guard.");
        }
        return new(tenantId, mailboxId, eventId, marker, DateTimeOffset.UtcNow,
            experimentStopDate < DateTimeOffset.UtcNow.AddMinutes(30) ? experimentStopDate : DateTimeOffset.UtcNow.AddMinutes(30),
            etag, source, response.Exchange, experimentOwner, experimentStopDate);
    }

    public async Task<ConditionalProbeResult> AttemptAsync(PreparedProbe prepared,
        DateTimeOffset proposedStart, DateTimeOffset proposedEnd, Func<ProbeIntent, Task> persistIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(persistIntent);
        if (prepared.PreparedAt == default || prepared.PreparedAt > DateTimeOffset.UtcNow || prepared.ExpiresAt <= DateTimeOffset.UtcNow
            || prepared.ExpiresAt - prepared.PreparedAt > TimeSpan.FromMinutes(31)
            || string.IsNullOrWhiteSpace(prepared.ExperimentOwner) || prepared.ExperimentStopDate <= DateTimeOffset.UtcNow
            || prepared.ExpiresAt > prepared.ExperimentStopDate || prepared.ExperimentStopDate - prepared.PreparedAt > TimeSpan.FromDays(14)
            || proposedStart <= DateTimeOffset.UtcNow || proposedEnd <= proposedStart)
        {
            throw new InvalidOperationException("Probe expired or proposed future interval is invalid.");
        }
        CheckDisposable(prepared.Source, prepared.EventId, prepared.Marker);
        if (prepared.SourceETag != CalendarReadProbe.Text(prepared.Source, "@odata.etag")
            || !EntityTagHeaderValue.TryParse(prepared.SourceETag, out var tag) || tag.Tag == "*")
        {
            throw new InvalidDataException("Prepared guard does not match source observation.");
        }
        var beforeResponse = await ReadAsync(prepared.EventId, cancellationToken).ConfigureAwait(false);
        var before = beforeResponse.Body!.Value;
        CheckDisposable(before, prepared.EventId, prepared.Marker);
        if (CalendarReadProbe.Text(before, "@odata.etag") is not { Length: > 0 } currentTag
            || currentTag == prepared.SourceETag || CalendarReadProbe.Text(before, "changeKey") == CalendarReadProbe.Text(prepared.Source, "changeKey")
            || SameTime(before, prepared.Source))
        {
            throw new InvalidOperationException("Introduce a competing start/end edit in Outlook before the stale-write test.");
        }
        var patch = JsonSerializer.Serialize(new
        {
            start = new { dateTime = proposedStart.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end = new { dateTime = proposedEnd.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" }
        });
        using var patchDocument = JsonDocument.Parse(patch);
        if (SameTime(before, patchDocument.RootElement))
        {
            throw new InvalidOperationException("Proposed patch must differ from the competing edit.");
        }
        var dispatchId = Guid.NewGuid();
        await persistIntent(new(prepared, before, patch, DateTimeOffset.UtcNow, dispatchId,
            "PATCH", GraphApi.EventPatch(prepared.EventId), GraphTransport.Preferences)).ConfigureAwait(false);
        if (prepared.ExpiresAt <= DateTimeOffset.UtcNow || prepared.ExperimentStopDate <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("Experiment expired before dispatch. The durable attempt remains consumed.");
        }
        var exchanges = new List<GraphExchange> { beforeResponse.Exchange };
        var accepted = false;
        try
        {
            var response = await transport.SendAsync(HttpMethod.Patch, GraphApi.EventPatch(prepared.EventId), patch,
                prepared.SourceETag, dispatchId, cancellationToken).ConfigureAwait(false);
            exchanges.Add(response.Exchange);
            accepted = response.Status is >= 200 and < 300;
            var afterResponse = await ReadAsync(prepared.EventId, cancellationToken).ConfigureAwait(false);
            exchanges.Add(afterResponse.Exchange);
            var after = afterResponse.Body!.Value;
            var preserved = CalendarReadProbe.Equivalent(before, after)
                && CalendarReadProbe.Text(before, "subject") == CalendarReadProbe.Text(after, "subject");
            var rejected = response.Status == 412;
            return new(rejected && preserved ? "Observed stale rejection on this disposable appointment only"
                : accepted ? "FAILED: stale guard was accepted"
                : "Inconclusive: rejection or preservation not established", rejected, accepted, preserved,
                "Response and organizer readback only. Delivery, actor attribution after loss, and general recoverability are unproven.", before, after, exchanges.ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidDataException or JsonException
            or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new(accepted ? "FAILED: stale guard was accepted. Subsequent readback is unknown. Never replay."
                : "UNKNOWN: dispatch or readback uncertain. Hold and inspect manually. Never replay.", false, accepted, false,
                "Unproven. Desired fields or an immediate unchanged read cannot establish attribution.", before, null, exchanges.ToArray());
        }
    }

    private static bool SameTime(JsonElement a, JsonElement b) => Time(a, "start") == Time(b, "start")
        && Time(a, "end") == Time(b, "end");

    private static DateTimeOffset Time(JsonElement item, string field)
    {
        var value = item.GetProperty(field);
        if (CalendarReadProbe.Text(value, "timeZone") != "UTC"
            || !DateTimeOffset.TryParse(CalendarReadProbe.Text(value, "dateTime"), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
        {
            throw new InvalidDataException("Disposable probe requires valid UTC scheduling observations.");
        }
        return time;
    }

    private async Task<GraphResponse> ReadAsync(string id, CancellationToken ct)
    {
        var response = await transport.SendAsync(HttpMethod.Get, GraphApi.ConditionalProbeEventDetail(id), cancellationToken: ct).ConfigureAwait(false);
        if (response.Status != 200 || response.Body is null)
        {
            throw new InvalidDataException($"Disposable event read failed with HTTP {response.Status}.");
        }
        return response;
    }

    private static void CheckDisposable(JsonElement item, string id, string marker)
    {
        if (!Guid.TryParse(marker, out _) || CalendarReadProbe.Text(item, "id") != id
            || CalendarReadProbe.Text(item, "subject") != "PHASE0-DISPOSABLE:" + marker
            || !item.GetProperty("isOrganizer").GetBoolean() || item.GetProperty("isAllDay").GetBoolean()
            || item.GetProperty("isCancelled").GetBoolean() || item.GetProperty("attendees").GetArrayLength() != 0
            || CalendarReadProbe.Text(item, "type") != "singleInstance"
            || string.IsNullOrWhiteSpace(CalendarReadProbe.Text(item, "changeKey"))
            || Time(item, "start") <= DateTimeOffset.UtcNow || Time(item, "end") <= Time(item, "start"))
        {
            throw new InvalidOperationException("Only a marked, organizer-owned attendee-free timed single-instance disposable appointment is allowed.");
        }
    }
}
