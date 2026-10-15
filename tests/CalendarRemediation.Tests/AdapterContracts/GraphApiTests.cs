using System.Globalization;
using CalendarRemediation.Graph;

namespace CalendarRemediation.Tests.AdapterContracts;

public sealed class GraphApiTests
{
    private const string EventProjection = "id,changeKey,start,end,originalStartTimeZone,originalEndTimeZone,isOrganizer,organizer,attendees,isAllDay,isCancelled,type,seriesMasterId";

    [Fact]
    public void CatalogPreservesCloudIdentityCalendarAndTimeZoneContracts()
    {
        Assert.Equal("https://graph.microsoft.com/v1.0/", GraphApi.Root);
        Assert.Equal("https://graph.microsoft.com/v1.0/me", GraphApi.CurrentUserUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/calendar", GraphApi.CalendarUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/calendarView", GraphApi.CalendarViewUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName", GraphApi.IdentityUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me?$select=id,mail,userPrincipalName,displayName", GraphApi.MailboxUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/calendar?$select=id,isDefaultCalendar,owner", GraphApi.DefaultCalendarUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Windows')", GraphApi.WindowsTimeZonesUrl);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')", GraphApi.IanaTimeZonesUrl);

        Assert.Equal("id,mail,userPrincipalName", GraphApi.IdentityFields);
        Assert.Equal("id,mail,userPrincipalName,displayName", GraphApi.MailboxFields);
        Assert.Equal("id,isDefaultCalendar,owner", GraphApi.CalendarFields);
        Assert.Equal(EventProjection, GraphApi.EventFields);
        Assert.Equal(EventProjection + ",subject", GraphApi.ConditionalProbeFields);
        Assert.Equal(100, GraphApi.PageSize);
    }

    [Theory]
    [InlineData("event", "event")]
    [InlineData("A/B+C=?# &", "A%2FB%2BC%3D%3F%23%20%26")]
    [InlineData("already%2Fescaped", "already%252Fescaped")]
    [InlineData("réunion/会議", "r%C3%A9union%2F%E4%BC%9A%E8%AD%B0")]
    public void EventRoutesKeepDefaultCalendarReadsSeparateFromProbeAndPatchRoutes(string id, string escapedId)
    {
        Assert.Equal("https://graph.microsoft.com/v1.0/me/calendar/events/" + escapedId + "?$select=" + EventProjection,
            GraphApi.CalendarEventDetail(id));
        Assert.Equal("https://graph.microsoft.com/v1.0/me/events/" + escapedId + "?$select=" + EventProjection,
            GraphApi.ProbeEventDetail(id));
        Assert.Equal("https://graph.microsoft.com/v1.0/me/events/" + escapedId + "?$select=" + EventProjection + ",subject",
            GraphApi.ConditionalProbeEventDetail(id));
        Assert.Equal("https://graph.microsoft.com/v1.0/me/events/" + escapedId, GraphApi.EventPatch(id));
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void CalendarViewFormatsUtcRangeInvariantlyAndEscapesOffsetAndFractionalSeconds(string culture)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var start = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);
            var end = new DateTimeOffset(2030, 2, 3, 4, 5, 6, TimeSpan.Zero).AddTicks(7654321);

            Assert.Equal("https://graph.microsoft.com/v1.0/me/calendarView"
                + "?startDateTime=2030-01-02T03%3A04%3A05.1234567%2B00%3A00"
                + "&endDateTime=2030-02-03T04%3A05%3A06.7654321%2B00%3A00"
                + "&$top=100&$select=" + EventProjection, GraphApi.CalendarView(start, end));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Theory]
    [InlineData("http://graph.microsoft.com/v1.0/me/calendarView")]
    [InlineData("https://attacker.example/v1.0/me/calendarView")]
    [InlineData("https://graph.microsoft.com.attacker.example/v1.0/me/calendarView")]
    [InlineData("https://graph.microsoft.com:444/v1.0/me/calendarView")]
    [InlineData("https://user@graph.microsoft.com/v1.0/me/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendarView#fragment")]
    [InlineData("https://graph.microsoft.com/beta/me/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/users/other/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/meToo/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/Me/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/me/../users/other/calendarView")]
    [InlineData("/v1.0/me/calendarView")]
    public void UntrustedDestinationsAreRejectedByBothValidators(string address)
    {
        Assert.Throws<InvalidOperationException>(() => GraphApi.ValidateUri(address));
        Assert.Throws<InvalidOperationException>(() => GraphApi.IsCalendarViewUri(address));
    }

    [Theory]
    [InlineData("not a URI")]
    [InlineData("https://")]
    public void MalformedDestinationsAreRejected(string address)
    {
        Assert.Throws<UriFormatException>(() => GraphApi.ValidateUri(address));
        Assert.Throws<UriFormatException>(() => GraphApi.IsCalendarViewUri(address));
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendarView?$skiptoken=A%2fb%2B%2526%3D&opaque=one+two&opaque=three")]
    public void CalendarViewAcceptsOpaqueQueryWithoutRequiringInitialRangeOrProjection(string address)
    {
        Assert.True(GraphApi.IsCalendarViewUri(address));
        Assert.Equal(address, GraphApi.ValidateUri(address).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/me")]
    [InlineData("https://graph.microsoft.com/v1.0/me/events")]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendar/calendarView")]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendarView/")]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendarView/child")]
    [InlineData("https://graph.microsoft.com/v1.0/me/calendarViewOther")]
    [InlineData("https://graph.microsoft.com/v1.0/me/CalendarView")]
    public void TrustedUserDestinationIsNotNecessarilyCalendarView(string address)
    {
        Assert.Equal(address, GraphApi.ValidateUri(address).AbsoluteUri);
        Assert.False(GraphApi.IsCalendarViewUri(address));
    }
}
