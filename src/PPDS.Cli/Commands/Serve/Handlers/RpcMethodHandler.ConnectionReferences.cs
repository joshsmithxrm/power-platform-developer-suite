using System.Diagnostics;
using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Diagnostics;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Authoring = PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Security;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.DeploymentSettings;
using PPDS.Cli.Services.EnvironmentVariables;
using PPDS.Cli.Services.Flows;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using System.Threading;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;
using PluginAssemblyInfoModel = PPDS.Cli.Plugins.Registration.PluginAssemblyInfo;
using PluginPackageInfoModel = PPDS.Cli.Plugins.Registration.PluginPackageInfo;
using PluginStepInfoModel = PPDS.Cli.Plugins.Registration.PluginStepInfo;
using ConnRefRelationshipType = PPDS.Cli.Services.ConnectionReferences.RelationshipType;
using WebResourceInfoModel = PPDS.Cli.Services.WebResources.WebResourceInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

public partial class RpcMethodHandler
{
    #region Connection References

    /// <summary>
    /// Lists connection references, optionally filtered by solution.
    /// Enriches with connection status from Power Platform API (graceful degradation for SPN).
    /// Maps to: ppds connref list --json
    /// </summary>
    [JsonRpcMethod("connectionReferences/list")]
    public async Task<ConnectionReferencesListResponse> ConnectionReferencesListAsync(
        string? solutionId = null,
        string? environmentUrl = null,
        bool includeInactive = false,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();
            var result = await connRefService.ListAsync(solutionName: solutionId, includeInactive: includeInactive, cancellationToken: ct);

            // Try to enrich with connection status from Power Platform API
            Dictionary<string, ConnectionInfo>? connectionMap = null;
            try
            {
                var connectionService = sp.GetRequiredService<IConnectionService>();
                var connections = await connectionService.ListAsync(cancellationToken: ct);
                connectionMap = connections.ToDictionary(c => c.ConnectionId, c => c);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to retrieve connection status (SPN may not have access to Connections API). Continuing without connection enrichment.");
            }

            return new ConnectionReferencesListResponse
            {
                TotalCount = result.TotalCount,
                FiltersApplied = result.FiltersApplied.ToList(),
                References = result.Items.Select(r => MapConnectionReferenceToDto(r, connectionMap)).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single connection reference with dependent flows and connection details.
    /// Maps to: ppds connref get --json
    /// </summary>
    [JsonRpcMethod("connectionReferences/get")]
    public async Task<ConnectionReferencesGetResponse> ConnectionReferencesGetAsync(
        string logicalName,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'logicalName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();

            var reference = await connRefService.GetAsync(logicalName, ct)
                ?? throw new RpcException(
                    ErrorCodes.Operation.NotFound,
                    $"Connection reference '{logicalName}' not found");

            var flows = await connRefService.GetFlowsUsingAsync(logicalName, ct);

            // Try to get connection details (graceful degradation for SPN)
            ConnectionInfo? connectionInfo = null;
            if (!string.IsNullOrEmpty(reference.ConnectionId))
            {
                try
                {
                    var connectionService = sp.GetRequiredService<IConnectionService>();
                    connectionInfo = await connectionService.GetAsync(reference.ConnectionId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to retrieve connection details for '{ConnectionId}' (SPN may not have access). Continuing without connection enrichment.", reference.ConnectionId);
                }
            }

            return new ConnectionReferencesGetResponse
            {
                Reference = MapConnectionReferenceToDetailDto(reference, flows, connectionInfo)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Analyzes connection references for orphaned references and flows.
    /// Maps to: ppds connref analyze --json
    /// </summary>
    [JsonRpcMethod("connectionReferences/analyze")]
    public async Task<ConnectionReferencesAnalyzeResponse> ConnectionReferencesAnalyzeAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();
            var analysis = await connRefService.AnalyzeAsync(cancellationToken: ct);

            var orphanedReferences = analysis.Relationships
                .Where(r => r.Type == ConnRefRelationshipType.OrphanedConnectionReference)
                .Select(r => new OrphanedReferenceDto
                {
                    LogicalName = r.ConnectionReferenceLogicalName ?? "",
                    DisplayName = r.ConnectionReferenceDisplayName,
                    ConnectorId = r.ConnectorId
                })
                .ToList();

            var orphanedFlows = analysis.Relationships
                .Where(r => r.Type == ConnRefRelationshipType.OrphanedFlow)
                .Select(r => new OrphanedFlowDto
                {
                    UniqueName = r.FlowUniqueName ?? "",
                    DisplayName = r.FlowDisplayName,
                    MissingReference = r.ConnectionReferenceLogicalName
                })
                .ToList();

            return new ConnectionReferencesAnalyzeResponse
            {
                OrphanedReferences = orphanedReferences,
                OrphanedFlows = orphanedFlows,
                TotalReferences = analysis.Relationships.Count(r =>
                    r.Type == ConnRefRelationshipType.OrphanedConnectionReference ||
                    r.Type == ConnRefRelationshipType.FlowToConnectionReference),
                TotalFlows = analysis.Relationships.Count(r =>
                    r.Type == ConnRefRelationshipType.OrphanedFlow ||
                    r.Type == ConnRefRelationshipType.FlowToConnectionReference)
            };
        }, cancellationToken);
    }

    private static ConnectionReferenceInfoDto MapConnectionReferenceToDto(
        ConnectionReferenceInfo r,
        Dictionary<string, ConnectionInfo>? connectionMap)
    {
        ConnectionInfo? connInfo = null;
        if (!string.IsNullOrEmpty(r.ConnectionId) && connectionMap != null)
        {
            connectionMap.TryGetValue(r.ConnectionId, out connInfo);
        }

        return new ConnectionReferenceInfoDto
        {
            LogicalName = r.LogicalName,
            DisplayName = r.DisplayName,
            ConnectorId = r.ConnectorId,
            ConnectionId = r.ConnectionId,
            IsManaged = r.IsManaged,
            ModifiedOn = r.ModifiedOn?.ToString("o"),
            ConnectionStatus = connInfo?.Status.ToString() ?? "N/A",
            ConnectorDisplayName = connInfo?.ConnectorDisplayName
        };
    }

    private static ConnectionReferenceDetailDto MapConnectionReferenceToDetailDto(
        ConnectionReferenceInfo r,
        List<FlowInfo> flows,
        ConnectionInfo? connectionInfo)
    {
        return new ConnectionReferenceDetailDto
        {
            LogicalName = r.LogicalName,
            DisplayName = r.DisplayName,
            ConnectorId = r.ConnectorId,
            ConnectionId = r.ConnectionId,
            IsManaged = r.IsManaged,
            ModifiedOn = r.ModifiedOn?.ToString("o"),
            ConnectionStatus = connectionInfo?.Status.ToString() ?? "N/A",
            ConnectorDisplayName = connectionInfo?.ConnectorDisplayName,
            Description = r.Description,
            IsBound = r.IsBound,
            CreatedOn = r.CreatedOn?.ToString("o"),
            Flows = flows.Select(f => new FlowReferenceDto
            {
                FlowId = f.Id.ToString(),
                UniqueName = f.UniqueName,
                DisplayName = f.DisplayName,
                State = f.State.ToString()
            }).ToList(),
            ConnectionOwner = connectionInfo?.CreatedBy,
            ConnectionIsShared = connectionInfo?.IsShared
        };
    }

    /// <summary>
    /// Lists Power Platform connections from the Power Apps Admin API,
    /// optionally filtered by connector ID. Used by the VS Code connection picker.
    /// </summary>
    [JsonRpcMethod("connections/list")]
    public async Task<ConnectionsListResponse> ConnectionsListAsync(
        string? connectorId = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var connectionService = sp.GetRequiredService<IConnectionService>();
            var connections = await connectionService.ListAsync(connectorId, ct);

            return new ConnectionsListResponse
            {
                Connections = connections.Select(c => new ConnectionDto
                {
                    ConnectionId = c.ConnectionId,
                    DisplayName = c.DisplayName,
                    ConnectorId = c.ConnectorId,
                    ConnectorDisplayName = c.ConnectorDisplayName,
                    Status = c.Status.ToString(),
                    IsShared = c.IsShared,
                    CreatedBy = c.CreatedBy,
                    ModifiedOn = c.ModifiedOn?.ToString("o")
                }).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Binds a Power Platform connection to a connection reference.
    /// Pass an empty/null connectionId to clear the binding.
    /// </summary>
    [JsonRpcMethod("connectionReferences/bind")]
    public async Task<ConnectionReferencesGetResponse> ConnectionReferencesBindAsync(
        string logicalName,
        string? connectionId = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'logicalName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();
            var updated = await connRefService.BindAsync(logicalName, connectionId, ct);

            var flows = await connRefService.GetFlowsUsingAsync(logicalName, ct);

            ConnectionInfo? connectionInfo = null;
            if (!string.IsNullOrEmpty(updated.ConnectionId))
            {
                try
                {
                    var connectionService = sp.GetRequiredService<IConnectionService>();
                    connectionInfo = await connectionService.GetAsync(updated.ConnectionId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to retrieve connection details for '{ConnectionId}' after bind (SPN may not have access).", updated.ConnectionId);
                }
            }

            return new ConnectionReferencesGetResponse
            {
                Reference = MapConnectionReferenceToDetailDto(updated, flows, connectionInfo)
            };
        }, cancellationToken);
    }

    #endregion
}

public class ConnectionReferencesListResponse
{
    [JsonPropertyName("references")]
    public List<ConnectionReferenceInfoDto> References { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("filtersApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FiltersApplied { get; set; }
}

public class ConnectionReferencesGetResponse
{
    [JsonPropertyName("reference")]
    public ConnectionReferenceDetailDto Reference { get; set; } = null!;
}

public class ConnectionReferencesAnalyzeResponse
{
    [JsonPropertyName("orphanedReferences")]
    public List<OrphanedReferenceDto> OrphanedReferences { get; set; } = [];

    [JsonPropertyName("orphanedFlows")]
    public List<OrphanedFlowDto> OrphanedFlows { get; set; } = [];

    [JsonPropertyName("totalReferences")]
    public int TotalReferences { get; set; }

    [JsonPropertyName("totalFlows")]
    public int TotalFlows { get; set; }
}

public class ConnectionReferenceInfoDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorId { get; set; }

    [JsonPropertyName("connectionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("connectionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionStatus { get; set; }

    [JsonPropertyName("connectorDisplayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorDisplayName { get; set; }
}

public class ConnectionReferenceDetailDto : ConnectionReferenceInfoDto
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("isBound")]
    public bool IsBound { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("flows")]
    public List<FlowReferenceDto> Flows { get; set; } = [];

    [JsonPropertyName("connectionOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionOwner { get; set; }

    [JsonPropertyName("connectionIsShared")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConnectionIsShared { get; set; }
}

public class FlowReferenceDto
{
    [JsonPropertyName("flowId")]
    public string FlowId { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";
}

public class OrphanedReferenceDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorId { get; set; }
}

public class OrphanedFlowDto
{
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("missingReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingReference { get; set; }
}

public class ConnectionsListResponse
{
    [JsonPropertyName("connections")]
    public List<ConnectionDto> Connections { get; set; } = [];
}

public class ConnectionDto
{
    [JsonPropertyName("connectionId")]
    public string ConnectionId { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    public string ConnectorId { get; set; } = "";

    [JsonPropertyName("connectorDisplayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorDisplayName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("isShared")]
    public bool IsShared { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

// ── Environment Variables DTOs ─────────────────────────────────────────────────
