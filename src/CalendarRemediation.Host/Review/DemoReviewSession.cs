using CalendarRemediation.Host.Review.Models;
using CalendarRemediation.Host.Review.Presentation;

namespace CalendarRemediation.Host.Review;

/// <summary>Deterministic, circuit-local sample workflow. No provider, storage or model calls.</summary>
public sealed class DemoReviewSession
{
    public static IReadOnlyList<DemoLocation> Locations { get; } = Array.AsReadOnly<DemoLocation>(
    [
        new("Vancouver", "British Columbia", -7, -8, "coast"),
        new("Cranbrook", "British Columbia", -6, -7, "peaks"),
        new("Calgary", "Alberta", -6, -7, "peaks"),
        new("Edmonton", "Alberta", -6, -7, "city"),
        new("Regina", "Saskatchewan", -6, -6, "prairie"),
        new("Saskatoon", "Saskatchewan", -6, -6, "prairie"),
        new("Winnipeg", "Manitoba", -5, -6, "city"),
        new("Brandon", "Manitoba", -5, -6, "prairie")
    ]);

    private readonly List<DemoMeeting> meetings = CreateMeetings();
    private readonly List<DemoPreview> preview = [];
    public IReadOnlyList<DemoMeeting> Meetings => meetings.AsReadOnly();
    public IReadOnlyList<DemoPreview> Preview => preview.AsReadOnly();
    public DemoLocation Location { get; private set; } = Locations[0];
    public bool LocationConfirmed { get; private set; }
    public bool SampleAssistanceEnabled { get; private set; } = true;
    public DemoReviewMode Mode { get; private set; }
    public bool HasResults { get; private set; }
    public int BatchVersion { get; private set; }
    public int SelectedCount => meetings.Count(m => m.IsSelected);
    public int EligibleCount => meetings.Count(IsEligible);
    public bool CanApprove => LocationConfirmed && preview.Count > 0 && !HasResults &&
        preview.Count == EligibleCount && meetings.All(m => !m.IsSelected || IsEligible(m));
    private string message = "";
    public event Action? Changed;
    public string Message
    {
        get => message;
        private set { message = value; Changed?.Invoke(); }
    }

    public void SetMode(DemoReviewMode mode)
    {
        Mode = mode;
        Message = "Review mode changed. Your selections and confirmed choices are preserved.";
    }

    public bool ChooseLocation(DemoLocation location)
    {
        if (!Locations.Contains(location)) { return false; }
        if (Location == location) { return true; }
        Location = location;
        LocationConfirmed = false;
        ClearDecisions();
        Message = "Location changed. Choices, assessments, preview and results cleared. Confirm this location to continue.";
        return true;
    }

    public void ConfirmLocation()
    {
        LocationConfirmed = true;
        Message = $"{Location.City} confirmed for time comparisons.";
    }

    public void SetAssistance(bool enabled)
    {
        if (enabled == SampleAssistanceEnabled) { return; }
        SampleAssistanceEnabled = enabled;
        ClearDecisions();
        Message = "Review suggestions setting changed. All selections, choices, assessments, previews and results reset. Manual review is available.";
    }

    public bool SetSelected(string id, bool selected)
    {
        var meeting = Find(id);
        if (meeting is null || !LocationConfirmed || (selected && !meeting.IsSupported)) { return false; }
        if (meeting.IsSelected == selected) { return true; }
        meeting.IsSelected = selected;
        meeting.HasManualDecision = true;
        Invalidate();
        Message = "Selection updated. Earlier preview and simulation cleared.";
        return true;
    }

    public void SelectAllInScope()
    {
        if (!LocationConfirmed) { return; }
        foreach (var meeting in meetings.Where(m => m.IsSupported))
        {
            meeting.IsSelected = true;
            meeting.HasManualDecision = true;
            if (Mode == DemoReviewMode.Express) { meeting.Intent = DemoIntent.Local; }
        }
        Invalidate();
        Message = "All five supported meetings selected, including hidden search/filter matches. Complex recurrence excluded. Express mode uses the local-time target.";
    }

