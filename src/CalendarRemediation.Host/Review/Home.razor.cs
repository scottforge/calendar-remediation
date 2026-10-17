using CalendarRemediation.Host.Components;
using CalendarRemediation.Host.Review.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CalendarRemediation.Host.Review;

public partial class Home : IDisposable
{
    private static readonly (string Key, string Label, AppIconName Icon)[] Navigation =
    [
        ("home", "Home", AppIconName.Home), ("account", "Account & AI", AppIconName.Account), ("locations", "Your location", AppIconName.Location),
        ("meetings", "Meetings", AppIconName.Meetings), ("preview", "Review changes", AppIconName.Preview), ("help", "Help", AppIconName.Help)
    ];
    private readonly DemoReviewSession session = new();
    private string view = "home";
    private string activeFilter = "all";
    private string search = "";
    private string selectedId = "DEMO-01";
    private bool showAttendees;
    private readonly List<string> guidedQueue = [];
    private readonly HashSet<string> guidedVisited = [];
    private bool IsGuidedReview => guidedQueue.Contains(selectedId) && session.Mode is DemoReviewMode.Guided or DemoReviewMode.Assisted;
    private int GuidedPosition => guidedQueue.IndexOf(selectedId) + 1;
    private bool IsLastGuidedChoice => guidedQueue.All(id => id == selectedId || guidedVisited.Contains(id));
    private ElementReference mainContent;
    private bool focusPending;
    private string renderedView = "home";
    private string ViewTitle => view switch
    {
        "account" => "Account & AI",
        "locations" => "Your location",
        "meetings" => "Meetings",
        "detail" => "Meeting review",
        "preview" => "Review changes",
        "results" => "Simulation results",
        "help" => "Help",
        _ => "Home"
    };

    protected override void OnInitialized() => session.Changed += StateHasChanged;
    public void Dispose()
    {
        session.Changed -= StateHasChanged;
        GC.SuppressFinalize(this);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender && (renderedView != view || focusPending))
        {
            renderedView = view;
            focusPending = false;
            await mainContent.FocusAsync();
        }
    }

    private void Navigate(string destination)
    {
        if (destination is "meetings" or "preview" or "detail" && !session.LocationConfirmed) { destination = "locations"; }
        if (destination == "preview") { session.BuildPreview(); }
        view = destination;
    }
    private void OpenMeeting(string id)
    {
        guidedQueue.Clear();
        guidedVisited.Clear();
        ShowMeeting(id);
    }
    private void ShowMeeting(string id)
    {
        selectedId = id;
        showAttendees = false;
        Navigate("detail");
        focusPending = true;
    }
    private void MoveMeeting(int direction)
    {
        if (IsGuidedReview)
        {
            var index = guidedQueue.IndexOf(selectedId) + direction;
            if (index >= 0 && index < guidedQueue.Count) { ShowMeeting(guidedQueue[index]); }
            return;
        }
        var ids = session.Meetings.Select(m => m.Id).ToList();
        OpenMeeting(ids[(ids.IndexOf(selectedId) + direction + ids.Count) % ids.Count]);
    }
    private void StartGuidedReview() => StartReview(DemoReviewMode.Guided);
    private void StartAssistedReview()
    {
        if (session.AnalyzeSamples()) { StartReview(DemoReviewMode.Assisted); }
    }
    private void StartReview(DemoReviewMode mode)
    {
        var queue = session.Meetings.Where(m => m.IsSupported && m.IsSelected).ToList();
        if (queue.Count == 0) { return; }
        session.SetMode(mode);
        var first = queue.FirstOrDefault(m => m.Intent == DemoIntent.Unset) ?? queue[0];
        OpenMeeting(first.Id);
        // Rotate the snapshot so every queued meeting follows the starting unresolved item.
        var start = queue.IndexOf(first);
        guidedQueue.AddRange(queue.Skip(start).Concat(queue.Take(start)).Select(m => m.Id));
    }
    private void SaveGuidedChoice()
    {
        if (!IsGuidedReview || session.Find(selectedId) is not { IsSupported: true } meeting ||
            meeting.Intent == DemoIntent.Unset) { return; }
        if (!session.SetSelected(meeting.Id, meeting.Intent is DemoIntent.Local or DemoIntent.Instant)) { return; }
        AdvanceGuidedReview();
    }
    private void SkipGuidedChoice()
    {
        if (!IsGuidedReview || !session.SetIntent(selectedId, DemoIntent.Unset)) { return; }
        session.SetSelected(selectedId, false);
        AdvanceGuidedReview();
    }
    private void AdvanceGuidedReview()
    {
        guidedVisited.Add(selectedId);
        var index = guidedQueue.IndexOf(selectedId);
        var next = guidedQueue.Skip(index + 1).Concat(guidedQueue.Take(index + 1))
            .FirstOrDefault(id => !guidedVisited.Contains(id));
        if (next is not null) { ShowMeeting(next); }
        else { Navigate("preview"); }
    }
    private void ConfirmLocation() { session.ConfirmLocation(); Navigate("meetings"); }
    private void Approve() { if (session.ApproveAndSimulate()) { Navigate("results"); } }
    private void Reset()
    {
        session.Reset();
        activeFilter = "all";
        search = "";
        selectedId = "DEMO-01";
        showAttendees = false;
        guidedQueue.Clear();
        guidedVisited.Clear();
        Navigate("home");
    }
}
