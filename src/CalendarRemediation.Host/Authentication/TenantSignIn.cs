using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace CalendarRemediation.Host.Authentication;

public sealed record TenantSignInOptions(string TenantId, string ClientId, string MailboxObjectId, string CacheDirectory);
public sealed record SignInObservation(string TenantId, string ClientId, string MailboxObjectId, string HomeAccountId,
    DateTimeOffset At, string AuthenticationPath, string CacheProtection, string[] Scopes);

// Windows uses protected persistence and an exclusive cache lock.
// Other platforms retain tokens only in MSAL's process-local in-memory cache.
public sealed class TenantSignIn : IDisposable
{
    private readonly TenantSignInOptions _options;
    private readonly IPublicClientApplication _application;
    private readonly FileStream? _cacheLock;
    private readonly string _cachePath;
    private readonly bool _persistentCache;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private bool _disposed;

    public TenantSignIn(TenantSignInOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Guid.TryParse(options.TenantId, out _) || !Guid.TryParse(options.ClientId, out _)
            || (!string.IsNullOrEmpty(options.MailboxObjectId) && !Guid.TryParse(options.MailboxObjectId, out _)))
        {
            throw new ArgumentException("Tenant, public client and expected mailbox object IDs must be GUIDs.", nameof(options));
        }
        _options = options;
        _persistentCache = OperatingSystem.IsWindows();
        _cachePath = Path.Combine(options.CacheDirectory, "msal.dpapi");
        if (_persistentCache)
        {
            Directory.CreateDirectory(options.CacheDirectory);
            _cacheLock = new FileStream(Path.Combine(options.CacheDirectory, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        try
        {
            if (_persistentCache) { VerifyWindowsProtection(options.CacheDirectory); }
            _application = BuildApplication(options);
            if (_persistentCache) { ConfigurePersistentCache(_application); }
        }
        catch
        {
            _cacheLock?.Dispose();
            throw;
        }
    }

    private static IPublicClientApplication BuildApplication(TenantSignInOptions options) =>
        PublicClientApplicationBuilder.Create(options.ClientId)
            .WithAuthority($"https://login.microsoftonline.com/{options.TenantId}")
            .WithRedirectUri("http://localhost")
            .Build();

    private void ConfigurePersistentCache(IPublicClientApplication application)
    {
        application.UserTokenCache.SetBeforeAccess(args =>
        {
            if (File.Exists(_cachePath))
            {
                var bytes = Unprotect(File.ReadAllBytes(_cachePath));
                try { args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        });
        application.UserTokenCache.SetAfterAccess(args =>
        {
            if (args.HasStateChanged)
            {
                var bytes = args.TokenCache.SerializeMsalV3();
                try { SaveProtected(_cachePath, bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
        });
    }

    public static string[] Scopes(bool write) => [write ? "Calendars.ReadWrite" : "Calendars.Read", "User.Read"];

    public async Task<SignInObservation> SignInAsync(bool write, BrowserSignInContext browser, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(browser);
        var returnUri = browser.ReturnUri;
        if (!returnUri.IsAbsoluteUri || returnUri.Scheme != "http" || !returnUri.IsLoopback
            || returnUri.UserInfo.Length != 0 || returnUri.AbsolutePath != "/connect"
            || returnUri.Query.Length != 0 || returnUri.Fragment.Length != 0)
        {
            throw new ArgumentException("Browser sign-in must return to the local Connect page.", nameof(browser));
        }
        await _operations.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = await _application.AcquireTokenInteractive(Scopes(write)).WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    OpenBrowserAsync = uri =>
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!uri.IsAbsoluteUri || uri.Scheme != "https" || !uri.IsDefaultPort
                            || uri.Host != "login.microsoftonline.com" || uri.UserInfo.Length != 0)
                        {
                            throw new InvalidOperationException("Unexpected Microsoft authorization authority.");
                        }
                        return browser.OpenBrowserAsync(uri);
                    },
                    // MSAL serves this HTML from its loopback listener before token/account validation finishes.
                    HtmlMessageSuccess = "<!doctype html><html><head><meta charset=\"utf-8\"><meta http-equiv=\"refresh\" content=\"0;url="
                        + System.Net.WebUtility.HtmlEncode(returnUri.AbsoluteUri)
                        + "\"><title>Returning to Connect</title></head><body>Returning to Connect.</body></html>"
                })
                .WithPrompt(Prompt.SelectAccount).ExecuteAsync(ct);
            try { Validate(result); }
            catch
            {
                await _application.RemoveAsync(result.Account);
                throw;
            }
            return new(result.TenantId, _options.ClientId, result.UniqueId, result.Account.HomeAccountId.Identifier, DateTimeOffset.UtcNow,
                "MSAL public client, system browser authorization code with PKCE and loopback callback",
                _persistentCache ? "Windows DPAPI CurrentUser, persisted round-trip verified, exclusive cache lock" : "MSAL process-local in-memory token cache; not persisted", Scopes(write));
        }
        finally { _operations.Release(); }
    }

    public async Task<string?> GetCachedAccountAsync(CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var accounts = (await _application.GetAccountsAsync()).ToArray();
            return accounts.Length == 1 ? accounts[0].Username : null;
        }
        finally { _operations.Release(); }
    }

    public async Task<SignInObservation> ContinueAsync(CancellationToken ct = default)
    {
        await _operations.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var accounts = (await _application.GetAccountsAsync()).ToArray();
            if (accounts.Length != 1) { throw new InvalidOperationException("Choose an account using browser sign-in."); }
            var result = await _application.AcquireTokenSilent(Scopes(false), accounts[0]).ExecuteAsync(ct);
            Validate(result);
            return new(result.TenantId, _options.ClientId, result.UniqueId, result.Account.HomeAccountId.Identifier,
                DateTimeOffset.UtcNow, _persistentCache ? "MSAL protected cached account" : "MSAL process-local in-memory cached account",
                _persistentCache ? "Windows DPAPI CurrentUser" : "In-memory only; tokens are lost when the process exits", Scopes(false));
        }
        finally { _operations.Release(); }
    }

