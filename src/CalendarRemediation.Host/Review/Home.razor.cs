using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CalendarRemediation.Host.Review;

public partial class Home
{
    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("en-US");
    private static readonly (string Key, string Label, string Icon)[] Navigation =
    [
        ("meetings", "Meeting workspace", "▦"), ("locations", "Locations & rules", "⌖"),
        ("preview", "Change preview", "▤"), ("results", "Demo results", "✓")
    ];
    private static readonly (string Key, string Label)[] Filters =
    [
        ("all", "All meetings"), ("review", "Needs review"), ("ready", "Confirmed"), ("skipped", "Skipped")
    ];
    private static readonly DemoLocation[] Locations =
    [
        new("Vancouver", "British Columbia", -7, -8, "coast"),
        new("Calgary", "Alberta", -6, -7, "peaks"),
        new("Toronto", "Ontario · comparison fixture", -4, -5, "city")
    ];
    private readonly List<DemoMeeting> meetings = CreateMeetings();
    private readonly List<DemoPreview> preview = [];
    private DemoLocation location = Locations[0];
    private DemoMeeting selectedMeeting = null!;
    private string view = "meetings";
    private string activeFilter = "all";
    private string search = "";
    private string message = "";
    private bool showAttendees;
    private bool hasResults;
    private int batchVersion;
    private ElementReference mainContent;
    private string renderedView = "meetings";
    private bool focusPending;

