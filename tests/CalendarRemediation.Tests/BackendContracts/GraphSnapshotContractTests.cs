using System.Text.Json.Nodes;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Tests.AdapterContracts;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class GraphSnapshotContractTests
{
    [Theory]
    [InlineData("@odata.etag", null, "guard-unknown")]
    [InlineData("@odata.etag", "\"unquoted\"", "guard-unknown")]
    [InlineData("@odata.etag", "\"*\"", "guard-unknown")]
    [InlineData("attendees", null, "attendees-unknown")]
    [InlineData("attendees", "{}", "attendees-unknown")]
    [InlineData("attendees", "[{\"type\":\"resource\",\"emailAddress\":{\"address\":42}}]", "attendee-membership-unknown")]
    [InlineData("seriesMasterId", "42", "series-identity-unknown")]
    [InlineData("isAllDay", "\"false\"", "isAllDay-unknown")]
    public async Task IncompleteSelectedEvidenceIsRetainedAsUnresolvedRatherThanRejectedOrQualified(
        string field, string? json, string unresolved)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var item = Event();
        if (json is null)
        {
            item.Remove(field);
        }
        else
        {
            item[field] = JsonNode.Parse(json);
        }
        handler.Add(new { value = new[] { item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var reader = GraphReadContractTests.Reader(client);

        var result = await reader.DiscoverAsync(ContractFixture.Scope);

        // Complete enumeration does not make an unresolved event eligible for a change.
        Assert.True(result.Complete);
        Assert.Empty(result.Holds);
        var source = Assert.Single(result.Events);
        Assert.Equal("event", source.Id);
        Assert.Equal("v1", source.Version);
        Assert.Equal(ContractFixture.Source.Start, source.Start);
        Assert.Contains(unresolved, source.Unresolved);
        if (field == "attendees" && json?.StartsWith('[') == true)
        {
            Assert.Equal("", Assert.Single(source.Attendees).Address);
        }
        if (field == "isAllDay")
        {
            Assert.Null(source.IsAllDay);
        }
    }

    [Theory]
    [InlineData("2030-01-02T16:00:00.1234567", "UTC", true)]
    [InlineData("2030-01-02T16:00:00.1234567Z", "UTC", true)]
    [InlineData("2030-01-02T16:00:00.1234567+00:00", "UTC", true)]
    [InlineData("2030-01-02T16:00:00.1234567+02:00", "UTC", false)]
    [InlineData("2030-01-02T16:00:00.1234567Z", "Fixture/OldMinus07", false)]
    [InlineData("2030-01-02T16:00:00.1234567Z", null, false)]
    public async Task UtcParsingPreservesPrecisionAndRequiresConsistentZoneEvidence(
        string dateTime, string? zone, bool known)
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { id = ContractFixture.Identity.MailboxId, mail = "owner@example.test" });
        handler.Add(new { id = "calendar", isDefaultCalendar = true, owner = new { address = "owner@example.test" } });
        var item = Event();
        item["start"] = new JsonObject { ["dateTime"] = dateTime, ["timeZone"] = zone };
        handler.Add(item);
        using var client = new HttpClient(handler);
        var reader = GraphReadContractTests.Reader(client);

        var source = Assert.IsType<SourceSnapshot>(await reader.ReadAsync(ContractFixture.Identity, "event"));

        Assert.Equal(new ProviderTime(dateTime, zone), source.RawStart);
        Assert.Equal(ContractFixture.Source.End, source.End);
        if (known)
        {
            Assert.Equal(ContractFixture.At("2030-01-02T16:00:00.1234567Z"), source.Start);
            Assert.Equal(TimeSpan.Zero, source.Start!.Value.Offset);
            Assert.Empty(source.Unresolved);
        }
        else
        {
            Assert.Null(source.Start);
            Assert.Equal("start-unknown", Assert.Single(source.Unresolved));
        }
    }

    [Theory]
    [InlineData("originalStartTimeZone", "\"Fixture/DifferentZone\"", false)]
    [InlineData("originalStartTimeZone", "\"Fixture/DifferentZone\"", true)]
    [InlineData("start", "{\"dateTime\":\"2030-01-02T16:00:00Z\",\"timeZone\":\"UTC\"}", false)]
    [InlineData("start", "{\"dateTime\":\"2030-01-02T16:00:00Z\",\"timeZone\":\"UTC\"}", true)]
    [InlineData("attendees", "[{\"type\":\"resource\",\"emailAddress\":{\"address\":\"room@example.test\"}}]", false)]
    [InlineData("attendees", "[{\"type\":\"resource\",\"emailAddress\":{\"address\":\"room@example.test\"}}]", true)]
    public async Task SelectedEvidenceChangesConflictEvenWhenVersionGuardAndInstantsAgree(
        string field, string json, bool changedDetail)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var changed = Event();
        changed[field] = JsonNode.Parse(json);
        handler.Add(new { value = changedDetail ? new[] { Event() } : new[] { Event(), changed } });
        handler.Add(changedDetail ? changed : Event());
        using var client = new HttpClient(handler);
        var reader = GraphReadContractTests.Reader(client);

        var result = await reader.DiscoverAsync(ContractFixture.Scope);

        Assert.False(result.Complete);
        Assert.Contains("conflicting-observation", result.Holds);
        var source = Assert.Single(result.Events);
        Assert.Equal("v1", source.Version);
        Assert.Equal("\"v1\"", source.Guard);
        Assert.Equal(ContractFixture.Source.Start, source.Start);
        Assert.Equal(ContractFixture.Source.End, source.End);
        Assert.Equal(1, result.DetailReads);
        Assert.Single(handler.Addresses, address => address.Contains("/events/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnselectedContentDifferencesDoNotCreateEvidenceConflicts()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(ContractFixture.Identity.MailboxId);
        var detail = Event();
        detail["subject"] = "a different subject";
        detail["body"] = new JsonObject { ["content"] = "unselected content" };
        handler.Add(new { value = new[] { Event(), detail } });
        handler.Add(detail);
        using var client = new HttpClient(handler);
        var reader = GraphReadContractTests.Reader(client);

        var result = await reader.DiscoverAsync(ContractFixture.Scope);

        Assert.True(result.Complete);
        Assert.Empty(result.Holds);
        Assert.Empty(Assert.Single(result.Events).Unresolved);
        Assert.Equal(1, result.DetailReads);
    }

    private static JsonObject Event()
    {
        var item = GraphReadContractTests.Event();
        item["attendees"] = new JsonArray();
        return item;
    }
}
