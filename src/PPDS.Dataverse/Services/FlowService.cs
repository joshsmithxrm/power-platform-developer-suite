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
/// Service for cloud flow (Power Automate) operations.
/// </summary>
public class FlowService : IFlowService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<FlowService> _logger;

    // Category values for cloud flows
    private const int ModernFlowCategory = 5;
    private const int DesktopFlowCategory = 6;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlowService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="logger">The logger.</param>
    public FlowService(
        IDataverseConnectionPool pool,
        ILogger<FlowService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<FlowInfo>> ListAsync(
        string? solutionName = null,
        FlowState? state = null,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Workflow.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                Workflow.Fields.WorkflowId,
                Workflow.Fields.UniqueName,
                Workflow.Fields.Name,
                Workflow.Fields.Description,
                Workflow.Fields.StateCode,
                Workflow.Fields.Category,
                Workflow.Fields.IsManaged,
                Workflow.Fields.ClientData,
                Workflow.Fields.CreatedOn,
                Workflow.Fields.ModifiedOn,
                Workflow.Fields.OwnerId),
            Orders = { new OrderExpression(Workflow.Fields.Name, OrderType.Ascending) }
        };

        // Filter to only cloud flows (ModernFlow=5 or DesktopFlow=6)
        var categoryFilter = new FilterExpression(LogicalOperator.Or);
        categoryFilter.AddCondition(Workflow.Fields.Category, ConditionOperator.Equal, ModernFlowCategory);
        categoryFilter.AddCondition(Workflow.Fields.Category, ConditionOperator.Equal, DesktopFlowCategory);
        query.Criteria.AddFilter(categoryFilter);

        // Filter by state if specified
        if (state.HasValue)
        {
            query.Criteria.AddCondition(Workflow.Fields.StateCode, ConditionOperator.Equal, (int)state.Value);
        }

        // Filter by solution if specified
        if (!string.IsNullOrEmpty(solutionName))
        {
            var solutionLink = query.AddLink(
                SolutionComponent.EntityLogicalName,
                Workflow.Fields.WorkflowId,
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

        _logger.LogDebug("Querying cloud flows");
        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        var flows = results.Entities.Select(MapToFlowInfo).ToList();
        _logger.LogDebug("Found {Count} cloud flows", flows.Count);

        return flows;
    }

    /// <inheritdoc />
    public async Task<FlowInfo?> GetAsync(
        string uniqueName,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(Workflow.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(true),
            TopCount = 1
        };

        // Filter by unique name
        query.Criteria.AddCondition(Workflow.Fields.UniqueName, ConditionOperator.Equal, uniqueName);

        // Filter to only cloud flows
        var categoryFilter = new FilterExpression(LogicalOperator.Or);
        categoryFilter.AddCondition(Workflow.Fields.Category, ConditionOperator.Equal, ModernFlowCategory);
        categoryFilter.AddCondition(Workflow.Fields.Category, ConditionOperator.Equal, DesktopFlowCategory);
        query.Criteria.AddFilter(categoryFilter);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        if (results.Entities.Count == 0)
        {
            return null;
        }

        return MapToFlowInfo(results.Entities[0]);
    }

    /// <inheritdoc />
    public async Task<FlowInfo?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            var entity = await client.RetrieveAsync(
                Workflow.EntityLogicalName,
                id,
                new ColumnSet(true),
                cancellationToken);

            // Verify it's a cloud flow
            var category = entity.GetAttributeValue<OptionSetValue>(Workflow.Fields.Category)?.Value;
            if (category != ModernFlowCategory && category != DesktopFlowCategory)
            {
                return null;
            }

            return MapToFlowInfo(entity);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return null;
        }
    }

    private static FlowInfo MapToFlowInfo(Entity entity)
    {
        var stateValue = entity.GetAttributeValue<OptionSetValue>(Workflow.Fields.StateCode)?.Value ?? 0;
        var categoryValue = entity.GetAttributeValue<OptionSetValue>(Workflow.Fields.Category)?.Value ?? ModernFlowCategory;
        var clientData = entity.GetAttributeValue<string>(Workflow.Fields.ClientData);
        var ownerRef = entity.GetAttributeValue<EntityReference>(Workflow.Fields.OwnerId);

        return new FlowInfo
        {
            Id = entity.Id,
            UniqueName = entity.GetAttributeValue<string>(Workflow.Fields.UniqueName) ?? string.Empty,
            DisplayName = entity.GetAttributeValue<string>(Workflow.Fields.Name),
            Description = entity.GetAttributeValue<string>(Workflow.Fields.Description),
            State = (FlowState)stateValue,
            Category = (FlowCategory)categoryValue,
            IsManaged = entity.GetAttributeValue<bool>(Workflow.Fields.IsManaged),
            ConnectionReferenceLogicalNames = FlowClientDataParser.ExtractConnectionReferenceLogicalNames(clientData),
            CreatedOn = entity.GetAttributeValue<DateTime?>(Workflow.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(Workflow.Fields.ModifiedOn),
            OwnerId = ownerRef?.Id,
            OwnerName = ownerRef?.Name
        };
    }
}
