using System.Text.Json;
using CalendarRemediation.Graph;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Tests.Discovery;

public sealed class ReadProbeTests
{
    private const string Mailbox = "22222222-2222-2222-2222-222222222222";
    private static ReadProbeOptions Scope => new("11111111-1111-1111-1111-111111111111", Mailbox,
        new DateTimeOffset(2030, 1, 1, 9, 30, 0, TimeSpan.Zero), new DateTimeOffset(2030, 2, 1, 0, 0, 0, TimeSpan.Zero), 10, 3, 10, 10);

    [Fact]
    public async Task CompletePagingPreservesCrossingEventAndOpaqueContinuation()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var first = ScriptedGraphHandler.Event(attendees: true);
        var second = ScriptedGraphHandler.Event("event2");
        var continuation = GraphTransport.Root + "me/calendarView?$skiptoken=opaque%2Bvalue&$select=" + GraphApi.EventFields;
        handler.Add(new Dictionary<string, object> { ["value"] = new[] { first }, ["@odata.nextLink"] = continuation }, inspect: r =>
        {
            Assert.Contains("startDateTime=2030-01-01T09%3A30%3A00", r.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
            Assert.Contains("%2B00%3A00", r.RequestUri.AbsoluteUri, StringComparison.Ordinal);
        });
        handler.Add(first);
        handler.Add(new { value = new[] { second } }, inspect: r => Assert.Equal(continuation, r.RequestUri!.AbsoluteUri));
        handler.Add(second);
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake-token"))).RunAsync(Scope);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Pages);
        Assert.Equal(2, result.DetailReads);
        Assert.Equal(1, result.OrganizerMeetings);
        Assert.Equal(2, result.Events.Length);
        Assert.All(handler.Methods, m => Assert.Equal("GET", m));
    }

