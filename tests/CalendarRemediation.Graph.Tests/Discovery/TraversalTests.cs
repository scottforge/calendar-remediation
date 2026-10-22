using System.Text.Json;
using System.Text.Json.Nodes;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;
using CalendarRemediation.Testing;
using GraphReadContractTests = CalendarRemediation.Graph.Tests.Discovery.ReadContractTests;

namespace CalendarRemediation.Graph.Tests.Discovery;

public sealed class TraversalTests
{
    private static readonly string[] TwoEventIds = ["event", "event-2"];
    private static readonly string[] ThreeEventIds = ["event", "event-2", "event-3"];

    [Fact]
    public async Task RepeatedContinuationStopsBeforeAnotherHttpRequestAndPreservesObservedEvents()
    {
        const string next = GraphTransport.Root + "me/calendarView?$skiptoken=cycle%2Btoken";
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = new[] { GraphReadContractTests.Event() },
            ["@odata.nextLink"] = next
        });
        handler.Add(GraphReadContractTests.Event());
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = new[] { GraphReadContractTests.Event("event-2") },
            ["@odata.nextLink"] = next
        });
        handler.Add(GraphReadContractTests.Event("event-2"));
        using var client = new HttpClient(handler);

        var result = await GraphReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.Equal("paging-cycle", Assert.Single(result.Holds));
        Assert.Equal(TwoEventIds, result.Events.Select(source => source.Id));
        Assert.Equal(2, result.Pages);
        Assert.Equal(2, result.DetailReads);
        Assert.Equal(8, handler.Addresses.Count);
        Assert.Single(handler.Addresses, address => address == next);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("42")]
    [InlineData("{}")]
    [InlineData("\"/v1.0/me/calendarView?$skiptoken=relative\"")]
    [InlineData("\"https://other.example.test/v1.0/me/calendarView?$skiptoken=escape\"")]
    [InlineData("\"https://graph.microsoft.com/v1.0/users/other/calendarView?$skiptoken=escape\"")]
    [InlineData("\"https://graph.microsoft.com/v1.0/me/events?$skiptoken=escape\"")]
    public async Task MalformedOrEscapingContinuationIsNeverFetched(string? nextLinkJson)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var page = new JsonObject
        {
            ["value"] = new JsonArray(GraphReadContractTests.Event()),
            ["@odata.nextLink"] = nextLinkJson is null ? null : JsonNode.Parse(nextLinkJson)
        };
        handler.Add(page);
        handler.Add(GraphReadContractTests.Event());
        using var client = new HttpClient(handler);

        var result = await GraphReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.True(Assert.Single(result.Holds) is "invalid-next-link" or "read-unavailable");
        Assert.Equal("event", Assert.Single(result.Events).Id);
        Assert.Equal(1, result.Pages);
        Assert.Equal(1, result.DetailReads);
        Assert.Equal(6, handler.Addresses.Count);
        Assert.Equal(GraphApi.CalendarEventDetail("event"), handler.Addresses[^1]);
    }

    [Fact]
    public async Task DuplicateOnLaterPageConsumesEventBudgetWithoutAnotherDetailRead()
    {
        const string next = GraphTransport.Root + "me/calendarView?$skiptoken=duplicates";
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = new[] { GraphReadContractTests.Event() },
            ["@odata.nextLink"] = next
        });
        handler.Add(GraphReadContractTests.Event());
        handler.Add(new { value = new[] { GraphReadContractTests.Event(), GraphReadContractTests.Event("event-2") } });
        using var client = new HttpClient(handler);
        var scope = ContractFixture.Scope with { Budget = ContractFixture.Scope.Budget with { Events = 2 } };

        var result = await GraphReadContractTests.Reader(client).DiscoverAsync(scope);

        Assert.False(result.Complete);
        Assert.Equal("event-budget", Assert.Single(result.Holds));
        Assert.Equal("event", Assert.Single(result.Events).Id);
        Assert.Equal(2, result.Pages);
        Assert.Equal(1, result.DetailReads);
        Assert.Equal(7, handler.Addresses.Count);
        Assert.Equal(GraphApi.CalendarEventDetail("event"),
            Assert.Single(handler.Addresses, address => address.Contains("/events/", StringComparison.Ordinal)));
        Assert.Equal(next, handler.Addresses[^1]);
    }

    [Theory]
    [InlineData("provider-status")]
    [InlineData("malformed-page")]
    [InlineData("non-object-page")]
    [InlineData("wrong-value-type")]
    public async Task LaterPageFailureRetainsEarlierEvidenceWithReadUnavailableHold(string failure)
    {
        const string next = GraphTransport.Root + "me/calendarView?$skiptoken=provider-failure";
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = new[] { GraphReadContractTests.Event() },
            ["@odata.nextLink"] = next
        });
        handler.Add(GraphReadContractTests.Event());
        if (failure == "provider-status")
        {
            handler.Add(new { error = "private-provider-content" }, 503);
        }
        else
        {
            handler.Add(JsonSerializer.Deserialize<JsonElement>(failure switch
            {
                "non-object-page" => "[]",
                "wrong-value-type" => "{\"value\":{}}",
                _ => "{\"unexpected\":\"private-provider-content\"}"
            }));
        }
        using var client = new HttpClient(handler);

        var result = await GraphReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.Equal("read-unavailable", Assert.Single(result.Holds));
        Assert.Equal("event", Assert.Single(result.Events).Id);
        Assert.Equal(failure is "provider-status" or "non-object-page" ? 1 : 2, result.Pages);
        Assert.Equal(1, result.DetailReads);
        Assert.Equal(next, handler.Addresses[^1]);
    }

    [Fact]
    public async Task InitialProviderFailureReturnsIncomplete()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { error = "private-provider-content" }, 503);
        using var client = new HttpClient(handler);

        var result = await GraphReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.Equal("read-unavailable", Assert.Single(result.Holds));
        Assert.Empty(result.Events);
    }

    [Theory]
    [InlineData("http-status")]
    [InlineData("io-exception")]
    [InlineData("http-exception")]
    [InlineData("invalid-data-exception")]
    public async Task FailedDetailRetainsViewEvidenceAndContinuesThroughRemainingEventsAndPages(string failure)
    {
        const string next = GraphTransport.Root + "me/calendarView?$skiptoken=after-failure";
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = new[] { GraphReadContractTests.Event(), GraphReadContractTests.Event("event-2") },
            ["@odata.nextLink"] = next
        });
        if (failure == "http-status")
        {
            handler.Add(new { error = "private-provider-content" }, 503);
        }
        else
        {
            handler.AddFailure(failure switch
            {
                "io-exception" => new IOException("private-provider-content"),
                "invalid-data-exception" => new InvalidDataException("private-provider-content"),
                _ => new HttpRequestException("private-provider-content")
            });
        }
        handler.Add(GraphReadContractTests.Event("event-2"));
        handler.Add(new { value = new[] { GraphReadContractTests.Event("event-3") } });
        handler.Add(GraphReadContractTests.Event("event-3"));
        using var client = new HttpClient(handler);

        var result = await GraphReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.Equal("detail read unavailable", Assert.Single(result.Holds));
        Assert.Equal(ThreeEventIds, result.Events.Select(source => source.Id));
        var partial = result.Events[0];
        Assert.Equal("v1", partial.Version);
        Assert.Equal("\"v1\"", partial.Guard);
        Assert.Equal(ContractFixture.Source.Start, partial.Start);
        Assert.Equal(ContractFixture.Source.End, partial.End);
        Assert.Equal("attendee@example.test", Assert.Single(partial.Attendees).Address);
        Assert.Contains("attendee-membership-unknown", partial.Unresolved);
        Assert.Contains("detail-read-unavailable", partial.Unresolved);
        Assert.Equal(2, partial.Unresolved.Length);
        Assert.All(result.Events.Skip(1), source =>
            Assert.Equal("attendee-membership-unknown", Assert.Single(source.Unresolved)));
        Assert.Equal(2, result.Pages);
        Assert.Equal(3, result.DetailReads);
        Assert.Equal(9, handler.Addresses.Count);
        Assert.Equal(ThreeEventIds.Select(GraphApi.CalendarEventDetail),
            handler.Addresses.Where(address => address.Contains("/events/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task CallerCancellationDuringDetailReadPropagatesAndStopsSubsequentWork()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new { value = new[] { GraphReadContractTests.Event(), GraphReadContractTests.Event("event-2") } });
        handler.Add(GraphReadContractTests.Event(), inspect: _ => cancellation.Cancel());
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GraphReadContractTests.Reader(client).DiscoverAsync(ContractFixture.Scope, cancellation.Token));

        Assert.Equal(6, handler.Addresses.Count);
        Assert.Equal(GraphApi.CalendarEventDetail("event"), handler.Addresses[^1]);
    }
}
