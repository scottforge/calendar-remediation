using System.Net.Http.Headers;
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
        var json = GraphEventPatch.Serialize(write.Patch.Start, write.Patch.End);
        try
        {
            var response = await transport.SendAsync(HttpMethod.Patch, GraphApi.EventPatch(write.Source.Id),
                json, write.Source.Guard, write.AttemptId, cancellationToken).ConfigureAwait(false);
            var outcome = GraphMutationOutcome.Classify(response.Status);
            return new(outcome, response.Exchange.RequestId);
        }
        catch (HttpRequestException) { return new(MutationOutcome.Unknown, null); }
        catch (OperationCanceledException) { return new(MutationOutcome.Unknown, null); }
        catch (IOException) { return new(MutationOutcome.Unknown, null); }
    }
}
