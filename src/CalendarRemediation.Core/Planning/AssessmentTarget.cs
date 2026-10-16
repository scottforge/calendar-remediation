namespace CalendarRemediation.Core.Planning;

internal static class AssessmentTarget
{
    internal static DateTimeOffset? SelectStart(LocalResolution resolution, DateTimeOffset? selectedOverlapInstant)
    {
        if (resolution.Candidates.IsDefaultOrEmpty)
        {
            return null;
        }
        if (resolution.Kind == ResolutionKind.Unique && resolution.Candidates.Length == 1
            && (selectedOverlapInstant is null || selectedOverlapInstant == resolution.Candidates[0]))
        {
            return resolution.Candidates[0];
        }
        if (resolution.Kind == ResolutionKind.Overlap && selectedOverlapInstant is { } selected
            && resolution.Candidates.Length > 1 && resolution.Candidates.Contains(selected))
        {
            return selected;
        }
        return null;
    }

    internal static ExactPatch? ProjectDuration(DateTimeOffset start, TimeSpan duration, string zone)
    {
        try
        {
            var end = start.Add(duration);
            return new(start.ToUniversalTime(), end.ToUniversalTime(), zone);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
