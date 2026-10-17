namespace CalendarRemediation.Host.Review.Models;

public enum DemoReviewMode { Guided, Assisted, Express }
public enum DemoRecurrence { OneOff, Weekly, Complex }
public enum DemoAssessmentKind { NotAssessed, ProposeLocal, NoChange, CannotAssess, Unsupported }

public sealed record DemoAssessment(DemoAssessmentKind Kind, string Label, string Rationale)
{
    public static DemoAssessment NotAssessed { get; } = new(DemoAssessmentKind.NotAssessed, "Not assessed", "Request rule-based suggestions or review the meeting notes to choose a time.");
}
