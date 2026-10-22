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
public sealed class ConditionalWriteProbe(GraphTransport transport, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    public async Task<PreparedProbe> PrepareAsync(string tenantId, string mailboxId, string eventId, string marker,
        string experimentOwner, DateTimeOffset experimentStopDate,
        CancellationToken cancellationToken = default)
    {
        ConditionalProbePolicy.ValidatePrepareInput(tenantId, mailboxId, marker, experimentOwner, experimentStopDate, clock.GetUtcNow());
        var response = await ReadAsync(eventId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        ConditionalProbePolicy.ValidatePrepareInput(tenantId, mailboxId, marker, experimentOwner, experimentStopDate, now);
        var source = response.Body!.Value;
        ConditionalProbePolicy.ValidateDisposable(source, eventId, marker, now);
        var etag = GraphJson.Text(source, "@odata.etag");
        ConditionalProbePolicy.ValidateGuard(etag);
        return new(tenantId, mailboxId, eventId, marker, now,
            experimentStopDate - now < TimeSpan.FromMinutes(30) ? experimentStopDate : now.AddMinutes(30),
            etag!, source, response.Exchange, experimentOwner, experimentStopDate);
    }

    public async Task<ConditionalProbeResult> AttemptAsync(PreparedProbe prepared,
        DateTimeOffset proposedStart, DateTimeOffset proposedEnd, Func<ProbeIntent, Task> persistIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(persistIntent);
        ConditionalProbePolicy.ValidatePrepared(prepared, proposedStart, proposedEnd, clock.GetUtcNow());
        var beforeResponse = await ReadAsync(prepared.EventId, cancellationToken).ConfigureAwait(false);
        var before = beforeResponse.Body!.Value;
        ConditionalProbePolicy.ValidateDisposable(before, prepared.EventId, prepared.Marker, clock.GetUtcNow());
        ConditionalProbePolicy.ValidateCompetingEdit(prepared, before);
        var patch = GraphEventPatch.Serialize(proposedStart, proposedEnd);
        using var patchDocument = JsonDocument.Parse(patch);
        ConditionalProbePolicy.ValidatePatch(before, patchDocument.RootElement);
        var dispatchId = Guid.NewGuid();
        await persistIntent(new(prepared, before, patch, clock.GetUtcNow(), dispatchId,
            "PATCH", GraphApi.EventPatch(prepared.EventId), GraphTransport.Preferences)).ConfigureAwait(false);
        ConditionalProbePolicy.ValidateDispatch(prepared, clock.GetUtcNow());
        var exchanges = new List<GraphExchange> { beforeResponse.Exchange };
        int? status = null;
        try
        {
            var response = await transport.SendAsync(HttpMethod.Patch, GraphApi.EventPatch(prepared.EventId), patch,
                prepared.SourceETag, dispatchId, cancellationToken).ConfigureAwait(false);
            exchanges.Add(response.Exchange);
            status = response.Status;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            return ConditionalProbeOutcome.CreateResult(status, null, before, null, exchanges.ToArray());
        }

        try
        {
            var afterResponse = await transport.SendAsync(HttpMethod.Get, GraphApi.ConditionalProbeEventDetail(prepared.EventId),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            exchanges.Add(afterResponse.Exchange);
            if (afterResponse.Status != 200 || afterResponse.Body is not { } after || !ReadProbeValidation.IsValid(after)
                || GraphJson.Text(after, "subject") is null)
            {
                return ConditionalProbeOutcome.CreateResult(status, null, before, null, exchanges.ToArray());
            }
            var preserved = CalendarReadProbe.Equivalent(before, after)
                && GraphJson.Text(before, "subject") == GraphJson.Text(after, "subject");
            return ConditionalProbeOutcome.CreateResult(status, preserved, before, after, exchanges.ToArray());
        }
        // After dispatch, any readback fault must preserve the PATCH status and evidence without replay.
        catch (Exception)
        {
            return ConditionalProbeOutcome.CreateResult(status, null, before, null, exchanges.ToArray());
        }
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
}
