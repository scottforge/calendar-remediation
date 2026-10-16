using CalendarRemediation.Core.Execution;

namespace CalendarRemediation.Graph.Execution;

internal static class GraphMutationOutcome
{
    public static MutationOutcome Classify(int status) => status switch
    {
        >= 200 and < 300 => MutationOutcome.Accepted,
        412 => MutationOutcome.Conflict,
        >= 400 and < 500 when status is not (408 or 429) => MutationOutcome.Rejected,
        _ => MutationOutcome.Unknown
    };
}
