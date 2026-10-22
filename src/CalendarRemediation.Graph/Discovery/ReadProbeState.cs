using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

internal sealed class ReadProbeState(ReadProbeOptions options)
{
    private readonly Dictionary<string, JsonElement> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _events = new(StringComparer.Ordinal);

    internal IReadOnlyList<JsonElement> Events => _events.Values.ToArray();
    internal int Observations { get; private set; }
    internal int DetailReads { get; private set; }
    internal string? Hold { get; private set; }
    internal int OrganizerMeetings => _events.Values.Count(e => e.GetProperty("isOrganizer").GetBoolean()
        && GraphJson.Text(e, "type") == "singleInstance" && e.GetProperty("attendees").GetArrayLength() > 0);

    internal bool AdmitObservation(JsonElement observation)
    {
        if (++Observations > options.MaxEvents)
        {
            Hold = "Observation budget exhausted. Narrow the scope or explicitly increase its budget.";
            return false;
        }
        if (!ReadProbeValidation.IsValid(observation))
        {
            Hold = "Missing or malformed required discovery/classification fields in calendar-view observation.";
            return false;
        }

        var id = GraphJson.Text(observation, "id")!;
        if (_observations.TryGetValue(id, out var previous))
        {
            if (!ReadProbeValidation.Equivalent(previous, observation))
            {
                Hold = "Repeated identity has differing version or evidence.";
            }
            return false;
        }

        _observations.Add(id, observation.Clone());
        if (DetailReads >= options.MaxDetailReads)
        {
            Hold = "Detail-read budget exhausted.";
            return false;
        }
        DetailReads++;
        return true;
    }

    internal void RecordDetail(JsonElement observation, JsonElement detail)
    {
        if (!ReadProbeValidation.IsValid(detail))
        {
            Hold = "Missing or malformed required discovery/classification fields in event detail.";
            return;
        }
        if (!ReadProbeValidation.Equivalent(observation, detail))
        {
            Hold = "Identity, version or relevant evidence changed between view and detail.";
            return;
        }
        _events.Add(GraphJson.Text(observation, "id")!, detail.Clone());
    }
}
