using System.Text.Json;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class GraphEventParserTests
{
    private static readonly string[] ExpectedMalformedMappingUnresolved =
    [
        "attendee-membership-unknown", "end-unknown", "organizer-unknown", "series-identity-unknown", "start-unknown"
    ];

    [Theory]
    [InlineData("2030-01-02T16:00:00", 0)]
    [InlineData("2030-01-02T16:00:00Z", 0)]
    [InlineData("2030-01-02T16:00:00+00:00", 0)]
    [InlineData("2030-01-02T16:00:00.1234567", 1234567)]
    [InlineData("2030-01-02T16:00:00.1234567Z", 1234567)]
    [InlineData("2030-01-02T16:00:00.1234567+00:00", 1234567)]
    public void ExplicitUtcFullTimestampsPreserveRawEvidenceAndTickPrecision(string text, long fractionalTicks)
    {
        var item = JsonSerializer.SerializeToElement(new { start = new { dateTime = text, timeZone = "UTC" } });

        var result = GraphEventParser.ParseTime(item, "start");

        Assert.Equal(new ProviderTime(text, "UTC"), result.Raw);
        Assert.Equal(new DateTimeOffset(2030, 1, 2, 16, 0, 0, TimeSpan.Zero).AddTicks(fractionalTicks), result.Instant);
        Assert.Equal(TimeSpan.Zero, result.Instant!.Value.Offset);
        Assert.Empty(result.Unresolved);
    }

    [Theory]
    [InlineData("2030-01-02T16:00:00Z", "Europe/London")]
    [InlineData("2030-01-02T16:00:00Z", null)]
    [InlineData("2030-01-02T16:00:00+02:00", "UTC")]
    [InlineData("2030-01-02T16:00:00-07:00", "UTC")]
    [InlineData("2030-01-02", "UTC")]
    [InlineData("16:00:00", "UTC")]
    [InlineData("2030-01-02T16:00", "UTC")]
    [InlineData("2030-02-30T16:00:00", "UTC")]
    [InlineData("not-a-time", "UTC")]
    [InlineData("", "UTC")]
    [InlineData(null, "UTC")]
    public void IncompleteOrContradictoryTimeEvidenceStaysRawAndUnresolved(string? text, string? zone)
    {
        var item = JsonSerializer.SerializeToElement(new { end = new { dateTime = text, timeZone = zone } });

        var result = GraphEventParser.ParseTime(item, "end");

        Assert.Equal(new ProviderTime(text, zone), result.Raw);
        Assert.Null(result.Instant);
        Assert.Equal("end-unknown", Assert.Single(result.Unresolved));
    }

    [Theory]
    [InlineData("{}", null, null)]
    [InlineData("null", null, null)]
    [InlineData("{\"start\":null}", null, null)]
    [InlineData("{\"start\":\"2030-01-02T16:00:00Z\"}", null, null)]
    [InlineData("{\"start\":[]}", null, null)]
    [InlineData("{\"start\":{\"dateTime\":42,\"timeZone\":\"UTC\"}}", null, "UTC")]
    [InlineData("{\"start\":{\"dateTime\":\"2030-01-02T16:00:00Z\",\"timeZone\":42}}", "2030-01-02T16:00:00Z", null)]
    public void MissingOrMalformedTimeChildrenDoNotThrow(string json, string? text, string? zone)
    {
        using var document = JsonDocument.Parse(json);

        var result = GraphEventParser.ParseTime(document.RootElement, "start");

        Assert.Equal(new ProviderTime(text, zone), result.Raw);
        Assert.Null(result.Instant);
        Assert.Equal("start-unknown", Assert.Single(result.Unresolved));
    }

    [Fact]
    public void ExplicitlyEmptyAttendeesAreKnownEmpty()
    {
        using var document = JsonDocument.Parse("{\"attendees\":[]}");

        var result = GraphEventParser.ParseAttendees(document.RootElement);

        Assert.Empty(result.Values);
        Assert.Empty(result.Unresolved);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"attendees\":null}")]
    [InlineData("{\"attendees\":{}}")]
    [InlineData("{\"attendees\":\"missing\"}")]
    public void AbsentOrMalformedAttendeesAreNotTreatedAsKnownEmpty(string json)
    {
        using var document = JsonDocument.Parse(json);

        var result = GraphEventParser.ParseAttendees(document.RootElement);

        Assert.Empty(result.Values);
        Assert.Equal("attendees-unknown", Assert.Single(result.Unresolved));
    }

    [Theory]
    [InlineData("required", AttendeeKind.Unknown, true)]
    [InlineData("optional", AttendeeKind.Unknown, true)]
    [InlineData("unrecognized", AttendeeKind.Unknown, true)]
    [InlineData("resource", AttendeeKind.Resource, false)]
    public void AttendeeRoleOnlyEstablishesResourceClassification(string type, AttendeeKind expectedKind, bool unresolved)
    {
        var item = JsonSerializer.SerializeToElement(new
        {
            attendees = new[] { new { type, emailAddress = new { address = "room@example.test" } } }
        });

        var result = GraphEventParser.ParseAttendees(item);

        var attendee = Assert.Single(result.Values);
        Assert.Equal("room@example.test", attendee.Address);
        Assert.Equal(expectedKind, attendee.Kind);
        if (unresolved)
        {
            Assert.Equal("attendee-membership-unknown", Assert.Single(result.Unresolved));
        }
        else
        {
            Assert.Empty(result.Unresolved);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"emailAddress\":null}")]
    [InlineData("{\"emailAddress\":\"room@example.test\"}")]
    [InlineData("{\"emailAddress\":{\"address\":42}}")]
    [InlineData("{\"emailAddress\":{\"address\":\"\"}}")]
    [InlineData("{\"emailAddress\":{\"address\":\"   \"}}")]
    public void ResourceWithoutUsableAddressRetainsItsKindButIsUnresolved(string childJson)
    {
        using var child = JsonDocument.Parse(childJson);
        var fields = child.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        fields["type"] = JsonSerializer.SerializeToElement("resource");
        var item = JsonSerializer.SerializeToElement(new { attendees = new[] { fields } });

        var result = GraphEventParser.ParseAttendees(item);

        Assert.Equal(AttendeeKind.Resource, Assert.Single(result.Values).Kind);
        Assert.True(string.IsNullOrWhiteSpace(result.Values[0].Address));
        Assert.Equal("attendee-membership-unknown", Assert.Single(result.Unresolved));
    }

    [Theory]
    [InlineData("null", null)]
    [InlineData("42", null)]
    [InlineData("{}", null)]
    [InlineData("{\"emailAddress\":[]}", null)]
    [InlineData("{\"emailAddress\":{\"address\":false}}", null)]
    [InlineData("{\"emailAddress\":{\"address\":\"owner@example.test\"}}", "owner@example.test")]
    public void EmailExtractionToleratesMalformedChildren(string json, string? expected)
    {
        using var document = JsonDocument.Parse(json);

        Assert.Equal(expected, GraphEventParser.ReadEmailAddress(document.RootElement));
    }

    [Theory]
    [InlineData("occurrence", "\"master\"", EventKind.Occurrence, "master", null)]
    [InlineData("exception", "\"master\"", EventKind.Exception, "master", null)]
    [InlineData("occurrence", null, EventKind.Occurrence, null, "series-identity-unknown")]
    [InlineData("exception", "null", EventKind.Exception, null, "series-identity-unknown")]
    [InlineData("occurrence", "\"\"", EventKind.Occurrence, "", "series-identity-unknown")]
    [InlineData("exception", "\"   \"", EventKind.Exception, "   ", "series-identity-unknown")]
    [InlineData("singleInstance", null, EventKind.SingleInstance, null, null)]
    [InlineData("seriesMaster", "null", EventKind.SeriesMaster, null, null)]
    [InlineData("singleInstance", "\"master\"", EventKind.SingleInstance, "master", "series-identity-unknown")]
    [InlineData("seriesMaster", "\"master\"", EventKind.SeriesMaster, "master", "series-identity-unknown")]
    [InlineData("singleInstance", "42", EventKind.SingleInstance, null, "series-identity-unknown")]
    [InlineData("seriesMaster", "{}", EventKind.SeriesMaster, null, "series-identity-unknown")]
    [InlineData("futureKind", null, EventKind.Unknown, null, "event-kind-unknown")]
    [InlineData(null, null, EventKind.Unknown, null, "event-kind-unknown")]
    public void SeriesIdentityRequiresMasterEvidenceConsistentWithEventKind(
        string? type, string? masterJson, EventKind expectedKind, string? expectedMaster, string? unresolved)
    {
        var fields = new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement(type) };
        if (masterJson is not null)
        {
            using var master = JsonDocument.Parse(masterJson);
            fields["seriesMasterId"] = master.RootElement.Clone();
        }

        var result = GraphEventParser.ParseSeriesIdentity(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedMaster, result.MasterId);
        if (unresolved is null)
        {
            Assert.Empty(result.Unresolved);
        }
        else
        {
            Assert.Equal(unresolved, Assert.Single(result.Unresolved));
        }
    }

    [Fact]
    public void MappingMalformedChildrenRetainsEventAndProducesStableUniqueOrderedUnresolvedEvidence()
    {
        using var document = JsonDocument.Parse("""
            {
              "id": "event",
              "changeKey": "v1",
              "@odata.etag": "\"v1\"",
              "start": [],
              "end": { "dateTime": 42, "timeZone": "UTC" },
              "organizer": { "emailAddress": false },
              "attendees": [null, 42, { "type": "resource", "emailAddress": [] }],
              "type": "occurrence",
              "seriesMasterId": {},
              "originalStartTimeZone": "UTC",
              "originalEndTimeZone": "UTC",
              "isOrganizer": true,
              "isAllDay": false,
              "isCancelled": false
            }
            """);

        var first = Assert.IsType<SourceSnapshot>(GraphEventMapper.Map(ContractFixture.Identity, document.RootElement));
        var second = Assert.IsType<SourceSnapshot>(GraphEventMapper.Map(ContractFixture.Identity, document.RootElement));

        Assert.Equal("event", first.Id);
        Assert.Null(first.Start);
        Assert.Null(first.End);
        Assert.Null(first.Organizer);
        Assert.Equal(3, first.Attendees.Length);
        Assert.All(first.Attendees, attendee => Assert.Equal("", attendee.Address));
        Assert.Equal(AttendeeKind.Resource, first.Attendees[2].Kind);
        Assert.Equal(ExpectedMalformedMappingUnresolved, first.Unresolved);
        Assert.Equal(first.Unresolved.ToArray(), second.Unresolved.ToArray());
    }
}
