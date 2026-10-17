using System.Collections.Immutable;
using CalendarRemediation.Core.Discovery;
using CalendarRemediation.Core.Planning;

namespace CalendarRemediation.Host.Authentication;

public sealed class LocalConnectionOptions
{
    public int Port { get; set; } = 5009;
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string CacheDirectory { get; set; } = "";
}

public enum ConnectionPhase { Disabled, SignedOut, SigningIn, AwaitingCompletion, Connected, Discovering, Failed }
public sealed record ConnectionProblem(string Code, string Message);
public sealed record ConfirmedLocation(string Province, string Community, string TimeZoneId);
public sealed record VerifiedMailbox(CalendarIdentity Identity, string Address, string DisplayName);
public sealed record ConnectionMeeting(string Id, DateTimeOffset? Start, DateTimeOffset? End,
    string? Organizer, EventKind Kind, int AttendeeCount);
public sealed record ConnectionDiscovery(DateTimeOffset Start, DateTimeOffset End, bool Complete,
    int ObservedEvents, ImmutableArray<ConnectionMeeting> OrganizerMeetings, ImmutableArray<string> Holds);
public sealed record ConnectionState(string Version, ConnectionPhase Phase, string? CachedAccount,
    VerifiedMailbox? Mailbox, ConfirmedLocation? Location, string SuggestedTimeZoneId,
    ConnectionDiscovery? Discovery, ConnectionProblem? Problem, MeetingTimePreview? Preview = null);

public sealed record BrowserSignInContext(Uri ReturnUri, Func<Uri, Task> OpenBrowserAsync);

// Tokens stay inside these server-side seams. They are never part of a state DTO.
public interface ILocalIdentityClient : IDisposable
{
    ConnectionProblem? Unavailable { get; }
    Task<string?> GetCachedAccountAsync(CancellationToken cancellationToken);
    Task<SignInObservation> AuthenticateAsync(bool interactive, BrowserSignInContext? browser, CancellationToken cancellationToken);
    Task<string> AcquireReadTokenAsync(SignInObservation account, CancellationToken cancellationToken);
    Task ClearAsync();
}

public interface ILocalCalendarClient
{
    Task<VerifiedMailbox> VerifyAsync(SignInObservation account, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken);
    Task<DiscoveryResult> DiscoverAsync(DiscoveryScope scope, Func<CancellationToken, Task<string>> token,
        CancellationToken cancellationToken);
}

public sealed class ConnectionException(ConnectionProblem problem) : Exception(problem.Message)
{
    public ConnectionProblem Problem { get; } = problem;
}
