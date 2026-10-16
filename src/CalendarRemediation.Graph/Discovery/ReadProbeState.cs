using System.Text.Json;

namespace CalendarRemediation.Graph.Discovery;

internal sealed class ReadProbeState(ReadProbeOptions options)
{
    private readonly Dictionary<string, JsonElement> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement> _events = new(StringComparer.Ordinal);

    internal IReadOnlyList<JsonElement> Events => _events.Values.ToArray();
    internal int Observations { get; private set; }
    internal int DetailReads { get; private set; }
    internal int OrganizerMeetings => _events.Values.Count(e => e.GetProperty("isOrganizer").GetBoolean()
        && GraphJson.Text(e, "type") == "singleInstance" && e.GetProperty("attendees").GetArrayLength() > 0);

    internal bool AdmitObservation(JsonElement observation)
    {
        if (++Observations > options.MaxEvents)
        {
            throw new InvalidDataException("Observation budget exhausted. Narrow the scope or explicitly increase its budget.");
        }
        if (!ReadProbeValidation.IsValid(observation))
        {
            throw new InvalidDataException("Missing or malformed required discovery/classification fields in calendar-view observation.");
        }

        var id = GraphJson.Text(observation, "id")!;
        if (_observations.TryGetValue(id, out var previous))
        {
            if (!ReadProbeValidation.Equivalent(previous, observation))
            {
                throw new InvalidDataException("Repeated identity has differing version or evidence.");
            }
            return false;
        }

        _observations.Add(id, observation.Clone());
        if (DetailReads >= options.MaxDetailReads)
        {
            throw new InvalidDataException("Detail-read budget exhausted.");
        }
        DetailReads++;
        return true;
    }

    internal void RecordDetail(JsonElement observation, JsonElement detail)
    {
        if (!ReadProbeValidation.IsValid(detail))
        {
            throw new InvalidDataException("Missing or malformed required discovery/classification fields in event detail.");
        }
        if (!ReadProbeValidation.Equivalent(observation, detail))
        {
            throw new InvalidDataException("Identity, version or relevant evidence changed between view and detail.");
        }
        _events.Add(GraphJson.Text(observation, "id")!, detail.Clone());
    }
}
