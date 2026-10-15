using CalendarRemediation.Core.Discovery;

namespace CalendarRemediation.Core.Planning;

public static class WriteEligibility
{
    public static bool IsAdmitted(SourceSnapshot source, WriteAdmission? admission, string? evidenceHash = null)
    {
        if (source.Attendees.IsDefault || source.Unresolved.IsDefault) { return false; }
        var category = source.Attendees.IsEmpty ? "appointment-single" : "internal-attendee-single";
        return admission is { Admitted: true, AtomicGuardProven: true, GuardHeader: "If-Match" }
            && admission.Identity == source.Identity && admission.Category == category
            && !string.IsNullOrWhiteSpace(admission.ReviewedBy) && !string.IsNullOrWhiteSpace(admission.EvidenceHash)
            && (evidenceHash is null || admission.EvidenceHash == evidenceHash)
            && !string.IsNullOrWhiteSpace(source.Id) && !string.IsNullOrWhiteSpace(source.Version)
            && source.Id is not ("." or "..") && !source.Id.Any(char.IsControl)
            && !string.IsNullOrWhiteSpace(source.Identity.TenantId) && !string.IsNullOrWhiteSpace(source.Identity.MailboxId)
            && !string.IsNullOrWhiteSpace(source.Identity.CalendarId)
            && !string.IsNullOrWhiteSpace(source.Guard) && source.Guard != "*"
            && source.Guard.Length >= 2 && (source.Guard.StartsWith('"') || source.Guard.StartsWith("W/\"", StringComparison.Ordinal))
            && source.Guard.EndsWith('"') && !source.Guard.Contains('\r') && !source.Guard.Contains('\n')
            && source.IsOrganizer == true && source.IsAllDay == false && source.IsCancelled == false
            && source.Kind == EventKind.SingleInstance && source.SeriesMasterId is null
            && source.Start is not null && source.End > source.Start && source.Unresolved.IsEmpty
            && !string.IsNullOrWhiteSpace(source.Organizer)
            && source.Attendees.All(a => a.Kind == AttendeeKind.Internal && !string.IsNullOrWhiteSpace(a.Address));
    }
}
