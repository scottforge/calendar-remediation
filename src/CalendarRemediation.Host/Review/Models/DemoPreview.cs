namespace CalendarRemediation.Host.Review.Models;

public sealed record DemoPreview(string Id, string Title, DateTimeOffset Before, DateTimeOffset After, int Duration, DemoIntent Intent);
