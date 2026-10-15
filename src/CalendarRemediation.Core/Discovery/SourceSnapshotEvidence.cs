namespace CalendarRemediation.Core.Discovery;

/// <summary>Compares all typed source evidence by value, including immutable collection contents.</summary>
public static class SourceSnapshotEvidence
{
    public static bool Matches(SourceSnapshot first, SourceSnapshot second)
    {
        if (first.Attendees.IsDefault || second.Attendees.IsDefault || first.Unresolved.IsDefault || second.Unresolved.IsDefault)
        {
            return false;
        }
        // Record equality covers every scalar and nested value record, including future
        // scalar fields. Compare collection contents separately from their backing storage.
        return (first with { Attendees = default, Unresolved = default })
                == (second with { Attendees = default, Unresolved = default })
            && first.Attendees.SequenceEqual(second.Attendees)
            && first.Unresolved.SequenceEqual(second.Unresolved);
    }
}
