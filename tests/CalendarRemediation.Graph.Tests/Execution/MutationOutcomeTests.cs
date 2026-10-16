using CalendarRemediation.Core.Execution;
using CalendarRemediation.Graph.Execution;

namespace CalendarRemediation.Graph.Tests.Execution;

public sealed class MutationOutcomeTests
{
    [Theory]
    [InlineData(199, MutationOutcome.Unknown)]
    [InlineData(200, MutationOutcome.Accepted)]
    [InlineData(204, MutationOutcome.Accepted)]
    [InlineData(299, MutationOutcome.Accepted)]
    [InlineData(300, MutationOutcome.Unknown)]
    [InlineData(399, MutationOutcome.Unknown)]
    [InlineData(400, MutationOutcome.Rejected)]
    [InlineData(407, MutationOutcome.Rejected)]
    [InlineData(408, MutationOutcome.Unknown)]
    [InlineData(409, MutationOutcome.Rejected)]
    [InlineData(411, MutationOutcome.Rejected)]
    [InlineData(412, MutationOutcome.Conflict)]
    [InlineData(413, MutationOutcome.Rejected)]
    [InlineData(428, MutationOutcome.Rejected)]
    [InlineData(429, MutationOutcome.Unknown)]
    [InlineData(430, MutationOutcome.Rejected)]
    [InlineData(499, MutationOutcome.Rejected)]
    [InlineData(500, MutationOutcome.Unknown)]
    [InlineData(599, MutationOutcome.Unknown)]
    public void ClassifiesAcceptanceConflictDefinitiveRejectionAndUncertainBoundaries(int status, MutationOutcome expected)
    {
        Assert.Equal(expected, GraphMutationOutcome.Classify(status));
    }
}