    public Task<string> AcquireTokenAsync(bool write, CancellationToken ct) => AcquireTokenCoreAsync(write, null, ct);

    public Task<string> AcquireReadTokenAsync(SignInObservation expected, CancellationToken ct) => AcquireTokenCoreAsync(false, expected, ct);

    private async Task<string> AcquireTokenCoreAsync(bool write, SignInObservation? expected, CancellationToken ct)
    {
        await _operations.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var accounts = (await _application.GetAccountsAsync()).ToArray();
            if (accounts.Length != 1)
            {
                throw new InvalidOperationException("Exactly one cached account is required. Clear cache and sign in explicitly.");
            }
            var result = await _application.AcquireTokenSilent(Scopes(write), accounts[0]).ExecuteAsync(ct);
            Validate(result);
            if (expected is not null && (!string.Equals(result.UniqueId, expected.MailboxObjectId, StringComparison.OrdinalIgnoreCase)
                || result.Account.HomeAccountId.Identifier != expected.HomeAccountId))
            {
                throw new InvalidOperationException("The cached account changed. Sign in again.");
            }
            return result.AccessToken;
        }
        finally { _operations.Release(); }
    }

    public async Task ClearAsync()
    {
        await _operations.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var account in await _application.GetAccountsAsync())
            {
                await _application.RemoveAsync(account);
            }
            if (_persistentCache) { File.Delete(_cachePath); }
        }
        finally { _operations.Release(); }
    }

    private void Validate(AuthenticationResult result)
    {
        if (!string.Equals(result.TenantId, _options.TenantId, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(result.UniqueId, out _)
            || (!string.IsNullOrEmpty(_options.MailboxObjectId)
                && !string.Equals(result.UniqueId, _options.MailboxObjectId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Unexpected tenant or account. No Graph request is permitted.");
        }
    }

    private static void VerifyWindowsProtection(string directory)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        var path = Path.Combine(directory, "protection-check.dpapi");
        var sample = RandomNumberGenerator.GetBytes(32);
        try
        {
            SaveProtected(path, sample);
            var restored = Unprotect(File.ReadAllBytes(path));
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(sample, restored))
                {
                    throw new CryptographicException("Protected persistence verification failed.");
                }
            }
            finally { CryptographicOperations.ZeroMemory(restored); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sample);
            File.Delete(path);
        }
    }

    private static void SaveProtected(string path, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(protectedBytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    private static byte[] Unprotect(byte[] bytes)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException(); }
        return ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
    }

    public void Dispose()
    {
        _operations.Wait();
        try
        {
            if (!_disposed) { _cacheLock?.Dispose(); _disposed = true; }
        }
        finally { _operations.Release(); }
    }
}
