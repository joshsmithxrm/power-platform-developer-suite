using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that updates an existing global choice (option set) in Dataverse.
/// </summary>
[McpServerToolType]
public sealed class MetadataUpdateChoiceTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataUpdateChoiceTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataUpdateChoiceTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Updates an existing global choice (option set) in Dataverse.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the global choice.</param>
    /// <param name="name">The name of the global choice to update.</param>
    /// <param name="displayName">The updated display name.</param>
    /// <param name="description">The updated description.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    [McpServerTool(Name = "ppds_metadata_update_choice")]
    [Description("Updates an existing global choice (option set). Only the provided optional fields are changed; omitted fields remain unchanged. Supports dry-run mode.")]
    public async Task<MetadataUpdateChoiceResult> ExecuteAsync(
        [Description("Unique name of the solution containing the global choice.")] string solution,
        [Description("Name of the global choice to update.")] string name,
        [Description("Updated display name (optional).")] string? displayName = null,
        [Description("Updated description (optional).")] string? description = null,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        if (Context.IsReadOnly)
            throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

        await using var serviceProvider = await CreateScopeAsync(cancellationToken,
            (nameof(solution), solution),
            (nameof(name), name)).ConfigureAwait(false);

        var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

        await service.UpdateGlobalChoiceAsync(new UpdateGlobalChoiceRequest
        {
            SolutionUniqueName = solution,
            Name = name,
            DisplayName = displayName,
            Description = description,
            DryRun = dryRun
        }, ct: cancellationToken).ConfigureAwait(false);

        return new MetadataUpdateChoiceResult
        {
            Success = true,
            WasDryRun = dryRun
        };
    }
}

/// <summary>
/// Result of the metadata_update_choice tool.
/// </summary>
public sealed class MetadataUpdateChoiceResult
{
    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
