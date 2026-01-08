using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Identity.Client;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Helper class for MSAL account lookup operations.
/// Provides consistent account selection logic across credential providers.
/// </summary>
internal static class MsalAccountHelper
{
    /// <summary>
    /// Finds the correct cached account for a profile.
    /// Uses HomeAccountId for precise lookup, falls back to tenant filtering, then username.
    /// </summary>
    /// <param name="msalClient">The MSAL public client application.</param>
    /// <param name="homeAccountId">Optional MSAL home account identifier for precise lookup.</param>
    /// <param name="tenantId">Optional tenant ID for filtering.</param>
    /// <param name="username">Optional username for filtering.</param>
    /// <returns>The matching account, or null if no match found (forces re-authentication).</returns>
    internal static async Task<IAccount?> FindAccountAsync(
        IPublicClientApplication msalClient,
        string? homeAccountId,
        string? tenantId,
        string? username = null)
    {
        AuthDebugLog.WriteLine($"FindAccountAsync: homeAccountId={!string.IsNullOrEmpty(homeAccountId)}, tenantId={!string.IsNullOrEmpty(tenantId)}, username={!string.IsNullOrEmpty(username)}");

        // Best case: we have the exact account identifier stored
        if (!string.IsNullOrEmpty(homeAccountId))
        {
            AuthDebugLog.WriteLine($"  Attempting HomeAccountId lookup: {homeAccountId}");
            var account = await msalClient.GetAccountAsync(homeAccountId).ConfigureAwait(false);
            if (account != null)
            {
                AuthDebugLog.WriteLine($"  SUCCESS: Found account by HomeAccountId ({account.Username})");
                return account;
            }
            AuthDebugLog.WriteLine("  HomeAccountId lookup returned null - account not in cache");
        }

        // Fall back to filtering accounts
        var accounts = await msalClient.GetAccountsAsync().ConfigureAwait(false);
        var accountList = accounts.ToList();

        AuthDebugLog.WriteLine($"  Account cache contains {accountList.Count} account(s)");

        if (accountList.Count == 0)
        {
            AuthDebugLog.WriteLine("  FAIL: No accounts in cache - returning null (will force re-auth)");
            return null;
        }

        // If we have a tenant ID, filter by it to avoid cross-tenant token usage
        if (!string.IsNullOrEmpty(tenantId))
        {
            AuthDebugLog.WriteLine($"  Attempting TenantId lookup: {tenantId}");
            var tenantAccount = accountList.FirstOrDefault(a =>
                string.Equals(a.HomeAccountId?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
            if (tenantAccount != null)
            {
                AuthDebugLog.WriteLine($"  SUCCESS: Found account by TenantId ({tenantAccount.Username})");
                return tenantAccount;
            }
            AuthDebugLog.WriteLine("  TenantId lookup found no match");
        }

        // Fall back to username match
        if (!string.IsNullOrEmpty(username))
        {
            AuthDebugLog.WriteLine($"  Attempting Username lookup: {username}");
            var usernameAccount = accountList.FirstOrDefault(a =>
                string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
            if (usernameAccount != null)
            {
                AuthDebugLog.WriteLine($"  SUCCESS: Found account by Username ({usernameAccount.Username})");
                return usernameAccount;
            }
            AuthDebugLog.WriteLine("  Username lookup found no match");
        }

        // If we can't find the right account, return null to force re-authentication.
        // Never silently use a random cached account - that causes cross-tenant issues.
        AuthDebugLog.WriteLine("  FAIL: No matching account found - returning null (will force re-auth)");
        return null;
    }
}
