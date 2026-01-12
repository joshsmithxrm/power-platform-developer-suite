using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that selects a Dataverse environment.
/// </summary>
[McpServerToolType]
public sealed class EnvSelectTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvSelectTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public EnvSelectTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Selects a Dataverse environment for subsequent queries.
    /// </summary>
    /// <param name="environment">Environment URL, display name, or unique name to select.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Details of the selected environment.</returns>
    [McpServerTool(Name = "ppds_env_select")]
    [Description("Select a Dataverse environment for subsequent queries. You can specify the environment by URL (e.g., 'https://contoso.crm.dynamics.com'), display name (e.g., 'Contoso Production'), or unique name. All subsequent ppds tools will use this environment.")]
    public async Task<EnvSelectResult> ExecuteAsync(
        [Description("Environment URL, display name, or unique name")]
        string environment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new ArgumentException("The 'environment' parameter is required.", nameof(environment));
        }

        var collection = await _context.GetProfileCollectionAsync(cancellationToken).ConfigureAwait(false);

        var profile = collection.ActiveProfile
            ?? throw new InvalidOperationException("No active profile configured. Run 'ppds auth create' first.");

        // Use multi-layer resolution.
        using var credentialStore = new NativeCredentialStore();
        using var resolver = new EnvironmentResolutionService(profile, credentialStore: credentialStore);
        var result = await resolver.ResolveAsync(environment, cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                result.ErrorMessage ?? $"Environment '{environment}' not found. Use ppds_env_list to see available environments.");
        }

        var resolved = result.Environment!;

        // Invalidate cached pool for the old environment.
        if (profile.Environment != null)
        {
            _context.InvalidateEnvironment(profile.Environment.Url);
        }

        // Update profile with new environment.
        profile.Environment = resolved;
        await _context.SaveProfileCollectionAsync(collection, cancellationToken).ConfigureAwait(false);

        return new EnvSelectResult
        {
            Url = resolved.Url,
            DisplayName = resolved.DisplayName,
            UniqueName = resolved.UniqueName,
            EnvironmentId = resolved.EnvironmentId,
            ResolutionMethod = result.Method.ToString()
        };
    }
}

/// <summary>
/// Result of the env_select tool.
/// </summary>
public sealed class EnvSelectResult
{
    /// <summary>
    /// Dataverse API URL of the selected environment.
    /// </summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>
    /// Human-readable display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Unique technical name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    /// <summary>
    /// Power Platform environment ID.
    /// </summary>
    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }

    /// <summary>
    /// How the environment was resolved (Direct, Discovery, Api).
    /// </summary>
    [JsonPropertyName("resolutionMethod")]
    public string ResolutionMethod { get; set; } = "";
}
