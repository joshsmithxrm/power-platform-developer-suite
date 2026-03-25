using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that analyzes connection references for orphaned references and flows.
/// </summary>
[McpServerToolType]
public sealed class ConnectionReferencesAnalyzeTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionReferencesAnalyzeTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public ConnectionReferencesAnalyzeTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Analyzes connection references for orphaned references and orphaned flows.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Analysis results with orphan detection.</returns>
    [McpServerTool(Name = "ppds_connection_references_analyze")]
    [Description("Analyze connection references for orphaned references (not used by any flow) and orphaned flows (referencing missing connection references). Useful for deployment cleanup.")]
    public async Task<ConnectionReferencesAnalyzeResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IConnectionReferenceService>();

        var analysis = await service.AnalyzeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        var orphanedReferences = analysis.Relationships
            .Where(r => r.Type == RelationshipType.OrphanedConnectionReference)
            .Select(r => new OrphanedConnectionReferenceSummary
            {
                LogicalName = r.ConnectionReferenceLogicalName ?? "",
                DisplayName = r.ConnectionReferenceDisplayName,
                ConnectorId = r.ConnectorId,
                IsBound = r.IsBound
            })
            .ToList();

        var orphanedFlows = analysis.Relationships
            .Where(r => r.Type == RelationshipType.OrphanedFlow)
            .Select(r => new OrphanedFlowSummary
            {
                UniqueName = r.FlowUniqueName ?? "",
                DisplayName = r.FlowDisplayName,
                MissingConnectionReferenceLogicalName = r.ConnectionReferenceLogicalName
            })
            .ToList();

        return new ConnectionReferencesAnalyzeResult
        {
            OrphanedReferences = orphanedReferences,
            OrphanedFlows = orphanedFlows,
            TotalRelationships = analysis.Relationships.Count,
            ValidRelationships = analysis.ValidCount,
            OrphanedReferenceCount = analysis.OrphanedConnectionReferenceCount,
            OrphanedFlowCount = analysis.OrphanedFlowCount,
            HasOrphans = analysis.HasOrphans
        };
    }
}

/// <summary>
/// Result of the connection_references_analyze tool.
/// </summary>
public sealed class ConnectionReferencesAnalyzeResult
{
    /// <summary>
    /// Orphaned connection references (not used by any flow).
    /// </summary>
    [JsonPropertyName("orphanedReferences")]
    public List<OrphanedConnectionReferenceSummary> OrphanedReferences { get; set; } = [];

    /// <summary>
    /// Orphaned flows (referencing missing connection references).
    /// </summary>
    [JsonPropertyName("orphanedFlows")]
    public List<OrphanedFlowSummary> OrphanedFlows { get; set; } = [];

    /// <summary>
    /// Total number of relationships analyzed.
    /// </summary>
    [JsonPropertyName("totalRelationships")]
    public int TotalRelationships { get; set; }

    /// <summary>
    /// Number of valid flow-to-connection-reference relationships.
    /// </summary>
    [JsonPropertyName("validRelationships")]
    public int ValidRelationships { get; set; }

    /// <summary>
    /// Number of orphaned connection references.
    /// </summary>
    [JsonPropertyName("orphanedReferenceCount")]
    public int OrphanedReferenceCount { get; set; }

    /// <summary>
    /// Number of orphaned flows.
    /// </summary>
    [JsonPropertyName("orphanedFlowCount")]
    public int OrphanedFlowCount { get; set; }

    /// <summary>
    /// Whether any orphans were detected.
    /// </summary>
    [JsonPropertyName("hasOrphans")]
    public bool HasOrphans { get; set; }
}

/// <summary>
/// An orphaned connection reference (not used by any flow).
/// </summary>
public sealed class OrphanedConnectionReferenceSummary
{
    /// <summary>
    /// The connection reference logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>
    /// The display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The connector ID.
    /// </summary>
    [JsonPropertyName("connectorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorId { get; set; }

    /// <summary>
    /// Whether the connection reference is bound to a connection.
    /// </summary>
    [JsonPropertyName("isBound")]
    public bool? IsBound { get; set; }
}

/// <summary>
/// An orphaned flow (referencing a missing connection reference).
/// </summary>
public sealed class OrphanedFlowSummary
{
    /// <summary>
    /// The flow unique name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// The flow display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The logical name of the missing connection reference.
    /// </summary>
    [JsonPropertyName("missingConnectionReferenceLogicalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingConnectionReferenceLogicalName { get; set; }
}