    public void DeselectAll()
    {
        foreach (var meeting in meetings) { meeting.IsSelected = false; meeting.HasManualDecision = true; }
        Invalidate();
        Message = "All meetings deselected. Confirmed time choices are retained.";
    }

    public bool SetIntent(string id, DemoIntent intent)
    {
        var meeting = Find(id);
        if (meeting is null || !LocationConfirmed || !meeting.IsSupported || !Enum.IsDefined(intent)) { return false; }
        meeting.Intent = intent;
        meeting.HasManualDecision = true;
        if (intent == DemoIntent.Skip) { meeting.IsSelected = false; }
        Invalidate();
        Message = "Your time choice is confirmed for this session. Earlier preview and simulation cleared. Selection is a separate choice.";
        return true;
    }

    public bool SelectExpress(string id, bool selected)
    {
        if (!SetSelected(id, selected)) { return false; }
        var meeting = Find(id)!;
        if (selected && meeting.Intent == DemoIntent.Unset) { SetIntent(id, DemoIntent.Local); }
        return true;
    }

    public bool AnalyzeSamples()
    {
        if (!LocationConfirmed || !SampleAssistanceEnabled) { return false; }
        foreach (var meeting in meetings)
        {
            meeting.Assessment = meeting.Id switch
            {
                "DEMO-01" => new(DemoAssessmentKind.ProposeLocal, "Local time proposed", "The meeting notes say to meet at the local office opening. Confirm whether local time should stay the same."),
                "DEMO-02" => new(DemoAssessmentKind.ProposeLocal, "Local time proposed", "The notes describe a local office workshop at the same local time each week. Review all three listed dates."),
                "DEMO-03" => new(DemoAssessmentKind.NoChange, "No change recommended", "The notes anchor the sync to a partner office's agreed instant. A different local display alone does not justify a change."),
                "DEMO-04" => new(DemoAssessmentKind.ProposeLocal, "Local time proposed", "The notes describe a local afternoon readout. Confirm whether it should follow the local working day."),
                "DEMO-05" => new(DemoAssessmentKind.CannotAssess, "Could not assess", "The launch deadline's location is missing. There is insufficient evidence for either time choice. Not automatically selected."),
                _ => new(DemoAssessmentKind.Unsupported, "Unsupported", "Moved occurrences and cancellations make series effects unknown. Excluded from selection and approval.")
            };
        }
        Invalidate();
        Message = "Rule-based suggestions shown for all six meetings. Existing choices and selections preserved. Apply suggestions separately if wanted.";
        return true;
    }

    public void ApplyRecommendations()
    {
        if (!LocationConfirmed || !SampleAssistanceEnabled) { return; }
        foreach (var meeting in meetings.Where(m => m.IsSupported && !m.HasManualDecision && m.Assessment.Kind == DemoAssessmentKind.ProposeLocal))
        {
            meeting.IsSelected = true;
            meeting.Intent = DemoIntent.Local;
            meeting.HasManualDecision = true;
        }
        Invalidate();
        Message = "Local-time suggestions confirmed and selected for meetings without manual decisions. Existing manual decisions were preserved. Review every exact time before approval.";
    }

    public void BuildPreview()
    {
        if (preview.Count > 0)
        {
            Message = HasResults
                ? "This exact batch has already been approved and simulated. Its results are preserved. Change a review decision to create a new batch."
                : "Review this existing exact batch. Approval only simulates the items shown.";
            return;
        }
        if (LocationConfirmed)
        {
            foreach (var meeting in meetings.Where(IsEligible))
            {
                var proposedStart = DemoDisplay.ProposedStart(meeting, meeting.Intent, Location);
                var occurrences = Enumerable.Range(0, meeting.Recurrence == DemoRecurrence.Weekly ? 3 : 1)
                    .Select(index => new DemoPreviewOccurrence(
                        meeting.Start.AddDays(index * 7),
                        meeting.Start.AddDays(index * 7).AddMinutes(meeting.Duration),
                        proposedStart.AddDays(index * 7),
                        proposedStart.AddDays(index * 7).AddMinutes(meeting.Duration)))
                    .ToArray();
                preview.Add(new DemoPreview(meeting.Id, meeting.Title, meeting.Start, proposedStart,
                    meeting.Duration, meeting.Intent, Array.AsReadOnly(occurrences)));
            }
        }
        if (preview.Count > 0) { BatchVersion++; }
        Message = SelectedCount != EligibleCount
            ? "Some selected meetings have no confirmed time choice. Confirm them or deselect them before approving."
            : "Review this exact batch. Approval only simulates the items shown.";
    }

