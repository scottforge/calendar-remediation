using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Core.Planning;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Execution;

public sealed class GraphCalendarWriter(GraphTransport transport) : ICalendarWriter
{
    public async Task<MutationResult> WriteAsync(GuardedWrite write, CancellationToken cancellationToken = default)
    {
        if (!WriteEligibility.IsAdmitted(write.Source, write.Admission) || write.AttemptId == Guid.Empty
            || !EntityTagHeaderValue.TryParse(write.Source.Guard, out var tag) || tag.Tag == "*"
            || write.Patch.End <= write.Patch.Start || string.IsNullOrWhiteSpace(write.Patch.Zone))
        {
            return new(MutationOutcome.Rejected, null);
        }
        if (cancellationToken.IsCancellationRequested) { return new(MutationOutcome.Rejected, null); }
        var json = JsonSerializer.Serialize(new
        {
            start = new { dateTime = write.Patch.Start.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end = new { dateTime = write.Patch.End.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), timeZone = "UTC" }
        });
        try
        {
            var response = await transport.SendAsync(HttpMethod.Patch, GraphApi.EventPatch(write.Source.Id),
                json, write.Source.Guard, write.AttemptId, cancellationToken).ConfigureAwait(false);
            var outcome = response.Status switch
            {
                >= 200 and < 300 => MutationOutcome.Accepted,
                412 => MutationOutcome.Conflict,
                >= 400 and < 500 when response.Status is not (408 or 429) => MutationOutcome.Rejected,
                _ => MutationOutcome.Unknown
            };
            return new(outcome, response.Exchange.RequestId);
        }
        catch (HttpRequestException) { return new(MutationOutcome.Unknown, null); }
        catch (OperationCanceledException) { return new(MutationOutcome.Unknown, null); }
        catch (IOException) { return new(MutationOutcome.Unknown, null); }
    }
}
