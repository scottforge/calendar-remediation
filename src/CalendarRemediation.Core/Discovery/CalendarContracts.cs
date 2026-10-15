using System.Collections.Immutable;

namespace CalendarRemediation.Core.Discovery;

public sealed record CalendarIdentity(string TenantId, string MailboxId, string CalendarId);
public sealed record DiscoveryBudget(int Events, int Pages, int Details, TimeSpan Duration);
public sealed record DiscoveryScope(CalendarIdentity Identity, DateTimeOffset Start, DateTimeOffset End,
    DateTimeOffset ExecutionCutoff, DiscoveryBudget Budget);
public enum EventKind { Unknown, SingleInstance, Occurrence, Exception, SeriesMaster }
public enum AttendeeKind { Unknown, Internal, External, Resource }
public sealed record Attendee(string Address, AttendeeKind Kind);
public sealed record ProviderTime(string? DateTime, string? Zone);
public sealed record ZoneProvenance(string? OriginalStart, string? OriginalEnd, string ResponseZone);
public sealed record SourceSnapshot(CalendarIdentity Identity, string Id, string? Version, string? Guard,
    DateTimeOffset? Start, DateTimeOffset? End, ProviderTime RawStart, ProviderTime RawEnd,
    bool? IsOrganizer, string? Organizer,
    ImmutableArray<Attendee> Attendees, bool? IsAllDay, bool? IsCancelled, EventKind Kind,
    string? SeriesMasterId, ZoneProvenance Zones, ImmutableArray<string> Unresolved);
public sealed record DiscoveryResult(DiscoveryScope Scope, bool Complete,
    ImmutableArray<SourceSnapshot> Events, ImmutableArray<string> SupportedZones,
    ImmutableArray<string> Holds, int Pages, int DetailReads);

public interface ICalendarReader
{
    Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, CancellationToken cancellationToken = default);
    Task<SourceSnapshot?> ReadAsync(CalendarIdentity identity, string eventId, CancellationToken cancellationToken = default);
}
