using System.Reflection;
using CalendarRemediation.Host.Authentication;
using Microsoft.Identity.Client;

namespace CalendarRemediation.Host.Tests.Authentication;

public sealed class PersonalAccountSignInTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Consumer = "9188040d-6c67-4c5b-b112-36a304b66dad";
    private const string Client = "22222222-2222-2222-2222-222222222222";
    private const string ObjectId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public void SharedDefaultIsTenantPinnedAndLocalConnectionOptsIntoCommon()
    {
        var pinned = new TenantSignInOptions(Tenant, Client, "", "unused");
        Assert.False(pinned.AllowPersonalAccounts);
        var pinnedApp = BuildApplication(pinned);
        Assert.Equal($"https://login.microsoftonline.com/{Tenant}/", pinnedApp.Authority);
        using var local = new LocalIdentityClient(Microsoft.Extensions.Options.Options.Create(
            new LocalConnectionOptions { TenantId = Tenant, ClientId = Client }));
        var localOptions = Assert.IsType<TenantSignInOptions>(typeof(LocalIdentityClient)
            .GetField("_options", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(local));
        Assert.True(localOptions.AllowPersonalAccounts);
        var common = BuildApplication(localOptions);
        Assert.Equal("https://login.microsoftonline.com/common/", common.Authority);
    }

    [Theory]
    [InlineData(Tenant, false, Tenant)]
    [InlineData(Tenant, true, Tenant)]
    [InlineData(Consumer, true, Consumer)]
    public void SilentAuthorityUsesKnownTenantAndRejectsAccountOrTenantSwitch(string homeTenant, bool personal, string tenant)
    {
        var options = Options(personal);
        var account = new TestAccount("selected-home", homeTenant);
        var expected = new SignInObservation(tenant, Client, ObjectId, "selected-home", DateTimeOffset.UnixEpoch, "test", "test", []);
        if (personal) { Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.SilentTenant(options, account, null)); }
        else { Assert.Equal(tenant, SignInAccountPolicy.SilentTenant(options, account, null)); }
        Assert.Equal(tenant, SignInAccountPolicy.SilentTenant(options, account, expected));
        Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.SilentTenant(options, account, expected with { HomeAccountId = "other-home" }));
        Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.SilentTenant(options, account,
            expected with { TenantId = "44444444-4444-4444-4444-444444444444" }));
        if (!personal)
        {
            Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.SilentTenant(options, account, expected with { TenantId = Consumer }));
        }
    }

    [Theory]
    [InlineData(Tenant)]
    [InlineData(Consumer)]
    public void CachedCommonAuthorityRequiresOneKnownTenantProfile(string tenant)
    {
        var options = Options(true);
        Assert.Equal(tenant, SignInAccountPolicy.CachedTenant(options, [tenant]));
        foreach (var profiles in new[] { Array.Empty<string>(), new[] { Tenant, Consumer }, new[] { "44444444-4444-4444-4444-444444444444" } })
        {
            Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.CachedTenant(options, profiles));
        }
    }

    [Theory]
    [InlineData(Tenant, ObjectId, false, true)]
    [InlineData(Consumer, "opaque-msal-subject", true, true)]
    [InlineData(Consumer, "opaque-msal-subject", false, false)]
    [InlineData("44444444-4444-4444-4444-444444444444", ObjectId, true, false)]
    [InlineData("not-a-tenant", ObjectId, true, false)]
    [InlineData(Tenant, "opaque-msal-subject", true, false)]
    [InlineData(Consumer, "", true, false)]
    [InlineData(Consumer, " ", true, false)]
    public void ResultValidationAllowsOnlyConfiguredOrganizationOrOptedInConsumer(string tenant, string uniqueId,
        bool personal, bool accepted)
    {
        var options = Options(personal);
        var result = Result(tenant, uniqueId, "selected-home");
        var error = Record.Exception(() => SignInAccountPolicy.ValidateResult(options, result));
        if (accepted) { Assert.Null(error); }
        else { Assert.IsType<InvalidOperationException>(error); }
    }

    [Fact]
    public void OrganizationExpectedObjectIdStillRestrictsAccount()
    {
        var options = Options(true) with { MailboxObjectId = ObjectId };
        SignInAccountPolicy.ValidateResult(options, Result(Tenant, ObjectId, "selected-home"));
        Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.ValidateResult(options,
            Result(Tenant, "55555555-5555-5555-5555-555555555555", "selected-home")));
        Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.ValidateResult(options, Result(Consumer, "opaque", "selected-home")));
    }

    [Theory]
    [InlineData(Tenant, ObjectId)]
    [InlineData(Consumer, "opaque-msal-subject")]
    public void SilentResultMustRetainTenantUniqueIdClientAndHomeAccount(string tenant, string uniqueId)
    {
        var options = Options(true);
        var result = Result(tenant, uniqueId, "selected-home");
        var expected = new SignInObservation(tenant, Client, uniqueId, "selected-home", DateTimeOffset.UnixEpoch, "test", "test", []);
        SignInAccountPolicy.ValidateResult(options, result, expected);
        foreach (var changed in new[]
        {
            expected with { HomeAccountId = "another-home" },
            expected with { MailboxObjectId = "another-subject" },
            expected with { TenantId = tenant == Tenant ? Consumer : Tenant },
            expected with { ClientId = "55555555-5555-5555-5555-555555555555" }
        })
        {
            Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.ValidateResult(options, result, changed));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void MissingStableHomeAccountIsRejected(string? home)
    {
        Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.ValidateResult(Options(true), Result(Consumer, "opaque", home)));
    }

    [Fact]
    public void SilentResultCannotSwitchSelectedHomeAccountOrRequestedTenantWithoutAnObservation()
    {
        var options = Options(true);
        var selected = new TestAccount("selected-home", Consumer);
        SignInAccountPolicy.ValidateSilentResult(options, Result(Consumer, "opaque", "selected-home"), selected, Consumer, null);
        foreach (var result in new[] { Result(Consumer, "opaque", "other-home"), Result(Tenant, ObjectId, "selected-home") })
        {
            Assert.Throws<InvalidOperationException>(() => SignInAccountPolicy.ValidateSilentResult(options, result, selected, Consumer, null));
        }
    }

    private static TenantSignInOptions Options(bool personal) => new(Tenant, Client, "", "unused", AllowPersonalAccounts: personal);

    // Authority construction stays private because account policy tests do not need an MSAL construction seam.
    private static IPublicClientApplication BuildApplication(TenantSignInOptions options)
    {
        var method = typeof(TenantSignIn).GetMethod("BuildApplication", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return method.CreateDelegate<Func<TenantSignInOptions, IPublicClientApplication>>()(options);
    }

    private static AuthenticationResult Result(string tenant, string uniqueId, string? home) =>
        new("synthetic-token", false, uniqueId, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, tenant,
            new TestAccount(home, tenant), "", TenantSignIn.Scopes(false), Guid.Empty);

    private sealed class TestAccount(string? home, string tenant) : IAccount
    {
        public string Username => "owner@example.test";
        public string Environment => "login.microsoftonline.com";
        public AccountId HomeAccountId => home is null ? null! : new(home, "opaque-home-object", tenant);
    }
}
