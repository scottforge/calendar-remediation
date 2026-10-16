using System.Globalization;
using System.Text.Json;
using CalendarRemediation.Graph;

namespace CalendarRemediation.Tests.BackendContracts;

public sealed class GraphEventPatchTests
{
    private static readonly string[] PatchFields = ["end", "start"];
    private static readonly string[] TimeFields = ["dateTime", "timeZone"];

    [Theory]
    [InlineData(330, "2030-01-01T18:45:00.1234567Z", "2030-01-01T19:45:00.7654321Z")]
    [InlineData(-480, "2030-01-02T08:15:00.1234567Z", "2030-01-02T09:15:00.7654321Z")]
    public void NormalizesNonzeroOffsetsAcrossDateBoundariesWithoutLosingTicks(int offsetMinutes, string expectedStart, string expectedEnd)
    {
        var start = new DateTimeOffset(2030, 1, 2, 0, 15, 0, TimeSpan.FromMinutes(offsetMinutes)).AddTicks(1234567);
        var end = start.AddHours(1).AddTicks(6419754);

        using var document = JsonDocument.Parse(GraphEventPatch.Serialize(start, end));

        Assert.Equal(PatchFields, document.RootElement.EnumerateObject().Select(p => p.Name).Order());
        AssertTime(document.RootElement.GetProperty("start"), expectedStart);
        AssertTime(document.RootElement.GetProperty("end"), expectedEnd);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void UsesInvariantGregorianUtcFormatIncludingSevenZeroFractionDigits(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var start = new DateTimeOffset(2030, 1, 2, 15, 0, 0, TimeSpan.Zero);

            using var document = JsonDocument.Parse(GraphEventPatch.Serialize(start, start.AddTicks(1)));

            AssertTime(document.RootElement.GetProperty("start"), "2030-01-02T15:00:00.0000000Z");
            AssertTime(document.RootElement.GetProperty("end"), "2030-01-02T15:00:00.0000001Z");
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsEqualOrEarlierInstantsEvenWhenEndWallClockLooksLater(int deltaTicks)
    {
        var start = new DateTimeOffset(2030, 1, 2, 10, 0, 0, TimeSpan.Zero);
        var end = start.ToOffset(TimeSpan.FromHours(2)).AddTicks(deltaTicks);

        Assert.Throws<ArgumentException>(() => GraphEventPatch.Serialize(start, end));
    }

    [Fact]
    public void AcceptsLaterInstantEvenWhenEndWallClockLooksEarlier()
    {
        var start = new DateTimeOffset(2030, 1, 2, 10, 0, 0, TimeSpan.FromHours(2));
        var end = new DateTimeOffset(2030, 1, 2, 9, 0, 0, TimeSpan.Zero);

        using var document = JsonDocument.Parse(GraphEventPatch.Serialize(start, end));

        AssertTime(document.RootElement.GetProperty("start"), "2030-01-02T08:00:00.0000000Z");
        AssertTime(document.RootElement.GetProperty("end"), "2030-01-02T09:00:00.0000000Z");
    }

    private static void AssertTime(JsonElement time, string expected)
    {
        Assert.Equal(TimeFields, time.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(expected, time.GetProperty("dateTime").GetString());
        Assert.Equal("UTC", time.GetProperty("timeZone").GetString());
    }
}
