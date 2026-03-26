using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;

namespace PPDS.Auth;

/// <summary>
/// Reads authentication configuration from environment variables for CI/CD scenarios.
/// </summary>
public static class EnvironmentVariableAuth
{
    /// <summary>Environment variable name for the application (client) ID.</summary>
    public const string ClientIdVar = "PPDS_CLIENT_ID";

    /// <summary>Environment variable name for the client secret.</summary>
    public const string ClientSecretVar = "PPDS_CLIENT_SECRET";

    /// <summary>Environment variable name for the tenant ID.</summary>
    public const string TenantIdVar = "PPDS_TENANT_ID";

    /// <summary>Environment variable name for the Dataverse environment URL.</summary>
    public const string EnvironmentUrlVar = "PPDS_ENVIRONMENT_URL";

    /// <summary>Environment variable name for the cloud environment (optional, defaults to Public).</summary>
    public const string CloudVar = "PPDS_CLOUD";

    /// <summary>
    /// Returns a synthetic AuthProfile and client secret from environment variables,
    /// or null if none are set.
    /// Throws AuthenticationException if partially configured (1-3 of 4 required vars).
    /// </summary>
    public static (AuthProfile Profile, string ClientSecret)? TryCreateProfile()
    {
        var clientId = GetTrimmed(ClientIdVar);
        var clientSecret = GetTrimmed(ClientSecretVar);
        var tenantId = GetTrimmed(TenantIdVar);
        var environmentUrl = GetTrimmed(EnvironmentUrlVar);

        var vars = new Dictionary<string, string?>
        {
            [ClientIdVar] = clientId,
            [ClientSecretVar] = clientSecret,
            [TenantIdVar] = tenantId,
            [EnvironmentUrlVar] = environmentUrl,
        };

        var setCount = vars.Values.Count(v => v != null);
        if (setCount == 0) return null;

        if (setCount < 4)
        {
            var missing = vars.Where(kv => kv.Value == null).Select(kv => kv.Key);
            throw new AuthenticationException(
                $"Incomplete environment variable auth configuration. " +
                $"Set all four: {ClientIdVar}, {ClientSecretVar}, {TenantIdVar}, {EnvironmentUrlVar}. " +
                $"Missing: {string.Join(", ", missing)}",
                "Auth.IncompleteEnvironmentConfig");
        }

        // Validate URL scheme
        if (!environmentUrl!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new AuthenticationException(
                $"{EnvironmentUrlVar} must be a full URL with https:// scheme. Got: {environmentUrl}",
                "Auth.InvalidEnvironmentUrl");
        }

        // Normalize trailing slash for pool URL matching
        var normalizedUrl = environmentUrl.TrimEnd('/');

        var cloud = ParseCloud(GetTrimmed(CloudVar));

        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = clientId,
            TenantId = tenantId,
            Cloud = cloud,
            Environment = new EnvironmentInfo { Url = normalizedUrl },
            Name = "(env vars)",
        };

        return (profile, clientSecret!);
    }

    private static string? GetTrimmed(string name)
    {
        var value = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static CloudEnvironment ParseCloud(string? value)
    {
        if (value == null) return CloudEnvironment.Public;
        if (Enum.TryParse<CloudEnvironment>(value, ignoreCase: true, out var cloud))
            return cloud;
        throw new AuthenticationException(
            $"{CloudVar} must be one of: Public, UsGov, UsGovHigh, UsGovDod, China. Got: {value}",
            "Auth.InvalidCloud");
    }
}