    protected override void OnInitialized() => selectedMeeting = meetings[0];

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender && (renderedView != view || focusPending))
        {
            renderedView = view;
            focusPending = false;
            await mainContent.FocusAsync();
        }
    }

    private string ViewTitle => view switch
    {
        "locations" => "Locations & rules",
        "detail" => "Meeting review",
        "preview" => "Change preview",
        "results" => "Demo results",
        "help" => "Help & demo guide",
        _ => "Meeting workspace"
    };
    private IEnumerable<DemoMeeting> FilteredMeetings => meetings.Where(m =>
        (activeFilter switch
        {
            "review" => m.Intent == DemoIntent.Unset,
            "ready" => m.Intent is DemoIntent.Local or DemoIntent.Instant,
            "skipped" => m.Intent == DemoIntent.Skip,
            _ => true
        }) && (string.IsNullOrWhiteSpace(search) || m.Title.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            m.Category.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            m.Attendees.Any(a => a.Contains(search, StringComparison.OrdinalIgnoreCase))));
    private string IntentNote => selectedMeeting.Intent switch
    {
        DemoIntent.Local => "Your demo confirmation: preserve local start. This enters the preview as a time update.",
        DemoIntent.Instant => "Your demo confirmation: preserve the absolute instant. This enters the preview as a no-change decision.",
        DemoIntent.Skip => "Skipped by you. This meeting is excluded from approval and simulation.",
        _ => "No confirmed intent. The comparison shows no update, and this meeting stays outside the batch."
    };

    private void Navigate(string destination)
    {
        if (destination == "preview")
        {
            OpenPreview();
            return;
        }
        view = destination;
        message = "";
    }
    private void SetFilter(string filter)
    {
        activeFilter = filter;
        view = "meetings";
        message = "";
    }
    private void ClearFilters()
    {
        activeFilter = "all";
        search = "";
    }
    private void OpenMeeting(DemoMeeting meeting)
    {
        selectedMeeting = meeting;
        showAttendees = false;
        view = "detail";
        message = "";
    }
    private void MoveMeeting(int direction)
    {
        OpenMeeting(meetings[(meetings.IndexOf(selectedMeeting) + direction + meetings.Count) % meetings.Count]);
        focusPending = true;
    }
    private void ReviewNext() => OpenMeeting(meetings.FirstOrDefault(m => m.Intent == DemoIntent.Unset) ?? meetings[0]);
    private void SetIntent(DemoIntent intent)
    {
        if (selectedMeeting.Intent == intent) { return; }
        selectedMeeting.Intent = intent;
        InvalidatePreview();
        message = "Choice updated in this demo session. Earlier preview and results cleared.";
    }
    private void ChooseLocation(DemoLocation city)
    {
        if (city == location)
        {
            message = $"{city.City} is your selected sample location.";
            return;
        }
        location = city;
        foreach (var meeting in meetings) { meeting.Intent = DemoIntent.Unset; }
        InvalidatePreview();
        message = $"Sample location changed to {city.City}. Confirm meeting intents again for this location. Earlier preview and results cleared.";
    }
    private void InvalidatePreview()
    {
        preview.Clear();
        hasResults = false;
    }
    private void OpenPreview()
    {
        if (preview.Count == 0)
        {
            foreach (var meeting in meetings.Where(m => m.Intent is DemoIntent.Local or DemoIntent.Instant))
            {
                preview.Add(new(meeting.Id, meeting.Title, meeting.Start, ProposedStart(meeting, meeting.Intent), meeting.Duration, meeting.Intent));
            }
            if (preview.Count > 0) { batchVersion++; }
        }
        view = "preview";
        message = "";
    }
    private void ApproveDemo()
    {
        if (preview.Count == 0) { OpenPreview(); return; }
        hasResults = true;
        view = "results";
        message = "Exact demo batch approved and simulated. No provider requests or calendar writes were made.";
    }
    private void ResetDemo()
    {
        foreach (var meeting in meetings) { meeting.Intent = DemoIntent.Unset; }
        location = Locations[0];
        InvalidatePreview();
        ClearFilters();
        selectedMeeting = meetings[0];
        showAttendees = false;
        view = "meetings";
        message = "Fresh demo ready. All sample choices, previews, and results reset.";
    }
    private DateTimeOffset ProposedStart(DemoMeeting meeting, DemoIntent intent) => intent == DemoIntent.Local
        ? meeting.Start.AddHours(location.SourceOffset - location.TargetOffset) : meeting.Start;
    private string SourceTime(DemoMeeting meeting) => LocalTime(meeting.Start, location.SourceOffset);
    private string TargetTime(DemoMeeting meeting, DemoIntent intent) => LocalTime(ProposedStart(meeting, intent), location.TargetOffset);
    private static string LocalTime(DateTimeOffset instant, int offset) => instant.ToOffset(TimeSpan.FromHours(offset)).ToString("h:mm tt", Culture);
    private static string UtcTime(DateTimeOffset instant) => instant.ToUniversalTime().ToString("HH:mm 'UTC'", Culture);
    private static string Offset(int hours) => $"{(hours < 0 ? "−" : "+")}{Math.Abs(hours):D2}:00";
    private static string Initials(string name) => string.Concat(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(part => part[0]));
    private static string StatusLabel(DemoMeeting meeting) => meeting.Intent switch
    {
        DemoIntent.Local => "Keep local time",
        DemoIntent.Instant => "Keep instant",
        DemoIntent.Skip => "Skipped",
        _ => "Needs review"
    };
    private static string StatusClass(DemoMeeting meeting) => meeting.Intent switch
    {
        DemoIntent.Local or DemoIntent.Instant => "green",
        DemoIntent.Skip => "neutral",
        _ => "amber"
    };
    private static List<DemoMeeting> CreateMeetings() =>
    [
        new("DEMO-01", "Design team catch-up", "Design & collaboration", new(2026, 11, 9, 16, 0, 0, TimeSpan.Zero), 30, ["Alex Chen", "Morgan Lee", "Taylor Brooks", "Sam Patel"], "A team catch-up might be anchored to the local working day. Its title alone cannot tell us whether that is true."),
        new("DEMO-02", "Product roadmap workshop", "Planning", new(2026, 11, 10, 18, 0, 0, TimeSpan.Zero), 60, ["Alex Chen", "Riley Wilson", "Casey Jones"], "A workshop may depend on local office hours or another team’s schedule. Which matters here?"),
        new("DEMO-03", "Cross-office project sync", "Cross-office collaboration", new(2026, 11, 11, 17, 30, 0, TimeSpan.Zero), 45, ["Morgan Lee", "Jordan Kim", "Avery Martin", "Drew Garcia", "Sam Patel"], "The cross-office title suggests another time anchor could matter. Keeping the instant may be intentional, but the title is not proof."),
        new("DEMO-04", "Customer research readout", "Research", new(2026, 11, 12, 21, 0, 0, TimeSpan.Zero), 45, ["Taylor Brooks", "Casey Jones", "Alex Chen"], "This internal readout could follow the local afternoon. Confirm whether its local time or agreed instant should stay fixed."),
        new("DEMO-05", "Launch readiness review", "Planning", new(2026, 11, 13, 16, 0, 0, TimeSpan.Zero), 60, ["Riley Wilson", "Jordan Kim", "Morgan Lee", "Avery Martin"], "A launch review may be linked to an external deadline. There is no deadline evidence in this sample, so neither intent can be assumed."),
        new("DEMO-06", "Coffee & new ideas", "Team connection", new(2026, 11, 13, 22, 0, 0, TimeSpan.Zero), 30, ["Drew Garcia", "Sam Patel"], "An informal coffee might favor a familiar local time. That is only a mock hypothesis. You can leave it unresolved.")
    ];
    private enum DemoIntent { Unset, Local, Instant, Skip }
    private sealed record DemoLocation(string City, string Region, int SourceOffset, int TargetOffset, string Art);
    private sealed record DemoPreview(string Id, string Title, DateTimeOffset Before, DateTimeOffset After, int Duration, DemoIntent Intent);
    private sealed class DemoMeeting(string id, string title, string category, DateTimeOffset start, int duration, string[] attendees, string hypothesis)
    {
        public string Id { get; } = id;
        public string Title { get; } = title;
        public string Category { get; } = category;
        public DateTimeOffset Start { get; } = start;
        public int Duration { get; } = duration;
        public string[] Attendees { get; } = attendees;
        public string Hypothesis { get; } = hypothesis;
        public DemoIntent Intent { get; set; }
    }
}
