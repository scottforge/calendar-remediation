namespace CalendarRemediation.Host.Review.Models;

public sealed record DemoPreview(
    string Id,
    string Title,
    DateTimeOffset Before,
    DateTimeOffset After,
    int Duration,
    DemoIntent Intent,
    IReadOnlyList<DemoPreviewOccurrence> Occurrences);

public sealed record DemoPreviewOccurrence(
    DateTimeOffset BeforeStart,
    DateTimeOffset BeforeEnd,
    DateTimeOffset AfterStart,
    DateTimeOffset AfterEnd);
