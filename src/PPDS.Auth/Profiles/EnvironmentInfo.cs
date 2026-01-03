using System;
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Information about a Dataverse environment bound to a profile.
/// </summary>
public sealed class EnvironmentInfo
{
    /// <summary>
    /// Gets or sets the environment URL.
    /// Example: https://orgcabef92d.crm.dynamics.com/
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the display name (friendly name).
    /// Example: PPDS Demo - Dev
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the unique name.
    /// Example: unq3a504f4385d7f01195c7000d3a5cc
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; set; }

    /// <summary>
    /// Gets or sets the organization ID (GUID).
    /// </summary>
    [JsonPropertyName("organizationId")]
    public string? OrganizationId { get; set; }

    /// <summary>
    /// Gets or sets the Power Platform environment ID.
    /// </summary>
    [JsonPropertyName("environmentId")]
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// Gets or sets the environment type.
    /// Example: Sandbox, Production
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the geographic region.
    /// Example: NA, EMEA, APAC
    /// </summary>
    [JsonPropertyName("region")]
    public string? Region { get; set; }

    /// <summary>
    /// Creates a new instance with the minimum required information.
    /// </summary>
    /// <param name="url">The environment URL.</param>
    /// <param name="displayName">The display name.</param>
    /// <returns>A new EnvironmentInfo instance.</returns>
    public static EnvironmentInfo Create(string url, string displayName)
    {
        return new EnvironmentInfo
        {
            Url = url ?? throw new ArgumentNullException(nameof(url)),
            DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName))
        };
    }

    /// <summary>
    /// Returns a string representation of the environment.
    /// </summary>
    public override string ToString()
    {
        return $"{DisplayName} ({Url})";
    }

    /// <summary>
    /// Creates a deep copy of this environment info.
    /// </summary>
    public EnvironmentInfo Clone()
    {
        return new EnvironmentInfo
        {
            Url = Url,
            DisplayName = DisplayName,
            UniqueName = UniqueName,
            OrganizationId = OrganizationId,
            EnvironmentId = EnvironmentId,
            Type = Type,
            Region = Region
        };
    }
}
