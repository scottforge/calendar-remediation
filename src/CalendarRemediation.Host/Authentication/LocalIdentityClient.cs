using Microsoft.Extensions.Options;

namespace CalendarRemediation.Host.Authentication;

public sealed class LocalIdentityClient : ILocalIdentityClient
{
    private readonly object _sync = new();
    private readonly TenantSignInOptions _options;
    private TenantSignIn? _signIn;
    private bool _disposed;
    public ConnectionProblem? Unavailable { get; }

    public LocalIdentityClient(IOptions<LocalConnectionOptions> options)
    {
        var value = options.Value;
        Unavailable = !Guid.TryParse(value.TenantId, out _) || !Guid.TryParse(value.ClientId, out _)
                ? new("configuration-required", "Configure the Microsoft 365 tenant and public client IDs to enable connection.")
                : null;
        var directory = string.IsNullOrWhiteSpace(value.CacheDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CalendarRemediation", "Identity", value.TenantId, value.ClientId)
            : value.CacheDirectory;
        if (OperatingSystem.IsWindows() && Unavailable is null && !Path.IsPathFullyQualified(directory))
        {
            Unavailable = new("configuration-required", "Protected account storage requires an absolute per-user cache directory.");
        }
        _options = new(value.TenantId, value.ClientId, "", directory);
    }

    public Task<string?> GetCachedAccountAsync(CancellationToken cancellationToken) => Client().GetCachedAccountAsync(cancellationToken);

    public async Task<SignInObservation> AuthenticateAsync(bool interactive, BrowserSignInContext? browser, CancellationToken cancellationToken)
    {
        if (!interactive) { return await Client().ContinueAsync(cancellationToken); }
        ArgumentNullException.ThrowIfNull(browser);
        // This onboarding flow is deliberately a single-account, read-only cache.
        await Client().ClearAsync();
        return await Client().SignInAsync(write: false, browser, cancellationToken);
    }

    public Task<string> AcquireReadTokenAsync(SignInObservation account, CancellationToken cancellationToken) =>
        Client().AcquireReadTokenAsync(account, cancellationToken);

    public Task ClearAsync() => Client().ClearAsync();

    private TenantSignIn Client()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Unavailable is not null) { throw new ConnectionException(Unavailable); }
            // Failed cache ownership/protection attempts may be retried after the cause is fixed.
            return _signIn ??= new(_options);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) { return; }
            _disposed = true;
            _signIn?.Dispose();
        }
    }
}
