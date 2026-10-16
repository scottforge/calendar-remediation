using CalendarRemediation.Host.Authentication;

namespace CalendarRemediation.Host.Tests.Feasibility;

public sealed class ProtectedCacheBoundaryTests
{
    [Fact]
    public async Task NonWindowsSessionLifecycleDoesNotCreateCacheDirectory()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var path = Path.Combine(Path.GetTempPath(), "phase0-must-not-create-" + Guid.NewGuid());
        var options = new TenantSignInOptions(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "", path);
        using (var signIn = new TenantSignIn(options))
        {
            Assert.False(Directory.Exists(path));
            Assert.Null(await signIn.GetCachedAccountAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.ContinueAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => signIn.AcquireTokenAsync(false, CancellationToken.None));
            await signIn.ClearAsync();
            Assert.Null(await signIn.GetCachedAccountAsync());
        }
        using (var reopened = new TenantSignIn(options))
        {
            Assert.Null(await reopened.GetCachedAccountAsync());
        }
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task NonWindowsSessionDoesNotRequireUsableCachePath()
    {
        if (OperatingSystem.IsWindows()) { return; }
        using var signIn = new TenantSignIn(new(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "", "invalid\0cache"));
        Assert.Null(await signIn.GetCachedAccountAsync());
        await signIn.ClearAsync();
    }

    [Fact]
    public async Task NonWindowsSessionsIgnoreAndPreserveExistingPersistentCacheFiles()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var path = Path.Combine(Path.GetTempPath(), "phase0-ignore-persisted-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(path);
        var cachePath = Path.Combine(path, "msal.dpapi");
        var lockPath = Path.Combine(path, "cache.lock");
        byte[] sentinel = [0x01, 0x02, 0x03];
        try
        {
            await File.WriteAllBytesAsync(cachePath, sentinel);
            await File.WriteAllBytesAsync(lockPath, sentinel);
            var options = new TenantSignInOptions(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "", path);
            using (var first = new TenantSignIn(options))
            using (var second = new TenantSignIn(options))
            {
                Assert.Null(await first.GetCachedAccountAsync());
                Assert.Null(await second.GetCachedAccountAsync());
                await first.ClearAsync();
                await second.ClearAsync();
            }
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(cachePath));
            Assert.Equal(sentinel, await File.ReadAllBytesAsync(lockPath));
            Assert.Equal(2, Directory.GetFiles(path).Length);
        }
        finally { Directory.Delete(path, recursive: true); }
    }
}
