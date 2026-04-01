using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that updates an existing Dataverse table (entity).
/// </summary>
[McpServerToolType]
public sealed class MetadataUpdateTableTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataUpdateTableTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataUpdateTableTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Updates an existing Dataverse table (entity) in the specified solution.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the table.</param>
    /// <param name="entityName">The logical name of the entity to update.</param>
    /// <param name="displayName">The updated display name.</param>
    /// <param name="pluralName">The updated plural display name.</param>
    /// <param name="description">The updated description.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    [McpServerTool(Name = "ppds_metadata_update_table")]
    [Description("Updates an existing Dataverse table (entity). Only the provided optional fields are changed; omitted fields remain unchanged. Supports dry-run mode.")]
    public async Task<MetadataUpdateTableResult> ExecuteAsync(
        [Description("Unique name of the solution containing the table.")] string solution,
        [Description("Logical name of the entity to update.")] string entityName,
        [Description("Updated display name (optional).")] string? displayName = null,
        [Description("Updated plural display name (optional).")] string? pluralName = null,
        [Description("Updated description (optional).")] string? description = null,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken,
            (nameof(solution), solution),
            (nameof(entityName), entityName)).ConfigureAwait(false);

        var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

        await service.UpdateTableAsync(new UpdateTableRequest
        {
            SolutionUniqueName = solution,
            EntityLogicalName = entityName,
            DisplayName = displayName,
            PluralDisplayName = pluralName,
            Description = description,
            DryRun = dryRun
        }, ct: cancellationToken).ConfigureAwait(false);

        return new MetadataUpdateTableResult
        {
            Success = true,
            WasDryRun = dryRun
        };
    }
}

/// <summary>
/// Result of the metadata_update_table tool.
/// </summary>
public sealed class MetadataUpdateTableResult
{
    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
