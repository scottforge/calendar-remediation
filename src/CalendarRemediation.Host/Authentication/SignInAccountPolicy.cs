using Microsoft.Identity.Client;

namespace CalendarRemediation.Host.Authentication;

internal static class SignInAccountPolicy
{
    internal static void ValidateSilentResult(TenantSignInOptions options, AuthenticationResult result, IAccount selected,
        string tenant, SignInObservation? expected = null)
    {
        ValidateResult(options, result, expected);
        if (result.Account.HomeAccountId.Identifier != selected.HomeAccountId.Identifier
            || !string.Equals(result.TenantId, tenant, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The cached account changed. Sign in again.");
        }
    }

    internal static string SilentTenant(TenantSignInOptions options, IAccount account, SignInObservation? expected)
    {
        if (string.IsNullOrWhiteSpace(account.HomeAccountId?.Identifier)
            || (expected is not null && account.HomeAccountId.Identifier != expected.HomeAccountId))
        {
            throw new InvalidOperationException("The cached account changed. Sign in again.");
        }
        var tenant = expected?.TenantId ?? CachedTenant(options, account.GetTenantProfiles()?.Select(profile => profile.TenantId) ?? []);
        if (!AllowedTenant(options, tenant))
        {
            throw new InvalidOperationException("Unexpected tenant or account. No Graph request is permitted.");
        }
        return tenant;
    }

    internal static string CachedTenant(TenantSignInOptions options, IEnumerable<string> profileTenants)
    {
        if (!options.AllowPersonalAccounts) { return options.TenantId; }
        // Home tenant alone cannot distinguish a consumer mailbox from that account's organization guest profile.
        var tenants = profileTenants.Where(tenant => AllowedTenant(options, tenant)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (tenants.Length != 1) { throw new InvalidOperationException("Choose an account using browser sign-in."); }
        return tenants[0];
    }

    private static bool AllowedTenant(TenantSignInOptions options, string tenant) => Guid.TryParse(tenant, out _)
        && (string.Equals(tenant, TenantSignIn.ConsumerTenantId, StringComparison.OrdinalIgnoreCase)
            ? options.AllowPersonalAccounts
            : string.Equals(tenant, options.TenantId, StringComparison.OrdinalIgnoreCase));

    internal static void ValidateResult(TenantSignInOptions options, AuthenticationResult result, SignInObservation? expected = null)
    {
        var consumer = string.Equals(result.TenantId, TenantSignIn.ConsumerTenantId, StringComparison.OrdinalIgnoreCase);
        var comparison = consumer ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!AllowedTenant(options, result.TenantId)
            || string.IsNullOrWhiteSpace(result.UniqueId) || string.IsNullOrWhiteSpace(result.Account?.HomeAccountId?.Identifier)
            || (!consumer && !Guid.TryParse(result.UniqueId, out _))
            || (!string.IsNullOrEmpty(options.MailboxObjectId)
                && (consumer || !string.Equals(result.UniqueId, options.MailboxObjectId, comparison))))
        {
            throw new InvalidOperationException("Unexpected tenant or account. No Graph request is permitted.");
        }
        if (expected is not null && (!string.Equals(result.TenantId, expected.TenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(options.ClientId, expected.ClientId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(result.UniqueId, expected.MailboxObjectId, comparison)
            || result.Account.HomeAccountId.Identifier != expected.HomeAccountId))
        {
            throw new InvalidOperationException("The cached account changed. Sign in again.");
        }
    }
}