    public bool ApproveAndSimulate()
    {
        if (!CanApprove) { Message = "Build a current preview with selected, supported meetings and confirmed time choices before approving."; return false; }
        HasResults = true;
        Message = "Exact batch approved and simulated. No calendar writes or invitations occurred.";
        return true;
    }

    public DemoMeeting? Find(string id) => meetings.Find(m => m.Id == id);
    public void Reset()
    {
        ClearDecisions();
        Location = Locations[0];
        LocationConfirmed = false;
        SampleAssistanceEnabled = true;
        Mode = DemoReviewMode.Guided;
        Message = "Workspace reset. Choose and confirm a location to begin again.";
    }

    private bool IsEligible(DemoMeeting m) => LocationConfirmed && m.IsSupported && m.IsSelected && m.Intent is DemoIntent.Local or DemoIntent.Instant;
    private void Invalidate() { preview.Clear(); HasResults = false; }
    private void ClearDecisions()
    {
        foreach (var m in meetings)
        {
            m.Intent = DemoIntent.Unset;
            m.IsSelected = false;
            m.HasManualDecision = false;
            m.Assessment = DemoAssessment.NotAssessed;
        }
        Invalidate();
    }

    private static List<DemoMeeting> CreateMeetings() =>
    [
        new("DEMO-01", "Design team catch-up", "Design & collaboration", new(2026, 11, 9, 16, 0, 0, TimeSpan.Zero), 30, ["Alex Chen", "Morgan Lee", "Taylor Brooks", "Sam Patel"], "Could this follow your local working day? A title alone does not establish intent.") { Notes = "Meet at the local office opening to plan the day." },
        new("DEMO-02", "Product roadmap workshop", "Planning · weekly series", new(2026, 11, 10, 18, 0, 0, TimeSpan.Zero), 60, ["Alex Chen", "Riley Wilson", "Casey Jones"], "Does the whole series follow the local office schedule?") { Recurrence = DemoRecurrence.Weekly, Notes = "Local office workshop on November 10, 17 and 24. Same local office time each week, no exceptions." },
        new("DEMO-03", "Cross-office project sync", "Cross-office collaboration", new(2026, 11, 11, 17, 30, 0, TimeSpan.Zero), 45, ["Morgan Lee", "Jordan Kim", "Avery Martin", "Drew Garcia", "Sam Patel"], "Another office's schedule may be the anchor. Confirm which time matters.") { Notes = "Keep the agreed worldwide moment with the partner office, even if our local display changes." },
        new("DEMO-04", "Customer research readout", "Research", new(2026, 11, 12, 21, 0, 0, TimeSpan.Zero), 45, ["Taylor Brooks", "Casey Jones", "Alex Chen"], "Is this anchored to your local afternoon or an agreed worldwide time?") { Notes = "Local afternoon readout for the office team, after the local research session." },
        new("DEMO-05", "Launch readiness review", "Planning", new(2026, 11, 13, 16, 0, 0, TimeSpan.Zero), 60, ["Riley Wilson", "Jordan Kim", "Morgan Lee", "Avery Martin"], "A launch deadline may matter, but its location is missing. You can leave this unresolved.") { Notes = "Review launch readiness before the deadline. The deadline location is not provided." },
        new("DEMO-06", "Coffee & new ideas", "Team connection · complex series", new(2026, 11, 13, 22, 0, 0, TimeSpan.Zero), 30, ["Drew Garcia", "Sam Patel"], "This meeting has moved occurrences and cancellations. It cannot enter the batch.") { Recurrence = DemoRecurrence.Complex, Notes = "Recurring coffee with moved occurrences and cancellations. Full exception effects are not available." }
    ];
}
