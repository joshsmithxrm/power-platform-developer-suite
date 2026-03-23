using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Models;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for environment variable operations.
/// </summary>
public class EnvironmentVariableService : IEnvironmentVariableService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<EnvironmentVariableService> _logger;

    private static readonly Dictionary<int, string> TypeNames = new()
    {
        { 100000000, "String" },
        { 100000001, "Number" },
        { 100000002, "Boolean" },
        { 100000003, "JSON" },
        { 100000004, "DataSource" },
        { 100000005, "Secret" }
    };

    private static readonly Dictionary<int, string> SecretStoreNames = new()
    {
        { 0, "AzureKeyVault" },
        { 1, "MicrosoftDataverse" }
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="EnvironmentVariableService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="logger">The logger.</param>
    public EnvironmentVariableService(
        IDataverseConnectionPool pool,
        ILogger<EnvironmentVariableService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ListResult<EnvironmentVariableInfo>> ListAsync(
        string? solutionName = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // First, get all environment variable definitions
        var definitionQuery = new QueryExpression(EnvironmentVariableDefinition.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                EnvironmentVariableDefinition.Fields.EnvironmentVariableDefinitionId,
                EnvironmentVariableDefinition.Fields.SchemaName,
                EnvironmentVariableDefinition.Fields.DisplayName,
                EnvironmentVariableDefinition.Fields.Description,
                EnvironmentVariableDefinition.Fields.Type,
                EnvironmentVariableDefinition.Fields.DefaultValue,
                EnvironmentVariableDefinition.Fields.IsRequired,
                EnvironmentVariableDefinition.Fields.IsManaged,
                EnvironmentVariableDefinition.Fields.SecretStore,
                EnvironmentVariableDefinition.Fields.CreatedOn,
                EnvironmentVariableDefinition.Fields.ModifiedOn),
            Orders = { new OrderExpression(EnvironmentVariableDefinition.Fields.SchemaName, OrderType.Ascending) }
        };

        var filtersApplied = new List<string>();

        // Filter by solution if specified
        if (!string.IsNullOrEmpty(solutionName))
        {
            var solutionLink = definitionQuery.AddLink(
                SolutionComponent.EntityLogicalName,
                EnvironmentVariableDefinition.Fields.EnvironmentVariableDefinitionId,
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

        if (!includeInactive)
        {
            // Only get active variables
            definitionQuery.Criteria.AddCondition(
                EnvironmentVariableDefinition.Fields.statecode, ConditionOperator.Equal, 0);
            filtersApplied.Add("active only");
        }

        _logger.LogDebug("Querying environment variable definitions");
        var definitions = await client.RetrieveMultipleAsync(definitionQuery, cancellationToken);

        if (definitions.Entities.Count == 0)
        {
            return new ListResult<EnvironmentVariableInfo>
            {
                Items = [],
                TotalCount = 0,
                FiltersApplied = filtersApplied
            };
        }

        // Get all values for these definitions
        var definitionIds = definitions.Entities
            .Select(e => e.Id)
            .ToList();

        var valueQuery = new QueryExpression(EnvironmentVariableValue.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                EnvironmentVariableValue.Fields.EnvironmentVariableValueId,
                EnvironmentVariableValue.Fields.EnvironmentVariableDefinitionId,
                EnvironmentVariableValue.Fields.Value)
        };
        valueQuery.Criteria.AddCondition(
            EnvironmentVariableValue.Fields.EnvironmentVariableDefinitionId,
            ConditionOperator.In,
            definitionIds.Cast<object>().ToArray());
        valueQuery.Criteria.AddCondition(
            EnvironmentVariableValue.Fields.statecode, ConditionOperator.Equal, 0);

        var values = await client.RetrieveMultipleAsync(valueQuery, cancellationToken);

        // Create lookup for values by definition ID
        var valuesByDefinitionId = values.Entities.ToDictionary(
            e => e.GetAttributeValue<EntityReference>(EnvironmentVariableValue.Fields.EnvironmentVariableDefinitionId)?.Id ?? Guid.Empty,
            e => e);

        var items = new List<EnvironmentVariableInfo>();
        foreach (var def in definitions.Entities)
        {
            var defId = def.Id;
            valuesByDefinitionId.TryGetValue(defId, out var valueEntity);

            items.Add(MapToInfo(def, valueEntity));
        }

        _logger.LogDebug("Found {Count} environment variables", items.Count);

        return new ListResult<EnvironmentVariableInfo>
        {
            Items = items,
            TotalCount = items.Count,
            FiltersApplied = filtersApplied
        };
    }

    /// <inheritdoc />
    public async Task<EnvironmentVariableInfo?> GetAsync(
        string schemaName,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var definitionQuery = new QueryExpression(EnvironmentVariableDefinition.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(true),
            TopCount = 1
        };
        definitionQuery.Criteria.AddCondition(
            EnvironmentVariableDefinition.Fields.SchemaName, ConditionOperator.Equal, schemaName);
        definitionQuery.Criteria.AddCondition(
            EnvironmentVariableDefinition.Fields.statecode, ConditionOperator.Equal, 0);

        var definitions = await client.RetrieveMultipleAsync(definitionQuery, cancellationToken);
        if (definitions.Entities.Count == 0)
        {
            return null;
        }

        var def = definitions.Entities[0];
        var valueEntity = await GetValueEntityAsync(client, def.Id, cancellationToken);

        return MapToInfo(def, valueEntity);
    }

    /// <inheritdoc />
    public async Task<EnvironmentVariableInfo?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            var def = await client.RetrieveAsync(
                EnvironmentVariableDefinition.EntityLogicalName,
                id,
                new ColumnSet(true),
                cancellationToken);

            var valueEntity = await GetValueEntityAsync(client, id, cancellationToken);
            return MapToInfo(def, valueEntity);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<bool> SetValueAsync(
        string schemaName,
        string value,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Find the definition by schema name
        var definitionQuery = new QueryExpression(EnvironmentVariableDefinition.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(EnvironmentVariableDefinition.Fields.EnvironmentVariableDefinitionId),
            TopCount = 1
        };
        definitionQuery.Criteria.AddCondition(
            EnvironmentVariableDefinition.Fields.SchemaName, ConditionOperator.Equal, schemaName);
        definitionQuery.Criteria.AddCondition(
            EnvironmentVariableDefinition.Fields.statecode, ConditionOperator.Equal, 0);

        var definitions = await client.RetrieveMultipleAsync(definitionQuery, cancellationToken);
        if (definitions.Entities.Count == 0)
        {
            _logger.LogWarning("Environment variable '{SchemaName}' not found", schemaName);
            return false;
        }

        var definitionId = definitions.Entities[0].Id;

        // Check if a value record already exists
        var existingValue = await GetValueEntityAsync(client, definitionId, cancellationToken);

        if (existingValue != null)
        {
            // Update existing value
            var update = new Entity(EnvironmentVariableValue.EntityLogicalName, existingValue.Id)
            {
                [EnvironmentVariableValue.Fields.Value] = value
            };
            await client.UpdateAsync(update, cancellationToken);
            _logger.LogDebug("Updated environment variable '{SchemaName}' value", schemaName);
        }
        else
        {
            // Create new value record
            var newValue = new Entity(EnvironmentVariableValue.EntityLogicalName)
            {
                [EnvironmentVariableValue.Fields.EnvironmentVariableDefinitionId] =
                    new EntityReference(EnvironmentVariableDefinition.EntityLogicalName, definitionId),
                [EnvironmentVariableValue.Fields.Value] = value
            };
            await client.CreateAsync(newValue, cancellationToken);
            _logger.LogDebug("Created environment variable '{SchemaName}' value", schemaName);
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<EnvironmentVariableExport> ExportAsync(
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        var result = await ListAsync(solutionName: solutionName, cancellationToken: cancellationToken);

        var exportItems = result.Items
            .Select(v => new EnvironmentVariableExportItem
            {
                SchemaName = v.SchemaName,
                Value = v.CurrentValue ?? v.DefaultValue
            })
            .ToList();

        return new EnvironmentVariableExport
        {
            EnvironmentVariables = exportItems
        };
    }

    private async Task<Entity?> GetValueEntityAsync(
        IDataverseClient client,
        Guid definitionId,
        CancellationToken cancellationToken)
    {
        var valueQuery = new QueryExpression(EnvironmentVariableValue.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                EnvironmentVariableValue.Fields.EnvironmentVariableValueId,
                EnvironmentVariableValue.Fields.Value),
            TopCount = 1
        };
        valueQuery.Criteria.AddCondition(
            EnvironmentVariableValue.Fields.EnvironmentVariableDefinitionId,
            ConditionOperator.Equal,
            definitionId);
        valueQuery.Criteria.AddCondition(
            EnvironmentVariableValue.Fields.statecode, ConditionOperator.Equal, 0);

        var values = await client.RetrieveMultipleAsync(valueQuery, cancellationToken);
        return values.Entities.FirstOrDefault();
    }

    private static EnvironmentVariableInfo MapToInfo(Entity definition, Entity? valueEntity)
    {
        var typeValue = definition.GetAttributeValue<OptionSetValue>(EnvironmentVariableDefinition.Fields.Type)?.Value ?? 100000000;
        var secretStoreValue = definition.GetAttributeValue<OptionSetValue>(EnvironmentVariableDefinition.Fields.SecretStore)?.Value;

        return new EnvironmentVariableInfo
        {
            Id = definition.Id,
            SchemaName = definition.GetAttributeValue<string>(EnvironmentVariableDefinition.Fields.SchemaName) ?? string.Empty,
            DisplayName = definition.GetAttributeValue<string>(EnvironmentVariableDefinition.Fields.DisplayName),
            Description = definition.GetAttributeValue<string>(EnvironmentVariableDefinition.Fields.Description),
            Type = TypeNames.TryGetValue(typeValue, out var typeName) ? typeName : typeValue.ToString(),
            DefaultValue = definition.GetAttributeValue<string>(EnvironmentVariableDefinition.Fields.DefaultValue),
            CurrentValue = valueEntity?.GetAttributeValue<string>(EnvironmentVariableValue.Fields.Value),
            CurrentValueId = valueEntity?.Id,
            IsRequired = definition.GetAttributeValue<bool>(EnvironmentVariableDefinition.Fields.IsRequired),
            IsManaged = definition.GetAttributeValue<bool>(EnvironmentVariableDefinition.Fields.IsManaged),
            SecretStore = secretStoreValue.HasValue && SecretStoreNames.TryGetValue(secretStoreValue.Value, out var secretStoreName)
                ? secretStoreName
                : null,
            CreatedOn = definition.GetAttributeValue<DateTime?>(EnvironmentVariableDefinition.Fields.CreatedOn),
            ModifiedOn = definition.GetAttributeValue<DateTime?>(EnvironmentVariableDefinition.Fields.ModifiedOn)
        };
    }
}
