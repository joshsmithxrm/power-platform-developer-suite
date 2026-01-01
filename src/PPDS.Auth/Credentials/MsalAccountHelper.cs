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
        // Best case: we have the exact account identifier stored
        if (!string.IsNullOrEmpty(homeAccountId))
        {
            var account = await msalClient.GetAccountAsync(homeAccountId).ConfigureAwait(false);
            if (account != null)
                return account;
        }

        // Fall back to filtering accounts
        var accounts = await msalClient.GetAccountsAsync().ConfigureAwait(false);
        var accountList = accounts.ToList();

        if (accountList.Count == 0)
            return null;

        // If we have a tenant ID, filter by it to avoid cross-tenant token usage
        if (!string.IsNullOrEmpty(tenantId))
        {
            var tenantAccount = accountList.FirstOrDefault(a =>
                string.Equals(a.HomeAccountId?.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
            if (tenantAccount != null)
                return tenantAccount;
        }

        // Fall back to username match
        if (!string.IsNullOrEmpty(username))
        {
            var usernameAccount = accountList.FirstOrDefault(a =>
                string.Equals(a.Username, username, StringComparison.OrdinalIgnoreCase));
            if (usernameAccount != null)
                return usernameAccount;
        }

        // If we can't find the right account, return null to force re-authentication.
        // Never silently use a random cached account - that causes cross-tenant issues.
        return null;
    }
}