    [Theory]
    [InlineData("https://attacker.example/v1.0/me/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/users/other/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/me/events")]
    [InlineData("http://graph.microsoft.com/v1.0/me/calendarView")]
    public async Task ScopeEscapeContinuationIsIncompleteAndNeverFetched(string next)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        handler.Add(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next });
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.Equal(5, handler.Methods.Count);
    }

    [Fact]
    public async Task ExhaustedPageBudgetAndMissingDetailBlockCompleteness()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        handler.Add(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = GraphTransport.Root + "me/calendarView?$skiptoken=next" });
        using var client = new HttpClient(handler);
        Assert.False((await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope with { MaxPages = 1 })).Complete);
        Assert.Equal(5, handler.Methods.Count);

        using var missing = new ScriptedGraphHandler();
        missing.AddReadFoundation(Mailbox);
        missing.Add(new { value = new[] { ScriptedGraphHandler.Event() } });
        missing.Add(new { error = "not retained" }, 404);
        using var missingClient = new HttpClient(missing);
        Assert.False((await new CalendarReadProbe(new(missingClient, _ => Task.FromResult("fake"))).RunAsync(Scope)).Complete);
    }

    [Fact]
    public async Task ChangedVersionAndDuplicateEvidenceCannotBeSilentlyDeduplicated()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        handler.Add(new { value = new[] { ScriptedGraphHandler.Event(), ScriptedGraphHandler.Event(version: "v2") } });
        handler.Add(ScriptedGraphHandler.Event());
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.Contains(result.Holds, h => h.Contains("differing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedIdenticalIdentityCountsObservationsWithoutExtraDetailRead()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var item = ScriptedGraphHandler.Event();
        handler.Add(new { value = new[] { item, item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.True(result.Complete);
        Assert.Equal(2, result.Observations);
        Assert.Equal(1, result.DetailReads);
    }

    [Fact]
    public async Task ThrottleIsRecordedAndNotRetried()
    {
        using var handler = new ScriptedGraphHandler();
        handler.Add(new { error = "secret text" }, 429);
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.Equal(429, Assert.Single(result.Exchanges).Status);
        Assert.Single(handler.Methods);
    }

    [Fact]
    public async Task InvalidBoundsSendNothing()
    {
        using var handler = new ScriptedGraphHandler();
        using var client = new HttpClient(handler);
        var probe = new CalendarReadProbe(new(client, _ => Task.FromResult("fake")));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.RunAsync(Scope with { End = Scope.Start }));
        await Assert.ThrowsAsync<ArgumentException>(() => probe.RunAsync(Scope with { MaxEvents = 0 }));
        Assert.Empty(handler.Methods);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ObservationAndDetailBudgetsStopBeforeExtraDetailRead(bool observationLimit)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        handler.Add(new { value = new[] { ScriptedGraphHandler.Event(), ScriptedGraphHandler.Event("second") } });
        handler.Add(ScriptedGraphHandler.Event());
        using var client = new HttpClient(handler);
        var scope = observationLimit ? Scope with { MaxEvents = 1 } : Scope with { MaxDetailReads = 1 };
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(scope);
        Assert.False(result.Complete);
        Assert.Equal(1, result.DetailReads);
        Assert.Equal(6, handler.Methods.Count);
    }

    [Fact]
    public async Task DetailVersionChangeAndUnexpectedMailboxBlockTheScope()
    {
        using var changed = new ScriptedGraphHandler();
        changed.AddReadFoundation(Mailbox);
        changed.Add(new { value = new[] { ScriptedGraphHandler.Event() } });
        changed.Add(ScriptedGraphHandler.Event(version: "v2"));
        using var client = new HttpClient(changed);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.Empty(result.Events);

        using var wrong = new ScriptedGraphHandler();
        wrong.Add(new { id = "other", mail = "other@example.test" });
        using var wrongClient = new HttpClient(wrong);
        Assert.False((await new CalendarReadProbe(new(wrongClient, _ => Task.FromResult("fake"))).RunAsync(Scope)).Complete);
        Assert.Single(wrong.Methods);
    }

    [Fact]
    public void ProductionHandlerDisablesRedirectsAndCookies()
    {
        using var handler = GraphTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    public static IEnumerable<object?[]> InvalidDiscoveryFields
    {
        get
        {
            string[] required = ["id", "changeKey", "start", "end", "originalStartTimeZone", "originalEndTimeZone",
                "isOrganizer", "organizer", "attendees", "isAllDay", "isCancelled", "type"];
            var invalid = required.Select(propertyName => (Field: propertyName, Json: (string?)null)).Concat(
            [
                ("id", "null"), ("changeKey", "1"), ("start", "null"), ("end", "[]"),
                ("start", "{\"dateTime\":\"not-a-date\",\"timeZone\":\"UTC\"}"),
                ("start", "{\"dateTime\":\"2030-01-01\",\"timeZone\":\"UTC\"}"),
                ("end", "{\"dateTime\":\"2030-01-01T10:00:00\"}"),
                ("originalStartTimeZone", "null"), ("originalEndTimeZone", "\"\""),
                ("isOrganizer", "null"), ("isOrganizer", "\"true\""), ("isAllDay", "0"), ("isCancelled", "{}"),
                ("organizer", "null"), ("organizer", "{\"emailAddress\":{}}"),
                ("attendees", "null"), ("attendees", "{}"), ("attendees", "[null]"),
                ("attendees", "[{\"type\":\"required\"}]"),
                ("attendees", "[{\"type\":\"unknownFutureValue\",\"emailAddress\":{\"address\":\"test@example.test\"}}]"),
                ("type", "null"), ("type", "\"unknownFutureValue\"")
            ]);
            foreach (var (propertyName, json) in invalid)
            {
                foreach (var location in new[] { "view", "detail" })
                {
                    yield return [propertyName, json, location];
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDiscoveryFields))]
    public async Task MissingOrMalformedRequiredFieldsInEitherReadCannotComplete(string field, string? json, string location)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var valid = ScriptedGraphHandler.Event(attendees: true);
        var invalid = WithField(valid, field, json);
        handler.Add(new { value = new[] { location == "detail" ? valid : invalid } });
        if (location == "detail") { handler.Add(invalid); }
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.NotEmpty(result.Holds);
        Assert.Empty(result.Events);
        Assert.Equal(0, result.OrganizerMeetings);
        Assert.Equal(location == "detail" ? 6 : 5, handler.Methods.Count);
        Assert.False(CalendarReadProbe.Equivalent(invalid, invalid));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"event\"")]
    public async Task NonObjectObservationIsIncompleteWithoutThrowingOrFetchingDetails(string json)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var item = JsonSerializer.Deserialize<JsonElement>(json);
        handler.Add(new { value = new[] { item } });
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.Empty(result.Events);
        Assert.Equal(5, handler.Methods.Count);
        Assert.False(CalendarReadProbe.Equivalent(item, item));
    }

    [Theory]
    [InlineData("singleInstance", true)]
    [InlineData("seriesMaster", true)]
    [InlineData("occurrence", false)]
    [InlineData("exception", false)]
    public async Task OnlyNonApplicableMasterIdentityCanBeAbsent(string type, bool expectedComplete)
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var item = WithField(WithField(ScriptedGraphHandler.Event(), "type", JsonSerializer.Serialize(type)), "seriesMasterId", null);
        handler.Add(new { value = new[] { item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.Equal(expectedComplete, result.Complete);
    }

    [Fact]
    public async Task InvalidClassificationAfterValidMeetingKeepsPartialCountsButBlocksCompleteness()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var valid = ScriptedGraphHandler.Event(attendees: true);
        var invalid = WithField(ScriptedGraphHandler.Event("second", attendees: true), "attendees", null);
        handler.Add(new { value = new[] { valid, invalid } });
        handler.Add(valid);
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.False(result.Complete);
        Assert.Single(result.Events);
        Assert.Equal(1, result.OrganizerMeetings);
        Assert.Equal(1, result.DetailReads);
        Assert.NotEmpty(result.Holds);
    }

    [Fact]
    public async Task ExplicitFalseOwnershipAndEmptyAttendeesRemainValidKnownClassifications()
    {
        using var handler = new ScriptedGraphHandler();
        handler.AddReadFoundation(Mailbox);
        var item = WithField(ScriptedGraphHandler.Event(), "isOrganizer", "false");
        item = WithField(item, "start", "{\"dateTime\":\"2030-01-01T09:00:00.0000000\",\"timeZone\":\"UTC\"}");
        handler.Add(new { value = new[] { item } });
        handler.Add(item);
        using var client = new HttpClient(handler);
        var result = await new CalendarReadProbe(new(client, _ => Task.FromResult("fake"))).RunAsync(Scope);
        Assert.True(result.Complete);
        Assert.Single(result.Events);
        Assert.Equal(0, result.OrganizerMeetings);
    }

    private static JsonElement WithField(JsonElement item, string field, string? json)
    {
        var fields = item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        if (json is null) { fields.Remove(field); }
        else { fields[field] = JsonSerializer.Deserialize<JsonElement>(json); }
        return JsonSerializer.SerializeToElement(fields);
    }
}
