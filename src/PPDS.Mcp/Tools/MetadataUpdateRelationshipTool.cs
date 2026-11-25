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
    /// <param name="cascadeDelete">New cascade behavior for delete: 'Cascade', 'RemoveLink' or 'Restrict'.</param>
    /// <param name="cascadeAssign">New cascade behavior for assign: 'Active', 'Cascade', 'NoCascade' or 'UserOwned'.</param>
    /// <param name="dryRun">If true, validates the request without persisting changes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result indicating success.</returns>
    [McpServerTool(Name = "ppds_metadata_update_relationship")]
    [Description("Updates an existing one-to-many relationship's cascade configuration. Only the provided cascade settings change; every other setting keeps its current value. The relationship is read and written back whole, so a change made by someone else in between is overwritten (last writer wins). The result lists each setting before and after. Dry-run mode reads the relationship and sends nothing.")]
    public async Task<MetadataUpdateRelationshipResult> ExecuteAsync(
        [Description("Unique name of the solution containing the relationship.")] string solution,
        [Description("Schema name of the relationship to update.")] string schemaName,
        [Description("New cascade behavior for delete: 'Cascade', 'RemoveLink' or 'Restrict' (optional; not given: kept as it is).")] string? cascadeDelete = null,
        [Description("New cascade behavior for assign: 'Active', 'Cascade', 'NoCascade' or 'UserOwned' (optional; not given: kept as it is).")] string? cascadeAssign = null,
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
                cascadeConfig = new CascadeConfigurationDto
                {
                    Delete = ParseCascade(cascadeDelete, "Delete", nameof(cascadeDelete)),
                    Assign = ParseCascade(cascadeAssign, "Assign", nameof(cascadeAssign))
                };
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
                RemovesInheritedAccess = result.RemovesInheritedAccess,
                InheritedAccessNotice = result.RemovesInheritedAccess ? CascadeActionRules.InheritedAccessCleanupNotice : null,
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

    /// <summary>
    /// Parses a cascade value by its name only (any case), and only a value documented as valid for the action; the
    /// error lists the valid values for that action.
    /// </summary>
    private static CascadeBehavior? ParseCascade(string? value, string action, string parameter)
    {
        if (value == null)
            return null;
        var match = CascadeActionRules.ValidFor(action)
            .Where(v => string.Equals(v.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(v => (CascadeBehavior?)v)
            .FirstOrDefault();
        return match ?? throw new ArgumentException(
            $"Invalid cascade {action.ToLowerInvariant()} behavior. Valid values: {CascadeActionRules.Describe(action)}.",
            parameter);
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

    /// <summary>
    /// Whether Reparent or Share changes to NoCascade, which Microsoft documents starts the inherited access rights
    /// cleanup system job.
    /// </summary>
    [JsonPropertyName("removesInheritedAccess")]
    public bool RemovesInheritedAccess { get; set; }

    /// <summary>What that job does, when <see cref="RemovesInheritedAccess"/> is true; otherwise null.</summary>
    [JsonPropertyName("inheritedAccessNotice")]
    public string? InheritedAccessNotice { get; set; }
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
