using System.Text.Json;
using CalendarRemediation.Core.Execution;
using CalendarRemediation.Graph.Discovery;
using CalendarRemediation.Graph.Execution;
using CalendarRemediation.Tests.AdapterContracts;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class GraphWriteContractTests
{
    [Fact]
    public async Task PatchContainsOnlyStartEndAndTheExactSuppliedProvenGuard()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { }, 204, request =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/v1.0/me/events/event", request.RequestUri!.AbsolutePath);
            Assert.Equal("\"v1\"", request.Headers.IfMatch.ToString());
            var json = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            Assert.Equal<string>(["end", "start"], document.RootElement.EnumerateObject().Select(p => p.Name).Order());
            foreach (var field in (string[])["start", "end"])
            {
                var time = document.RootElement.GetProperty(field);
                Assert.Equal<string>(["dateTime", "timeZone"], time.EnumerateObject().Select(p => p.Name).Order());
                // Exact UTC payload avoids claiming that an admitted zone label proves deployed rules.
                Assert.Equal("UTC", time.GetProperty("timeZone").GetString());
                var expected = field == "start" ? ContractFixture.Patch.Start : ContractFixture.Patch.End;
                Assert.Equal(expected.UtcDateTime, DateTime.Parse(time.GetProperty("dateTime").GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind));
            }
        });
        using var client = new HttpClient(handler);
        var result = await Writer(client).WriteAsync(Write());

        Assert.Equal(MutationOutcome.Accepted, result.Outcome);
        Assert.Equal<string>(["PATCH"], handler.Methods);
        // Independent readback belongs to ICalendarReader and the executor, not hidden write replay.
    }

    [Theory]
    [InlineData("missing-admission")]
    [InlineData("unproven-guard")]
    [InlineData("wildcard")]
    [InlineData("recurrence")]
    public async Task UnadmittedOrUnguardedWritesAreRejectedWithoutHttp(string invalid)
    {
        var write = invalid switch
        {
            "missing-admission" => Write() with { Admission = null },
            "unproven-guard" => Write() with { Admission = ContractFixture.Admission with { AtomicGuardProven = false } },
            "wildcard" => Write() with { Source = ContractFixture.Source with { Guard = "*" } },
            _ => Write() with { Source = ContractFixture.Source with { Kind = CalendarRemediation.Core.Discovery.EventKind.Occurrence, SeriesMasterId = "master" } }
        };
        using var handler = new ScriptedGraphHandler();
        using var client = new HttpClient(handler);
        var result = await Writer(client).WriteAsync(write);

        Assert.Equal(MutationOutcome.Rejected, result.Outcome);
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData(412, MutationOutcome.Conflict)]
    [InlineData(403, MutationOutcome.Rejected)]
    [InlineData(503, MutationOutcome.Unknown)]
    public async Task MapsDefinitiveRejectionAndUncertainServerFailureWithoutReplay(int status, MutationOutcome expected)
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { error = "not persisted" }, status);
        using var client = new HttpClient(handler);
        var result = await Writer(client).WriteAsync(Write());

        Assert.Equal(expected, result.Outcome);
        Assert.Equal<string>(["PATCH"], handler.Methods);
    }

    [Fact]
    public async Task ConnectionLossAfterDispatchIsUnknownWithoutHttpRetry()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddFailure(new HttpRequestException("response lost"));
        using var client = new HttpClient(handler);
        var result = await Writer(client).WriteAsync(Write());

        Assert.Equal(MutationOutcome.Unknown, result.Outcome);
        Assert.Equal<string>(["PATCH"], handler.Methods);
    }

    private static GuardedWrite Write() => new(ContractFixture.Source, ContractFixture.Patch, ContractFixture.Admission, Guid.NewGuid());
    private static GraphCalendarWriter Writer(HttpClient client)
        => new(new GraphTransport(client, _ => Task.FromResult("credential-must-not-persist")));
}
