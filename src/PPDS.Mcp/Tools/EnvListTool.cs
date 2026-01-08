using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PPDS.Auth.Discovery;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists available Dataverse environments.
/// </summary>
[McpServerToolType]
public sealed class EnvListTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public EnvListTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Lists available Dataverse environments for the current profile.
    /// </summary>
    /// <param name="filter">Optional filter to search by name, URL, or ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available environments.</returns>
    [McpServerTool(Name = "ppds_env_list")]
    [Description("List available Dataverse environments. Use this to discover which environments are accessible with the current authentication profile. The currently selected environment is marked with isActive=true.")]
    public async Task<EnvListResult> ExecuteAsync(
        [Description("Optional filter to search environments by name, URL, or ID")]
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        var profile = await _context.GetActiveProfileAsync(cancellationToken).ConfigureAwait(false);

        using var gds = GlobalDiscoveryService.FromProfile(profile);
        var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken).ConfigureAwait(false);

        // Apply filter if provided.
        IReadOnlyList<DiscoveredEnvironment> filtered = environments;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filtered = environments.Where(e =>
                e.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.UniqueName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.ApiUrl.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (e.EnvironmentId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }

        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();

        return new EnvListResult
        {
            Filter = filter,
            Environments = filtered.Select(e => new EnvironmentInfo
            {
                Id = e.Id,
                EnvironmentId = e.EnvironmentId,
                FriendlyName = e.FriendlyName,
                UniqueName = e.UniqueName,
                ApiUrl = e.ApiUrl,
                Url = e.Url,
                Type = e.EnvironmentType,
                State = e.IsEnabled ? "Enabled" : "Disabled",
                Region = e.Region,
                Version = e.Version,
                IsActive = selectedUrl != null &&
                    e.ApiUrl.TrimEnd('/').ToLowerInvariant() == selectedUrl
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the env_list tool.
/// </summary>
public sealed class EnvListResult
{
    /// <summary>
    /// Filter that was applied, if any.
    /// </summary>
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filter { get; set; }

    /// <summary>
    /// List of discovered environments.
    /// </summary>
    [JsonPropertyName("environments")]
    public List<EnvironmentInfo> Environments { get; set; } = [];
}

/// <summary>
/// Information about a discovered environment.
/// </summary>
public sealed class EnvironmentInfo
{
    /// <summary>
    /// Organization ID (GUID).
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Power Platform environment ID.
    /// </summary>
    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// Human-readable name.
    /// </summary>
    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    /// <summary>
    /// Unique technical name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// Dataverse API URL (use this for ppds_env_select).
    /// </summary>
    [JsonPropertyName("apiUrl")]
    public string ApiUrl { get; set; } = "";

    /// <summary>
    /// Web application URL.
    /// </summary>
    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    /// <summary>
    /// Environment type (Production, Sandbox, Developer, Trial).
    /// </summary>
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// Environment state (Enabled/Disabled).
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>
    /// Geographic region.
    /// </summary>
    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }

    /// <summary>
    /// Dataverse version.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// Whether this is the currently selected environment.
    /// </summary>
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
