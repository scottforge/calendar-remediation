using System.Globalization;

namespace CalendarRemediation.Graph;

// Code-level configuration for the supported Graph cloud, API and query shapes.
public static class GraphApi
{
    public const string Root = "https://graph.microsoft.com/v1.0/";
    public const string CurrentUserUrl = Root + "me";
    public const string CalendarUrl = CurrentUserUrl + "/calendar";
    public const string CalendarViewUrl = CurrentUserUrl + "/calendarView";
    public const string IdentityFields = "id,mail,userPrincipalName";
    public const string MailboxFields = IdentityFields + ",displayName";
    public const string CalendarFields = "id,isDefaultCalendar,owner";
    public const string EventFields = "id,changeKey,start,end,originalStartTimeZone,originalEndTimeZone,isOrganizer,organizer,attendees,isAllDay,isCancelled,type,seriesMasterId";
    public const string ConditionalProbeFields = EventFields + ",subject";
    public const int PageSize = 100;

    public const string IdentityUrl = CurrentUserUrl + "?$select=" + IdentityFields;
    public const string MailboxUrl = CurrentUserUrl + "?$select=" + MailboxFields;
    public const string DefaultCalendarUrl = CalendarUrl + "?$select=" + CalendarFields;
    private const string SupportedTimeZonesUrl = CurrentUserUrl + "/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'";
    public const string WindowsTimeZonesUrl = SupportedTimeZonesUrl + "Windows')";
    public const string IanaTimeZonesUrl = SupportedTimeZonesUrl + "Iana')";
    private static readonly Uri CurrentUser = new(CurrentUserUrl);
    private static readonly string CalendarViewPath = new Uri(CalendarViewUrl).AbsolutePath;

    public static string CalendarView(DateTimeOffset start, DateTimeOffset end) => CalendarViewUrl + "?startDateTime="
        + Uri.EscapeDataString(start.ToString("O", CultureInfo.InvariantCulture)) + "&endDateTime="
        + Uri.EscapeDataString(end.ToString("O", CultureInfo.InvariantCulture))
        + "&$top=" + PageSize.ToString(CultureInfo.InvariantCulture) + "&$select=" + EventFields;

    // Production reads remain scoped to the verified default calendar.
    public static string CalendarEventDetail(string eventId) => CalendarUrl + "/events/" + Uri.EscapeDataString(eventId)
        + "?$select=" + EventFields;

    public static string EventPatch(string eventId) => CurrentUserUrl + "/events/" + Uri.EscapeDataString(eventId);
    public static string ProbeEventDetail(string eventId) => EventPatch(eventId) + "?$select=" + EventFields;
    public static string ConditionalProbeEventDetail(string eventId) => EventPatch(eventId) + "?$select=" + ConditionalProbeFields;

    public static Uri ValidateUri(string address)
    {
        var uri = new Uri(address, UriKind.Absolute);
        var userPath = CurrentUser.AbsolutePath;
        if (uri.Scheme != CurrentUser.Scheme || uri.Host != CurrentUser.Host || !uri.IsDefaultPort
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.StartsWith(userPath, StringComparison.Ordinal)
            || (uri.AbsolutePath.Length > userPath.Length && uri.AbsolutePath[userPath.Length] != '/'))
        {
            throw new InvalidOperationException("Graph destination is outside the configured cloud and user path.");
        }
        return uri;
    }

    // Validate without rebuilding opaque continuation queries.
    public static bool IsCalendarViewUri(string address) => ValidateUri(address).AbsolutePath == CalendarViewPath;
}
