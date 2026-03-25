using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets full details of a specific connection reference.
/// </summary>
[McpServerToolType]
public sealed class ConnectionReferencesGetTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionReferencesGetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public ConnectionReferencesGetTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Gets full details of a specific connection reference including dependent flows.
    /// </summary>
    /// <param name="logicalName">The logical name of the connection reference.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection reference details with dependent flows.</returns>
    [McpServerTool(Name = "ppds_connection_references_get")]
    [Description("Get full details of a specific connection reference including dependent flows and connection info. Use the logicalName from ppds_connection_references_list.")]
    public async Task<ConnectionReferencesGetResult> ExecuteAsync(
        [Description("Logical name of the connection reference")]
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken, (nameof(logicalName), logicalName)).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IConnectionReferenceService>();

        var reference = await service.GetAsync(logicalName, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Connection reference '{logicalName}' not found.");

        var flows = await service.GetFlowsUsingAsync(logicalName, cancellationToken).ConfigureAwait(false);

        return new ConnectionReferencesGetResult
        {
            LogicalName = reference.LogicalName,
            DisplayName = reference.DisplayName,
            Description = reference.Description,
            ConnectorId = reference.ConnectorId,
            ConnectionId = reference.ConnectionId,
            IsManaged = reference.IsManaged,
            IsBound = reference.IsBound,
            ConnectionStatus = "N/A",
            ConnectorDisplayName = null,
            CreatedOn = reference.CreatedOn?.ToString("o"),
            ModifiedOn = reference.ModifiedOn?.ToString("o"),
            Flows = flows.Select(f => new ConnectionReferenceFlowSummary
            {
                UniqueName = f.UniqueName,
                DisplayName = f.DisplayName,
                State = f.State.ToString(),
                Category = f.Category.ToString()
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the connection_references_get tool.
/// </summary>
public sealed class ConnectionReferencesGetResult
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
    /// The description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// The connector ID.
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

    /// <summary>
    /// When the connection reference was created (ISO 8601).
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    /// <summary>
    /// When the connection reference was last modified (ISO 8601).
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    /// <summary>
    /// Flows that depend on this connection reference.
    /// </summary>
    [JsonPropertyName("flows")]
    public List<ConnectionReferenceFlowSummary> Flows { get; set; } = [];
}

/// <summary>
/// Summary of a flow that uses a connection reference.
/// </summary>
public sealed class ConnectionReferenceFlowSummary
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
    /// The flow state (Draft, Activated, Suspended).
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    /// <summary>
    /// The flow category (ModernFlow, DesktopFlow).
    /// </summary>
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";
}
