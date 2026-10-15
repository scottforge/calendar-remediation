using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace CalendarRemediation.Host.Authentication;

public sealed record TenantSignInOptions(string TenantId, string ClientId, string MailboxObjectId, string CacheDirectory);
public sealed record SignInObservation(string TenantId, string ClientId, string MailboxObjectId, string HomeAccountId,
    DateTimeOffset At, string AuthenticationPath, string CacheProtection, string[] Scopes);

// Windows-only persistence is intentional. There is no plaintext or in-memory fallback.
// Holding the lock for the runner lifetime also prevents competing cache writers.
public sealed class TenantSignIn : IDisposable
{
    private readonly TenantSignInOptions _options;
    private readonly IPublicClientApplication _application;
    private readonly FileStream _cacheLock;
    private readonly string _cachePath;
    private bool _disposed;

    public TenantSignIn(TenantSignInOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Live sign-in requires tested Windows CurrentUser DPAPI. No unprotected fallback exists.");
        }
        if (!Guid.TryParse(options.TenantId, out _) || !Guid.TryParse(options.ClientId, out _)
            || !Guid.TryParse(options.MailboxObjectId, out _))
        {
            throw new ArgumentException("Tenant, public client and expected mailbox object IDs must be GUIDs.", nameof(options));
        }
        _options = options;
        Directory.CreateDirectory(options.CacheDirectory);
        _cachePath = Path.Combine(options.CacheDirectory, "msal.dpapi");
        _cacheLock = new FileStream(Path.Combine(options.CacheDirectory, "cache.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            VerifyProtection(options.CacheDirectory);
            _application = PublicClientApplicationBuilder.Create(options.ClientId)
                .WithAuthority($"https://login.microsoftonline.com/{options.TenantId}")
                .WithRedirectUri("http://localhost")
                .Build();
            _application.UserTokenCache.SetBeforeAccess(args =>
            {
                if (File.Exists(_cachePath))
                {
                    var bytes = Unprotect(File.ReadAllBytes(_cachePath));
                    try { args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
            });
            _application.UserTokenCache.SetAfterAccess(args =>
            {
                if (args.HasStateChanged)
                {
                    var bytes = args.TokenCache.SerializeMsalV3();
                    try { SaveProtected(_cachePath, bytes); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
            });
        }
        catch
        {
            _cacheLock.Dispose();
            throw;
        }
    }

    public static string[] Scopes(bool write) => [write ? "Calendars.ReadWrite" : "Calendars.Read", "User.Read"];

    public async Task<SignInObservation> SignInAsync(bool write, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await _application.AcquireTokenInteractive(Scopes(write)).WithUseEmbeddedWebView(false)
            .WithPrompt(Prompt.SelectAccount).ExecuteAsync(ct);
        try { Validate(result); }
        catch
        {
            await _application.RemoveAsync(result.Account);
            throw;
        }
        return new(result.TenantId, _options.ClientId, result.UniqueId, result.Account.HomeAccountId.Identifier, DateTimeOffset.UtcNow,
            "MSAL public client, system browser authorization code with PKCE and loopback callback",
            "Windows DPAPI CurrentUser, persisted round-trip verified, exclusive cache lock", Scopes(write));
    }

    public async Task<string> AcquireTokenAsync(bool write, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var accounts = (await _application.GetAccountsAsync()).ToArray();
        if (accounts.Length != 1)
        {
            throw new InvalidOperationException("Exactly one cached account is required. Clear cache and sign in explicitly.");
        }
        var result = await _application.AcquireTokenSilent(Scopes(write), accounts[0]).ExecuteAsync(ct);
        Validate(result);
        return result.AccessToken;
    }

    public async Task ClearAsync()
    {
        foreach (var account in await _application.GetAccountsAsync())
        {
            await _application.RemoveAsync(account);
        }
        File.Delete(_cachePath);
    }

    private void Validate(AuthenticationResult result)
    {
        if (!string.Equals(result.TenantId, _options.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.UniqueId, _options.MailboxObjectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected tenant or account. No Graph request is permitted.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyProtection(string directory)
    {
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
        if (!_disposed) { _cacheLock.Dispose(); _disposed = true; }
    }
}
