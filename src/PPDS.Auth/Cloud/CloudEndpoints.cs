using System;
using Azure.Identity;
using Microsoft.Identity.Client;

namespace PPDS.Auth.Cloud;

/// <summary>
/// Provides endpoint URLs for different Azure cloud environments.
/// </summary>
public static class CloudEndpoints
{
    /// <summary>
    /// Gets the Azure AD authority URL for the specified cloud environment.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <param name="tenantId">Optional tenant ID. Defaults to "organizations" for multi-tenant.</param>
    /// <returns>The authority URL.</returns>
    public static string GetAuthorityUrl(CloudEnvironment cloud, string? tenantId = null)
    {
        var tenant = string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId;
        var baseUrl = GetAuthorityBaseUrl(cloud);
        return $"{baseUrl}/{tenant}";
    }

    /// <summary>
    /// Gets the base authority URL (without tenant) for the specified cloud environment.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <returns>The base authority URL.</returns>
    public static string GetAuthorityBaseUrl(CloudEnvironment cloud)
    {
        return cloud switch
        {
            CloudEnvironment.Public => "https://login.microsoftonline.com",
            CloudEnvironment.UsGov => "https://login.microsoftonline.us",
            CloudEnvironment.UsGovHigh => "https://login.microsoftonline.us",
            CloudEnvironment.UsGovDod => "https://login.microsoftonline.us",
            CloudEnvironment.China => "https://login.chinacloudapi.cn",
            _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown cloud environment")
        };
    }

    /// <summary>
    /// Gets the MSAL Azure cloud instance for the specified cloud environment.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <returns>The Azure cloud instance.</returns>
    public static AzureCloudInstance GetAzureCloudInstance(CloudEnvironment cloud)
    {
        return cloud switch
        {
            CloudEnvironment.Public => AzureCloudInstance.AzurePublic,
            CloudEnvironment.UsGov => AzureCloudInstance.AzureUsGovernment,
            CloudEnvironment.UsGovHigh => AzureCloudInstance.AzureUsGovernment,
            CloudEnvironment.UsGovDod => AzureCloudInstance.AzureUsGovernment,
            CloudEnvironment.China => AzureCloudInstance.AzureChina,
            _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown cloud environment")
        };
    }

    /// <summary>
    /// Gets the Global Discovery Service URL for the specified cloud environment.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <returns>The Global Discovery Service URL.</returns>
    public static string GetGlobalDiscoveryUrl(CloudEnvironment cloud)
    {
        return cloud switch
        {
            CloudEnvironment.Public => "https://globaldisco.crm.dynamics.com",
            CloudEnvironment.UsGov => "https://globaldisco.crm9.dynamics.com",
            CloudEnvironment.UsGovHigh => "https://globaldisco.crm.microsoftdynamics.us",
            CloudEnvironment.UsGovDod => "https://globaldisco.crm.appsplatform.us",
            CloudEnvironment.China => "https://globaldisco.crm.dynamics.cn",
            _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown cloud environment")
        };
    }

    /// <summary>
    /// Gets the Azure.Identity authority host URI for the specified cloud environment.
    /// </summary>
    /// <param name="cloud">The cloud environment.</param>
    /// <returns>The authority host URI.</returns>
    public static Uri GetAuthorityHost(CloudEnvironment cloud)
    {
        return cloud switch
        {
            CloudEnvironment.Public => AzureAuthorityHosts.AzurePublicCloud,
            CloudEnvironment.UsGov => AzureAuthorityHosts.AzureGovernment,
            CloudEnvironment.UsGovHigh => AzureAuthorityHosts.AzureGovernment,
            CloudEnvironment.UsGovDod => AzureAuthorityHosts.AzureGovernment,
            CloudEnvironment.China => AzureAuthorityHosts.AzureChina,
            _ => throw new ArgumentOutOfRangeException(nameof(cloud), cloud, "Unknown cloud environment")
        };
    }

    /// <summary>
    /// Parses a cloud environment from a string value.
    /// </summary>
    /// <param name="value">The string value (case-insensitive).</param>
    /// <returns>The cloud environment.</returns>
    /// <exception cref="ArgumentException">If the value is not a valid cloud environment.</exception>
    public static CloudEnvironment Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return CloudEnvironment.Public;
        }

        return value.ToUpperInvariant() switch
        {
            "PUBLIC" => CloudEnvironment.Public,
            "USGOV" => CloudEnvironment.UsGov,
            "USGOVHIGH" => CloudEnvironment.UsGovHigh,
            "USGOVDOD" => CloudEnvironment.UsGovDod,
            "CHINA" => CloudEnvironment.China,
            _ => throw new ArgumentException($"Unknown cloud environment: {value}. Valid values: Public, UsGov, UsGovHigh, UsGovDod, China", nameof(value))
        };
    }
}
