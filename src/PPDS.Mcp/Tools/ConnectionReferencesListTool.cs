using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists connection references in the current environment.
/// </summary>
[McpServerToolType]
public sealed class ConnectionReferencesListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionReferencesListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public ConnectionReferencesListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists connection references in the current environment.
    /// </summary>
    /// <param name="solutionId">Optional solution unique name to filter by.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of connection reference summaries.</returns>
    [McpServerTool(Name = "ppds_connection_references_list")]
    [Description("List connection references in the current environment. Shows connector bindings and whether connections are bound. Optionally filter by solution name.")]
    public async Task<ConnectionReferencesListResult> ExecuteAsync(
        [Description("Solution unique name to filter by")]
        string? solutionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IConnectionReferenceService>();

        var result = await service.ListAsync(solutionName: solutionId, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ConnectionReferencesListResult
        {
            TotalCount = result.TotalCount,
            ConnectionReferences = result.Items.Select(r => new ConnectionReferenceSummary
            {
                LogicalName = r.LogicalName,
                DisplayName = r.DisplayName,
                ConnectorId = r.ConnectorId,
                ConnectionId = r.ConnectionId,
                IsManaged = r.IsManaged,
                IsBound = r.IsBound,
                ConnectionStatus = "N/A",
                ConnectorDisplayName = null
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the connection_references_list tool.
/// </summary>
public sealed class ConnectionReferencesListResult
{
    /// <summary>
    /// List of connection reference summaries.
    /// </summary>
    [JsonPropertyName("connectionReferences")]
    public List<ConnectionReferenceSummary> ConnectionReferences { get; set; } = [];

    /// <summary>Total count of records.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Summary information about a connection reference.
/// </summary>
public sealed class ConnectionReferenceSummary
{
    /// <summary>
    /// The connection reference logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    /// <summary>
    /// The display name of the connection reference.
    /// </summary>
    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// The connector ID (e.g., "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps").
    /// </summary>
    [JsonPropertyName("connectorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorId { get; set; }

    /// <summary>
    /// The bound connection ID, or null if unbound.
    /// </summary>
    [JsonPropertyName("connectionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionId { get; set; }

    /// <summary>
    /// Whether this is a managed component.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Whether a connection is bound.
    /// </summary>
    [JsonPropertyName("isBound")]
    public bool IsBound { get; set; }

    /// <summary>
    /// Connection status (Connected, Error, Unknown, or N/A if unavailable).
    /// </summary>
    [JsonPropertyName("connectionStatus")]
    public string ConnectionStatus { get; set; } = "N/A";

    /// <summary>
    /// The connector display name, or null if unavailable.
    /// </summary>
    [JsonPropertyName("connectorDisplayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorDisplayName { get; set; }
}
