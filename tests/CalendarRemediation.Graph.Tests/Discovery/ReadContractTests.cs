using System.Text.Json.Nodes;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Graph.Discovery;
using CalendarRemediation.Testing;

namespace CalendarRemediation.Graph.Tests.Discovery;

public sealed class ReadContractTests
{
    [Fact]
    public async Task MapsIdentityTimesVersionsAttendeesAndZoneProvenanceWithoutSubjectContent()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var item = Event();
        handler.Add(new { value = new[] { item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.True(result.Complete);
        Assert.Equal(ContractFixture.Identity, result.Scope.Identity);
        var source = Assert.Single(result.Events);
        Assert.Equal("event", source.Id);
        Assert.Equal("v1", source.Version);
        Assert.Equal("\"v1\"", source.Guard);
        Assert.Equal(ContractFixture.Source.Start, source.Start);
        Assert.Equal(ContractFixture.Source.End, source.End);
        Assert.Equal(new ProviderTime("2030-01-02T16:00:00", "UTC"), source.RawStart);
        Assert.Equal(new ProviderTime("2030-01-02T17:00:00", "UTC"), source.RawEnd);
        Assert.True(source.IsOrganizer);
        Assert.Equal(EventKind.SingleInstance, source.Kind);
        Assert.Equal("attendee@example.test", Assert.Single(source.Attendees).Address);
        // An address or matching domain is not proof of tenant membership.
        Assert.Equal(AttendeeKind.Unknown, Assert.Single(source.Attendees).Kind);
        Assert.Equal(new ZoneProvenance("Fixture/OldMinus07", "Fixture/OldMinus07", "UTC"), source.Zones);
        Assert.Contains("UTC", result.SupportedZones);
        Assert.Contains("Etc/UTC", result.SupportedZones);
        var query = Uri.UnescapeDataString(Assert.Single(handler.Addresses, a => a.Contains("calendarView", StringComparison.Ordinal)));
        Assert.Contains("startDateTime=2030-01-01T00:00:00", query, StringComparison.Ordinal);
        Assert.Contains("endDateTime=2030-01-31T00:00:00", query, StringComparison.Ordinal);
        Assert.DoesNotContain("subject", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("body", query, StringComparison.OrdinalIgnoreCase);
        AssertDetailRequests(handler, "event");
    }

    [Fact]
    public async Task RejectsNonUtcWindowBeforeHttp()
    {
        using var handler = new ScriptedGraphHandler();
        using var client = new HttpClient(handler);
        var scope = ContractFixture.Scope with { Start = ContractFixture.Now.ToOffset(TimeSpan.FromHours(-7)) };

        await Assert.ThrowsAsync<ArgumentException>(() => Reader(client).DiscoverAsync(scope));
        Assert.Empty(handler.Methods);
    }

    [Fact]
    public async Task FollowsOpaquePagesAndReadsEveryUniqueEventDetail()
    {
        const string next = GraphTransport.Root + "me/calendarView?$skiptoken=opaque%2Btoken";
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new Dictionary<string, object> { ["value"] = new[] { Event() }, ["@odata.nextLink"] = next });
        handler.Add(Event());
        handler.Add(new { value = new[] { Event("event-2") } });
        handler.Add(Event("event-2"));
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.True(result.Complete);
        Assert.Equal(2, result.Events.Length);
        Assert.Equal(2, result.Pages);
        Assert.Equal(2, result.DetailReads);
        Assert.Contains(next, handler.Addresses);
        AssertDetailRequests(handler, "event", "event-2");
    }

    [Theory]
    [InlineData("isOrganizer")]
    [InlineData("start")]
    public async Task UnknownOrMalformedFieldsRemainExplicitlyUnresolved(string field)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var item = Event();
        item[field] = field == "start" ? JsonValue.Create("malformed-time") : null;
        handler.Add(new { value = new[] { item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        var source = Assert.Single(result.Events);
        Assert.NotEmpty(source.Unresolved);
        if (field == "isOrganizer")
        {
            Assert.Null(source.IsOrganizer);
        }
        else
        {
            Assert.Null(source.Start);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeduplicatesOnlyMatchingObservationsAndHoldsConflictingVersions(bool conflicting)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var repeated = Event();
        if (conflicting)
        {
            repeated["changeKey"] = "v2";
            repeated["@odata.etag"] = "\"v2\"";
        }
        handler.Add(new { value = new[] { Event(), repeated } });
        handler.Add(Event());
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.Equal(!conflicting, result.Complete);
        Assert.Single(result.Events);
        AssertDetailRequests(handler, "event");
        if (conflicting)
        {
            Assert.NotEmpty(result.Holds);
        }
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("details")]
    public async Task ExhaustedBudgetIsIncompleteAndPreservesObservedWork(string budget)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new Dictionary<string, object>
        {
            ["value"] = budget == "details" ? new[] { Event(), Event("event-2") } : new[] { Event() },
            ["@odata.nextLink"] = GraphTransport.Root + "me/calendarView?$skiptoken=more"
        });
        handler.Add(Event());
        using var client = new HttpClient(handler);
        var scope = ContractFixture.Scope with
        {
            Budget = budget == "pages"
                ? ContractFixture.Scope.Budget with { Pages = 1 }
                : ContractFixture.Scope.Budget with { Details = 1 }
        };
        var result = await Reader(client).DiscoverAsync(scope);

        Assert.False(result.Complete);
        Assert.Equal(budget == "pages" ? "page-budget" : "detail-budget", Assert.Single(result.Holds));
        Assert.Single(result.Events);
        Assert.Equal(1, result.Pages);
        Assert.Equal(1, result.DetailReads);
        Assert.Single(handler.Addresses, a => new Uri(a).AbsolutePath == "/v1.0/me/calendarView");
        AssertDetailRequests(handler, "event");
        Assert.Equal(6, handler.Methods.Count);
    }

    [Fact]
    public async Task MailboxIdentityMismatchFailsClosedBeforeCalendarEnumeration()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { id = "different-mailbox", mail = "owner@example.test" });
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.NotEmpty(result.Holds);
        Assert.Empty(result.Events);
        Assert.Single(handler.Methods);
    }

    [Fact]
    public async Task RequiredDetailReadFailureIsIncompleteWithoutRetry()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        handler.Add(new { value = new[] { Event() } });
        handler.Add(new { error = "do not persist provider error content" }, 503);
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.NotEmpty(result.Holds);
        AssertDetailRequests(handler, "event");
        Assert.DoesNotContain(result.Holds, h => h.Contains("do not persist", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SupportedZoneListDoesNotFabricateOriginalZoneProvenance()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var item = Event();
        item.Remove("originalStartTimeZone");
        handler.Add(new { value = new[] { item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var result = await Reader(client).DiscoverAsync(ContractFixture.Scope);

        var source = Assert.Single(result.Events);
        Assert.Null(source.Zones.OriginalStart);
        Assert.Equal("UTC", source.Zones.ResponseZone);
        Assert.NotEmpty(source.Unresolved);
    }

    private static void AssertDetailRequests(ScriptedGraphHandler handler, params string[] eventIds)
    {
        const string selectedFields = "id,changeKey,start,end,originalStartTimeZone,originalEndTimeZone,isOrganizer,organizer,attendees,isAllDay,isCancelled,type,seriesMasterId";
        var detailRequests = handler.Addresses.Skip(4)
            .Where(address => new Uri(address).AbsolutePath != "/v1.0/me/calendarView");
        Assert.Equal(eventIds.Select(id => "https://graph.microsoft.com/v1.0/me/calendar/events/" + id + "?$select=" + selectedFields), detailRequests);
    }

    internal static GraphCalendarReader Reader(HttpClient client)
        => new(new GraphTransport(client, _ => Task.FromResult("credential-must-not-persist")));

    internal static JsonObject Event(string id = "event")
    {
        var item = JsonNode.Parse(ScriptedGraphHandler.Event(id, attendees: true).GetRawText())!.AsObject();
        item["start"] = new JsonObject { ["dateTime"] = "2030-01-02T16:00:00", ["timeZone"] = "UTC" };
        item["end"] = new JsonObject { ["dateTime"] = "2030-01-02T17:00:00", ["timeZone"] = "UTC" };
        item["originalStartTimeZone"] = "Fixture/OldMinus07";
        item["originalEndTimeZone"] = "Fixture/OldMinus07";
        return item;
    }
}
