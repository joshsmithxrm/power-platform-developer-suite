using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Mcp.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that updates an existing column (attribute) on a Dataverse table.
/// </summary>
[McpServerToolType]
public sealed class MetadataUpdateColumnTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataUpdateColumnTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataUpdateColumnTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Updates an existing column (attribute) on a Dataverse table.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the table.</param>
    /// <param name="entityName">The logical name of the entity containing the column.</param>
    /// <param name="columnName">The logical name of the column to update.</param>
    /// <param name="displayName">The updated display name.</param>
    /// <param name="description">The updated description.</param>
    /// <param name="requiredLevel">The updated requirement level: 'None', 'Recommended', or 'Required'.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    [McpServerTool(Name = "ppds_metadata_update_column")]
    [Description("Updates an existing column (attribute) on a Dataverse table. Only the provided optional fields are changed; omitted fields remain unchanged. Supports dry-run mode.")]
    public async Task<MetadataUpdateColumnResult> ExecuteAsync(
        [Description("Unique name of the solution containing the table.")] string solution,
        [Description("Logical name of the entity containing the column.")] string entityName,
        [Description("Logical name of the column to update.")] string columnName,
        [Description("Updated display name (optional).")] string? displayName = null,
        [Description("Updated description (optional).")] string? description = null,
        [Description("Updated requirement level: 'None', 'Recommended', or 'Required' (optional).")] string? requiredLevel = null,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Context.IsReadOnly)
                throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

            await using var serviceProvider = await CreateScopeAsync(cancellationToken,
                (nameof(solution), solution),
                (nameof(entityName), entityName),
                (nameof(columnName), columnName)).ConfigureAwait(false);

            var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            await service.UpdateColumnAsync(new UpdateColumnRequest
            {
                SolutionUniqueName = solution,
                EntityLogicalName = entityName,
                ColumnLogicalName = columnName,
                DisplayName = displayName,
                Description = description,
                RequiredLevel = requiredLevel,
                DryRun = dryRun
            }, ct: cancellationToken).ConfigureAwait(false);

            return new MetadataUpdateColumnResult
            {
                Success = true,
                WasDryRun = dryRun
            };
        }
        catch (PpdsException ex)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not ArgumentException)
        {
            McpToolErrorHelper.ThrowStructuredError(ex);
            throw; // unreachable — ThrowStructuredError always throws
        }
    }
}

/// <summary>
/// Result of the metadata_update_column tool.
/// </summary>
public sealed class MetadataUpdateColumnResult
{
    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }
}
