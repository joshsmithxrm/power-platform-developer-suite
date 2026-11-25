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
/// MCP tool that updates an existing Dataverse relationship.
/// </summary>
[McpServerToolType]
public sealed class MetadataUpdateRelationshipTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MetadataUpdateRelationshipTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public MetadataUpdateRelationshipTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Updates an existing Dataverse relationship's cascade configuration.
    /// </summary>
    /// <param name="solution">The unique name of the solution containing the relationship.</param>
    /// <param name="schemaName">The schema name of the relationship to update.</param>
    /// <param name="cascadeDelete">Cascade behavior for delete: 'Cascade', 'NoCascade', 'RemoveLink', 'Restrict'.</param>
    /// <param name="cascadeAssign">Cascade behavior for assign: 'Cascade', 'NoCascade', 'Active', 'UserOwned'.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    [McpServerTool(Name = "ppds_metadata_update_relationship")]
    [Description("Updates an existing one-to-many relationship's cascade configuration. Only the provided cascade settings change; every other setting keeps its current value. The result lists each setting before and after. Dry-run mode reads the relationship and sends nothing.")]
    public async Task<MetadataUpdateRelationshipResult> ExecuteAsync(
        [Description("Unique name of the solution containing the relationship.")] string solution,
        [Description("Schema name of the relationship to update.")] string schemaName,
        [Description("Cascade behavior for delete: 'Cascade', 'NoCascade', 'RemoveLink', 'Restrict' (optional).")] string? cascadeDelete = null,
        [Description("Cascade behavior for assign: 'Cascade', 'NoCascade', 'Active', 'UserOwned' (optional).")] string? cascadeAssign = null,
        [Description("If true, validates without persisting changes.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (Context.IsReadOnly)
                throw new InvalidOperationException("Cannot modify metadata: this MCP session is read-only.");

            await using var serviceProvider = await CreateScopeAsync(cancellationToken,
                (nameof(solution), solution),
                (nameof(schemaName), schemaName)).ConfigureAwait(false);

            CascadeConfigurationDto? cascadeConfig = null;

            if (cascadeDelete != null || cascadeAssign != null)
            {
                cascadeConfig = new CascadeConfigurationDto();

                if (cascadeDelete != null)
                {
                    if (!Enum.TryParse<CascadeBehavior>(cascadeDelete, ignoreCase: true, out var deleteBehavior))
                    {
                        throw new ArgumentException(
                            $"Invalid cascade delete behavior '{cascadeDelete}'. Valid values: {string.Join(", ", Enum.GetNames<CascadeBehavior>())}",
                            nameof(cascadeDelete));
                    }
                    cascadeConfig.Delete = deleteBehavior;
                }

                if (cascadeAssign != null)
                {
                    if (!Enum.TryParse<CascadeBehavior>(cascadeAssign, ignoreCase: true, out var assignBehavior))
                    {
                        throw new ArgumentException(
                            $"Invalid cascade assign behavior '{cascadeAssign}'. Valid values: {string.Join(", ", Enum.GetNames<CascadeBehavior>())}",
                            nameof(cascadeAssign));
                    }
                    cascadeConfig.Assign = assignBehavior;
                }
            }

            var service = serviceProvider.GetRequiredService<IMetadataAuthoringService>();

            var result = await service.UpdateRelationshipAsync(new UpdateRelationshipRequest
            {
                SolutionUniqueName = solution,
                SchemaName = schemaName,
                CascadeConfiguration = cascadeConfig,
                DryRun = dryRun
            }, ct: cancellationToken).ConfigureAwait(false);

            return new MetadataUpdateRelationshipResult
            {
                Success = true,
                WasDryRun = result.WasDryRun,
                CascadeSettings = result.CascadeSettings.Select(s => new MetadataCascadeSettingChange
                {
                    Setting = s.Setting,
                    Before = s.Before.ToString(),
                    After = s.After.ToString(),
                    Requested = s.Requested,
                    Changed = s.Changed
                }).ToList()
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
/// Result of the metadata_update_relationship tool.
/// </summary>
public sealed class MetadataUpdateRelationshipResult
{
    /// <summary>Whether the operation succeeded.</summary>
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Whether the operation was a dry run.</summary>
    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }

    /// <summary>
    /// The six cascade settings, each with its value before the update and the value sent. A setting not given is
    /// sent as it was read.
    /// </summary>
    [JsonPropertyName("cascadeSettings")]
    public List<MetadataCascadeSettingChange> CascadeSettings { get; set; } = [];
}

/// <summary>
/// One cascade setting of a relationship update.
/// </summary>
public sealed class MetadataCascadeSettingChange
{
    /// <summary>The setting: Assign, Delete, Merge, Reparent, Share or Unshare.</summary>
    [JsonPropertyName("setting")]
    public string Setting { get; set; } = "";

    /// <summary>The value before the update.</summary>
    [JsonPropertyName("before")]
    public string Before { get; set; } = "";

    /// <summary>The value sent.</summary>
    [JsonPropertyName("after")]
    public string After { get; set; } = "";

    /// <summary>Whether the caller gave this setting.</summary>
    [JsonPropertyName("requested")]
    public bool Requested { get; set; }

    /// <summary>Whether the value sent differs from the value before.</summary>
    [JsonPropertyName("changed")]
    public bool Changed { get; set; }
}
