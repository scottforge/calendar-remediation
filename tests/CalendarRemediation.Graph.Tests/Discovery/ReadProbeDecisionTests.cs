using System.Text.Json;
using CalendarRemediation.Graph.Discovery;

namespace CalendarRemediation.Graph.Tests.Discovery;

public sealed class ReadProbeDecisionTests
{
    private static readonly string[] CompletedEventIds = ["first", "second"];

    private static ReadProbeOptions Scope => new("11111111-1111-1111-1111-111111111111",
        "22222222-2222-2222-2222-222222222222", new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2030, 2, 1, 0, 0, 0, TimeSpan.Zero), 10, 3, 10, 10);

    [Fact]
    public void EquivalentDuplicatesConsumeObservationBudgetEvenAtDetailLimit()
    {
        var state = new ReadProbeState(Scope with { MaxEvents = 2, MaxDetailReads = 1 });
        var item = ScriptedGraphHandler.Event();
        Assert.True(state.AdmitObservation(item));

        Assert.False(state.AdmitObservation(item));
        Assert.Equal(2, state.Observations);
        Assert.Equal(1, state.DetailReads);
        Assert.Empty(state.Events);

        Assert.Throws<InvalidDataException>(() => state.AdmitObservation(item));
        Assert.Equal(3, state.Observations);
        Assert.Equal(1, state.DetailReads);
    }

    [Fact]
    public void MalformedObservationConsumesBudgetWithoutReservingDetail()
    {
        var state = new ReadProbeState(Scope with { MaxEvents = 1 });
        var malformed = WithField(ScriptedGraphHandler.Event(), "attendees", "null");

        Assert.Throws<InvalidDataException>(() => state.AdmitObservation(malformed));
        Assert.Equal(1, state.Observations);
        Assert.Equal(0, state.DetailReads);
        Assert.Empty(state.Events);
        Assert.Equal(0, state.OrganizerMeetings);

        Assert.Throws<InvalidDataException>(() => state.AdmitObservation(ScriptedGraphHandler.Event()));
        Assert.Equal(2, state.Observations);
        Assert.Equal(0, state.DetailReads);
    }

    [Fact]
    public void DetailReservationBlocksNewIdentityButAllowsEquivalentDuplicate()
    {
        var state = new ReadProbeState(Scope with { MaxDetailReads = 1 });
        var item = ScriptedGraphHandler.Event();
        Assert.True(state.AdmitObservation(item));
        Assert.False(state.AdmitObservation(item));

        Assert.Throws<InvalidDataException>(() => state.AdmitObservation(ScriptedGraphHandler.Event("second")));

        Assert.Equal(3, state.Observations);
        Assert.Equal(1, state.DetailReads);
        Assert.Empty(state.Events);
    }

    [Fact]
    public void SameVersionWithChangedSelectedEvidenceConflictsWithoutAnotherReservation()
    {
        var state = new ReadProbeState(Scope);
        var item = ScriptedGraphHandler.Event(attendees: true);
        Assert.True(state.AdmitObservation(item));
        state.RecordDetail(item, item);
        var changed = WithField(item, "originalStartTimeZone", "\"Europe/Paris\"");
        Assert.True(ReadProbeValidation.IsValid(changed));

        Assert.Throws<InvalidDataException>(() => state.AdmitObservation(changed));

        Assert.Equal(2, state.Observations);
        Assert.Equal(1, state.DetailReads);
        Assert.Equal("UTC", Assert.Single(state.Events).GetProperty("originalStartTimeZone").GetString());
        Assert.Equal(1, state.OrganizerMeetings);
    }

    [Theory]
    [InlineData("attendees", "null")]
    [InlineData("originalEndTimeZone", "\"Europe/Paris\"")]
    public void RejectedDetailDoesNotPublishEventOrChangeCompletedCounts(string field, string json)
    {
        var state = new ReadProbeState(Scope);
        var completed = ScriptedGraphHandler.Event("completed", attendees: true);
        Assert.True(state.AdmitObservation(completed));
        state.RecordDetail(completed, completed);
        var pending = ScriptedGraphHandler.Event("pending", attendees: true);
        Assert.True(state.AdmitObservation(pending));

        Assert.Throws<InvalidDataException>(() => state.RecordDetail(pending, WithField(pending, field, json)));

        Assert.Equal(2, state.DetailReads);
        Assert.Equal("completed", Assert.Single(state.Events).GetProperty("id").GetString());
        Assert.Equal(1, state.OrganizerMeetings);
    }

    [Fact]
    public void StateOwnsObservationAndDetailBeyondIndependentDocumentLifetimes()
    {
        var state = new ReadProbeState(Scope);
        var json = ScriptedGraphHandler.Event(attendees: true).GetRawText();
        using (var originalDocument = JsonDocument.Parse(json))
        {
            Assert.True(state.AdmitObservation(originalDocument.RootElement));
        }

        using (var duplicateDocument = JsonDocument.Parse(json))
        {
            Assert.False(state.AdmitObservation(duplicateDocument.RootElement));
        }

        using (var observationDocument = JsonDocument.Parse(json))
        using (var detailDocument = JsonDocument.Parse(json))
        {
            state.RecordDetail(observationDocument.RootElement, detailDocument.RootElement);
        }

        Assert.Equal("event", Assert.Single(state.Events).GetProperty("id").GetString());
        Assert.Equal("attendee@example.test", state.Events[0].GetProperty("attendees")[0]
            .GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.Equal(1, state.OrganizerMeetings);
        Assert.False(state.AdmitObservation(ScriptedGraphHandler.Event(attendees: true)));
        Assert.Equal(1, state.DetailReads);
    }

    [Fact]
    public void PublishedEventsAreSnapshotsOfCompletedWork()
    {
        var state = new ReadProbeState(Scope);
        var first = ScriptedGraphHandler.Event("first");
        Assert.True(state.AdmitObservation(first));
        state.RecordDetail(first, first);
        var snapshot = state.Events;
        var second = ScriptedGraphHandler.Event("second");
        Assert.True(state.AdmitObservation(second));
        Assert.Single(state.Events);
        state.RecordDetail(second, second);

        Assert.Equal("first", Assert.Single(snapshot).GetProperty("id").GetString());
        Assert.Equal(CompletedEventIds, state.Events.Select(e => e.GetProperty("id").GetString()));
    }

    [Theory]
    [InlineData(true, true, "singleInstance", 1)]
    [InlineData(false, true, "singleInstance", 0)]
    [InlineData(true, false, "singleInstance", 0)]
    [InlineData(true, true, "seriesMaster", 0)]
    public void OrganizerMeetingsCountOnlyCompletedAttendeeBearingSingleInstances(
        bool organizer, bool attendees, string type, int expected)
    {
        var state = new ReadProbeState(Scope);
        var item = WithField(ScriptedGraphHandler.Event(attendees: attendees), "isOrganizer", JsonSerializer.Serialize(organizer));
        item = WithField(item, "type", JsonSerializer.Serialize(type));
        Assert.True(state.AdmitObservation(item));
        Assert.Equal(0, state.OrganizerMeetings);
        Assert.Empty(state.Events);

        state.RecordDetail(item, item);

        Assert.Single(state.Events);
        Assert.Equal(expected, state.OrganizerMeetings);
    }

    [Fact]
    public void LocalNonUtcDateTimesAreValidShapeWithoutNormalizingEvidence()
    {
        var item = ScriptedGraphHandler.Event();
        item = WithField(item, "start", "{\"dateTime\":\"2030-01-01T09:00:00.1234567\",\"timeZone\":\"Europe/Paris\"}");
        item = WithField(item, "end", "{\"dateTime\":\"2030-01-01T10:00:00\",\"timeZone\":\"Europe/Paris\"}");
        item = WithField(item, "originalStartTimeZone", "\"Romance Standard Time\"");
        var state = new ReadProbeState(Scope);

        Assert.True(ReadProbeValidation.IsValid(item));
        Assert.True(state.AdmitObservation(item));
        state.RecordDetail(item, item);

        var stored = Assert.Single(state.Events);
        Assert.Equal("Europe/Paris", stored.GetProperty("start").GetProperty("timeZone").GetString());
        Assert.Equal("2030-01-01T09:00:00.1234567", stored.GetProperty("start").GetProperty("dateTime").GetString());
        Assert.Equal("Romance Standard Time", stored.GetProperty("originalStartTimeZone").GetString());
    }

    [Theory]
    [InlineData("occurrence", "\"master\"", true)]
    [InlineData("exception", "\"master\"", true)]
    [InlineData("occurrence", "null", false)]
    [InlineData("exception", "\" \"", false)]
    [InlineData("singleInstance", "\"master\"", false)]
    [InlineData("seriesMaster", "\"master\"", false)]
    public void RecurrenceMasterIdentityMustAgreeWithEventKind(string type, string masterJson, bool expected)
    {
        var item = WithField(ScriptedGraphHandler.Event(), "type", JsonSerializer.Serialize(type));
        item = WithField(item, "seriesMasterId", masterJson);

        Assert.Equal(expected, ReadProbeValidation.IsValid(item));
        Assert.Equal(expected, CalendarReadProbe.Equivalent(item, item));
    }

    private static JsonElement WithField(JsonElement item, string field, string json)
    {
        var fields = item.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        fields[field] = JsonSerializer.Deserialize<JsonElement>(json);
        return JsonSerializer.SerializeToElement(fields);
    }
}
