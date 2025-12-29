using System;

namespace PPDS.Auth.Discovery;

/// <summary>
/// Represents an environment discovered via the Global Discovery Service.
/// </summary>
public sealed class DiscoveredEnvironment
{
    /// <summary>
    /// Gets or sets the environment ID (OrganizationId).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the Power Platform environment ID.
    /// </summary>
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// Gets or sets the friendly display name.
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique name for the instance.
    /// </summary>
    public string UniqueName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL name (subdomain part).
    /// </summary>
    public string? UrlName { get; set; }

    /// <summary>
    /// Gets or sets the API URL for connecting to Dataverse.
    /// </summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the application URL.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the environment state (0 = enabled, 1 = disabled).
    /// </summary>
    public int State { get; set; }

    /// <summary>
    /// Gets or sets the environment version.
    /// </summary>
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets the region code (e.g., "NAM", "EUR").
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Gets or sets the tenant ID.
    /// </summary>
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Gets or sets the organization type.
    /// </summary>
    public int OrganizationType { get; set; }

    /// <summary>
    /// Gets or sets whether the calling user has system administrator role.
    /// </summary>
    public bool IsUserSysAdmin { get; set; }

    /// <summary>
    /// Gets or sets the trial expiration date (if applicable).
    /// </summary>
    public DateTimeOffset? TrialExpirationDate { get; set; }

    /// <summary>
    /// Gets whether this environment is enabled.
    /// </summary>
    public bool IsEnabled => State == 0;

    /// <summary>
    /// Gets whether this is a trial environment.
    /// </summary>
    public bool IsTrial => TrialExpirationDate.HasValue;

    /// <summary>
    /// Gets the environment type as a string.
    /// Maps from Microsoft.Xrm.Sdk.Organization.OrganizationType enum values.
    /// </summary>
    public string EnvironmentType => OrganizationType switch
    {
        0 => "Production",       // Customer
        5 => "Sandbox",          // CustomerTest
        6 => "Sandbox",          // CustomerFreeTest
        7 => "Preview",          // CustomerPreview
        9 => "TestDrive",        // TestDrive
        11 => "Trial",           // EmailTrial
        12 => "Default",         // Default
        13 => "Developer",       // Developer
        14 => "Trial",           // Trial
        15 => "Teams",           // Teams
        _ => "Production"        // Other types (Monitoring, Support, Backend, etc.) treated as Production
    };

    /// <summary>
    /// Returns a string representation of the environment.
    /// </summary>
    public override string ToString() => $"{FriendlyName} ({UniqueName})";
}
