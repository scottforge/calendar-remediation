using CalendarRemediation.Host.Authentication;

namespace CalendarRemediation.Host.Tests.Feasibility;

public sealed class ProtectedCacheBoundaryTests
{
    [Fact]
    public void UnsupportedPlatformCannotFallBackToUnprotectedPersistence()
    {
        if (OperatingSystem.IsWindows()) { return; }
        var path = Path.Combine(Path.GetTempPath(), "phase0-must-not-create-" + Guid.NewGuid());
        Assert.Throws<PlatformNotSupportedException>(() => new TenantSignIn(new(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), path)));
        Assert.False(Directory.Exists(path));
    }
}
