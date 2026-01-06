using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services.Utilities;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for connection reference operations in Dataverse.
/// </summary>
public class ConnectionReferenceService : IConnectionReferenceService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IFlowService _flowService;
    private readonly ILogger<ConnectionReferenceService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConnectionReferenceService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="flowService">The flow service for relationship analysis.</param>
    /// <param name="logger">The logger.</param>
    public ConnectionReferenceService(
        IDataverseConnectionPool pool,
        IFlowService flowService,
        ILogger<ConnectionReferenceService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _flowService = flowService ?? throw new ArgumentNullException(nameof(flowService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<ConnectionReferenceInfo>> ListAsync(
        string? solutionName = null,
        bool unboundOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(ConnectionReference.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                ConnectionReference.Fields.ConnectionReferenceId,
                ConnectionReference.Fields.ConnectionReferenceLogicalName,
                ConnectionReference.Fields.ConnectionReferenceDisplayName,
                ConnectionReference.Fields.Description,
                ConnectionReference.Fields.ConnectionId,
                ConnectionReference.Fields.ConnectorId,
                ConnectionReference.Fields.IsManaged,
                ConnectionReference.Fields.CreatedOn,
                ConnectionReference.Fields.ModifiedOn),
            Orders = { new OrderExpression(ConnectionReference.Fields.ConnectionReferenceLogicalName, OrderType.Ascending) }
        };

        // Only active connection references
        query.Criteria.AddCondition(ConnectionReference.Fields.StateCode, ConditionOperator.Equal, 0);

        // Filter to unbound only if specified
        if (unboundOnly)
        {
            query.Criteria.AddCondition(ConnectionReference.Fields.ConnectionId, ConditionOperator.Null);
        }

        // Filter by solution if specified
        if (!string.IsNullOrEmpty(solutionName))
        {
            var solutionLink = query.AddLink(
                SolutionComponent.EntityLogicalName,
                ConnectionReference.Fields.ConnectionReferenceId,
                SolutionComponent.Fields.ObjectId);
            solutionLink.EntityAlias = "sc";

            var solutionLink2 = solutionLink.AddLink(
                Solution.EntityLogicalName,
                SolutionComponent.Fields.SolutionId,
                Solution.Fields.SolutionId);
            solutionLink2.EntityAlias = "sol";
            solutionLink2.LinkCriteria.AddCondition(
                Solution.Fields.UniqueName, ConditionOperator.Equal, solutionName);
        }

        _logger.LogDebug("Querying connection references");
        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        var connectionRefs = results.Entities.Select(MapToInfo).ToList();
        _logger.LogDebug("Found {Count} connection references", connectionRefs.Count);

        return connectionRefs;
    }

    /// <inheritdoc />
    public async Task<ConnectionReferenceInfo?> GetAsync(
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(ConnectionReference.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(true),
            TopCount = 1
        };
        query.Criteria.AddCondition(
            ConnectionReference.Fields.ConnectionReferenceLogicalName,
            ConditionOperator.Equal,
            logicalName);
        query.Criteria.AddCondition(ConnectionReference.Fields.StateCode, ConditionOperator.Equal, 0);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        if (results.Entities.Count == 0)
        {
            return null;
        }

        return MapToInfo(results.Entities[0]);
    }

    /// <inheritdoc />
    public async Task<ConnectionReferenceInfo?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            var entity = await client.RetrieveAsync(
                ConnectionReference.EntityLogicalName,
                id,
                new ColumnSet(true),
                cancellationToken);

            return MapToInfo(entity);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<FlowInfo>> GetFlowsUsingAsync(
        string logicalName,
        CancellationToken cancellationToken = default)
    {
        // Get all flows and filter by those referencing this connection reference
        var allFlows = await _flowService.ListAsync(cancellationToken: cancellationToken);

        // Case-insensitive comparison for connection reference names
        return allFlows
            .Where(f => f.ConnectionReferenceLogicalNames
                .Any(cr => string.Equals(cr, logicalName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<FlowConnectionAnalysis> AnalyzeAsync(
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Analyzing flow-connection reference relationships");

        // Get all connection references and flows in the solution
        var connectionRefs = await ListAsync(solutionName, cancellationToken: cancellationToken);
        var flows = await _flowService.ListAsync(solutionName, cancellationToken: cancellationToken);

        // Build case-insensitive lookup for connection references
        var crLookup = connectionRefs.ToDictionary(
            cr => cr.LogicalName,
            cr => cr,
            StringComparer.OrdinalIgnoreCase);

        // Track which CRs are used by at least one flow
        var usedCrLogicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var relationships = new List<FlowConnectionRelationship>();

        // Process each flow's connection reference dependencies
        foreach (var flow in flows)
        {
            foreach (var crName in flow.ConnectionReferenceLogicalNames)
            {
                if (crLookup.TryGetValue(crName, out var cr))
                {
                    // Valid relationship: flow uses existing CR
                    relationships.Add(new FlowConnectionRelationship
                    {
                        Type = RelationshipType.FlowToConnectionReference,
                        FlowUniqueName = flow.UniqueName,
                        FlowDisplayName = flow.DisplayName,
                        ConnectionReferenceLogicalName = cr.LogicalName,
                        ConnectionReferenceDisplayName = cr.DisplayName,
                        ConnectorId = cr.ConnectorId,
                        IsBound = cr.IsBound
                    });
                    usedCrLogicalNames.Add(cr.LogicalName);
                }
                else
                {
                    // Orphaned flow: references CR that doesn't exist
                    relationships.Add(new FlowConnectionRelationship
                    {
                        Type = RelationshipType.OrphanedFlow,
                        FlowUniqueName = flow.UniqueName,
                        FlowDisplayName = flow.DisplayName,
                        ConnectionReferenceLogicalName = crName, // The missing CR name
                        ConnectionReferenceDisplayName = null,
                        ConnectorId = null,
                        IsBound = null
                    });
                }
            }
        }

        // Find orphaned connection references (exist but not used by any flow)
        foreach (var cr in connectionRefs)
        {
            if (!usedCrLogicalNames.Contains(cr.LogicalName))
            {
                relationships.Add(new FlowConnectionRelationship
                {
                    Type = RelationshipType.OrphanedConnectionReference,
                    FlowUniqueName = null,
                    FlowDisplayName = null,
                    ConnectionReferenceLogicalName = cr.LogicalName,
                    ConnectionReferenceDisplayName = cr.DisplayName,
                    ConnectorId = cr.ConnectorId,
                    IsBound = cr.IsBound
                });
            }
        }

        var analysis = new FlowConnectionAnalysis { Relationships = relationships };

        _logger.LogDebug(
            "Analysis complete: {Valid} valid, {OrphanedFlows} orphaned flows, {OrphanedCRs} orphaned CRs",
            analysis.ValidCount,
            analysis.OrphanedFlowCount,
            analysis.OrphanedConnectionReferenceCount);

        return analysis;
    }

    private static ConnectionReferenceInfo MapToInfo(Entity entity)
    {
        return new ConnectionReferenceInfo
        {
            Id = entity.Id,
            LogicalName = entity.GetAttributeValue<string>(ConnectionReference.Fields.ConnectionReferenceLogicalName) ?? string.Empty,
            DisplayName = entity.GetAttributeValue<string>(ConnectionReference.Fields.ConnectionReferenceDisplayName),
            Description = entity.GetAttributeValue<string>(ConnectionReference.Fields.Description),
            ConnectionId = entity.GetAttributeValue<string>(ConnectionReference.Fields.ConnectionId),
            ConnectorId = entity.GetAttributeValue<string>(ConnectionReference.Fields.ConnectorId),
            IsManaged = entity.GetAttributeValue<bool>(ConnectionReference.Fields.IsManaged),
            CreatedOn = entity.GetAttributeValue<DateTime?>(ConnectionReference.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(ConnectionReference.Fields.ModifiedOn)
        };
    }
}
