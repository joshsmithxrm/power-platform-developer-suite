using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services;

/// <summary>
/// Service for managing Dataverse virtual entity data providers and data sources.
/// </summary>
/// <remarks>
/// Uses connection pooling so each method acquires its own client for safe parallel use.
/// </remarks>
public sealed class DataProviderService : IDataProviderService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IShakedownGuard _guard;
    private readonly ILogger<DataProviderService> _logger;

    // Entity logical names (entitydatasource is not in the generated entities)
    // entitydatasource has a very limited schema — only 'name' is reliably queryable
    // It's a metadata entity, not a standard data entity (no displayname, createdon, modifiedon)
    private const string DataSourceEntityName = "entitydatasource";
    private const string DataSourceIdField = "entitydatasourceid";
    private const string DataSourceNameField = "name";

    /// <summary>
    /// Creates a new instance of <see cref="DataProviderService"/>.
    /// </summary>
    public DataProviderService(IDataverseConnectionPool pool, IShakedownGuard guard, ILogger<DataProviderService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Data Source — Query Operations

    /// <inheritdoc />
    public async Task<List<DataSourceInfo>> ListDataSourcesAsync(CancellationToken cancellationToken = default)
    {
        var query = BuildDataSourceListQuery();
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.Select(MapToDataSourceInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<DataSourceInfo?> GetDataSourceAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        var query = BuildDataSourceListQuery();
        if (Guid.TryParse(nameOrId, out var id))
        {
            query.Criteria.AddCondition(DataSourceIdField, ConditionOperator.Equal, id);
        }
        else
        {
            query.Criteria.AddCondition(DataSourceNameField, ConditionOperator.Equal, nameOrId);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } e ? MapToDataSourceInfo(e) : null;
    }

    #endregion

    #region Data Source — Register Operations

    /// <inheritdoc />
    public async Task<Guid> RegisterDataSourceAsync(DataSourceRegistration registration, CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("dataproviders.dataSource.register");
        if (string.IsNullOrWhiteSpace(registration.Name))
        {
            throw new PpdsException(
                ErrorCodes.DataProvider.ValidationFailed,
                "Data source name is required.");
        }

        var entity = new Entity(DataSourceEntityName);
        entity[DataSourceNameField] = registration.Name;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var newId = await CreateAsync(entity, client, cancellationToken);

        _logger.LogInformation("Registered data source '{Name}' (ID: {Id})", registration.Name, newId);
        return newId;
    }

    #endregion

    #region Data Source — Unregister Operations

    /// <inheritdoc />
    public async Task UnregisterDataSourceAsync(Guid id, bool force = false, CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("dataproviders.dataSource.unregister");
        var existing = await GetDataSourceByIdInternalAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.DataSource.NotFound,
                $"Data source with ID '{id}' was not found.");
        }

        // Find dependent data providers
        var providerQuery = BuildDataProviderListQuery();
        providerQuery.Criteria.AddCondition(
            EntityDataProvider.Fields.DataSourceLogicalName,
            ConditionOperator.Equal,
            existing.Name);

        await using var provClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var provResults = await RetrieveMultipleAsync(providerQuery, provClient, cancellationToken);
        var providers = provResults.Entities.ToList();

        if (providers.Count > 0 && !force)
        {
            throw new PpdsException(
                ErrorCodes.DataSource.HasDependents,
                $"Data source '{existing.Name}' has {providers.Count} data provider(s). " +
                "Use force=true to cascade delete.");
        }

        if (providers.Count > 0)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var provider = providers[i];
                await using var depClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
                await DeleteEntityAsync(EntityDataProvider.EntityLogicalName, provider.Id, depClient, cancellationToken);
            }
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteEntityAsync(DataSourceEntityName, id, client, cancellationToken);

        _logger.LogInformation("Unregistered data source '{Name}' (ID: {Id})", existing.Name, id);
    }

    #endregion

    #region Data Provider — Query Operations

    /// <inheritdoc />
    public async Task<List<DataProviderInfo>> ListDataProvidersAsync(Guid? dataSourceId = null, CancellationToken cancellationToken = default)
    {
        var query = BuildDataProviderListQuery();

        if (dataSourceId.HasValue)
        {
            // We need to look up the data source logical name for filtering
            var dsInfo = await GetDataSourceByIdInternalAsync(dataSourceId.Value, cancellationToken);
            if (dsInfo is not null)
            {
                query.Criteria.AddCondition(
                    EntityDataProvider.Fields.DataSourceLogicalName,
                    ConditionOperator.Equal,
                    dsInfo.Name);
            }
            else
            {
                // Data source not found — filter by ID field if possible, or return empty
                query.Criteria.AddCondition(
                    EntityDataProvider.Fields.EntityDataProviderId,
                    ConditionOperator.Null);
            }
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.Select(MapToDataProviderInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<DataProviderInfo?> GetDataProviderAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        var query = BuildDataProviderListQuery();
        if (Guid.TryParse(nameOrId, out var id))
        {
            query.Criteria.AddCondition(EntityDataProvider.Fields.EntityDataProviderId, ConditionOperator.Equal, id);
        }
        else
        {
            query.Criteria.AddCondition(EntityDataProvider.Fields.Name, ConditionOperator.Equal, nameOrId);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } e ? MapToDataProviderInfo(e) : null;
    }

    #endregion

    #region Data Provider — Register Operations

    /// <inheritdoc />
    public async Task<Guid> RegisterDataProviderAsync(DataProviderRegistration registration, CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("dataproviders.provider.register");
        if (string.IsNullOrWhiteSpace(registration.Name))
        {
            throw new PpdsException(
                ErrorCodes.DataProvider.ValidationFailed,
                "Data provider name is required.");
        }

        // Look up the data source to get its logical name
        var dataSource = await GetDataSourceByIdInternalAsync(registration.DataSourceId, cancellationToken);
        if (dataSource is null)
        {
            throw new PpdsException(
                ErrorCodes.DataSource.NotFound,
                $"Data source with ID '{registration.DataSourceId}' was not found.");
        }

        var entity = new Entity(EntityDataProvider.EntityLogicalName);
        entity[EntityDataProvider.Fields.Name] = registration.Name;
        entity[EntityDataProvider.Fields.DataSourceLogicalName] = dataSource.Name;

        if (registration.RetrievePlugin.HasValue)
            entity[EntityDataProvider.Fields.RetrievePlugin] = registration.RetrievePlugin.Value;

        if (registration.RetrieveMultiplePlugin.HasValue)
            entity[EntityDataProvider.Fields.RetrieveMultiplePlugin] = registration.RetrieveMultiplePlugin.Value;

        if (registration.CreatePlugin.HasValue)
            entity[EntityDataProvider.Fields.CreatePlugin] = registration.CreatePlugin.Value;

        if (registration.UpdatePlugin.HasValue)
            entity[EntityDataProvider.Fields.UpdatePlugin] = registration.UpdatePlugin.Value;

        if (registration.DeletePlugin.HasValue)
            entity[EntityDataProvider.Fields.DeletePlugin] = registration.DeletePlugin.Value;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var newId = await CreateAsync(entity, client, cancellationToken);

        _logger.LogInformation("Registered data provider '{Name}' (ID: {Id})", registration.Name, newId);
        return newId;
    }

    #endregion

    #region Data Provider — Update Operations

    /// <inheritdoc />
    public async Task UpdateDataProviderAsync(Guid id, DataProviderUpdateRequest request, CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("dataproviders.provider.update");
        var existing = await GetDataProviderByIdInternalAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.DataProvider.NotFound,
                $"Data provider with ID '{id}' was not found.");
        }

        var update = new Entity(EntityDataProvider.EntityLogicalName) { Id = id };
        var hasChanges = false;

        if (request.RetrievePlugin.HasValue)
        {
            update[EntityDataProvider.Fields.RetrievePlugin] = request.RetrievePlugin.Value;
            hasChanges = true;
        }

        if (request.RetrieveMultiplePlugin.HasValue)
        {
            update[EntityDataProvider.Fields.RetrieveMultiplePlugin] = request.RetrieveMultiplePlugin.Value;
            hasChanges = true;
        }

        if (request.CreatePlugin.HasValue)
        {
            update[EntityDataProvider.Fields.CreatePlugin] = request.CreatePlugin.Value;
            hasChanges = true;
        }

        if (request.UpdatePlugin.HasValue)
        {
            update[EntityDataProvider.Fields.UpdatePlugin] = request.UpdatePlugin.Value;
            hasChanges = true;
        }

        if (request.DeletePlugin.HasValue)
        {
            update[EntityDataProvider.Fields.DeletePlugin] = request.DeletePlugin.Value;
            hasChanges = true;
        }

        if (!hasChanges) return;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await UpdateEntityAsync(update, client, cancellationToken);
    }

    #endregion

    #region Data Provider — Unregister Operations

    /// <inheritdoc />
    public async Task UnregisterDataProviderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _guard.EnsureCanMutate("dataproviders.provider.unregister");
        var existing = await GetDataProviderByIdInternalAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.DataProvider.NotFound,
                $"Data provider with ID '{id}' was not found.");
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteEntityAsync(EntityDataProvider.EntityLogicalName, id, client, cancellationToken);

        _logger.LogInformation("Unregistered data provider '{Name}' (ID: {Id})", existing.Name, id);
    }

    #endregion

    #region Private Helpers

    private async Task<DataSourceInfo?> GetDataSourceByIdInternalAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = BuildDataSourceListQuery();
        query.Criteria.AddCondition(DataSourceIdField, ConditionOperator.Equal, id);
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } e ? MapToDataSourceInfo(e) : null;
    }

    private async Task<DataProviderInfo?> GetDataProviderByIdInternalAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = BuildDataProviderListQuery();
        query.Criteria.AddCondition(EntityDataProvider.Fields.EntityDataProviderId, ConditionOperator.Equal, id);
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } e ? MapToDataProviderInfo(e) : null;
    }

    private static QueryExpression BuildDataSourceListQuery() =>
        new(DataSourceEntityName)
        {
            ColumnSet = new ColumnSet(
                DataSourceNameField),
            Orders = { new OrderExpression(DataSourceNameField, OrderType.Ascending) }
        };

    private static QueryExpression BuildDataProviderListQuery() =>
        new(EntityDataProvider.EntityLogicalName)
        {
            // entitydataprovider is a metadata entity — no createdon/modifiedon
            ColumnSet = new ColumnSet(
                EntityDataProvider.Fields.Name,
                EntityDataProvider.Fields.DataSourceLogicalName,
                EntityDataProvider.Fields.RetrievePlugin,
                EntityDataProvider.Fields.RetrieveMultiplePlugin,
                EntityDataProvider.Fields.CreatePlugin,
                EntityDataProvider.Fields.UpdatePlugin,
                EntityDataProvider.Fields.DeletePlugin,
                EntityDataProvider.Fields.IsManaged),
            Orders = { new OrderExpression(EntityDataProvider.Fields.Name, OrderType.Ascending) }
        };

    private static DataSourceInfo MapToDataSourceInfo(Entity e) =>
        new()
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(DataSourceNameField) ?? ""
        };

    private static DataProviderInfo MapToDataProviderInfo(Entity e) =>
        new()
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(EntityDataProvider.Fields.Name) ?? "",
            DataSourceName = e.GetAttributeValue<string>(EntityDataProvider.Fields.DataSourceLogicalName),
            RetrievePlugin = e.GetAttributeValue<Guid?>(EntityDataProvider.Fields.RetrievePlugin),
            RetrieveMultiplePlugin = e.GetAttributeValue<Guid?>(EntityDataProvider.Fields.RetrieveMultiplePlugin),
            CreatePlugin = e.GetAttributeValue<Guid?>(EntityDataProvider.Fields.CreatePlugin),
            UpdatePlugin = e.GetAttributeValue<Guid?>(EntityDataProvider.Fields.UpdatePlugin),
            DeletePlugin = e.GetAttributeValue<Guid?>(EntityDataProvider.Fields.DeletePlugin),
            IsManaged = e.GetAttributeValue<bool?>(EntityDataProvider.Fields.IsManaged) ?? false
        };

    // Async Dataverse helpers (same pattern as CustomApiService)

    private static async Task<EntityCollection> RetrieveMultipleAsync(
        QueryExpression query,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            return await asyncService.RetrieveMultipleAsync(query, cancellationToken);
        return await Task.Run(() => client.RetrieveMultiple(query), cancellationToken);
    }

    private static async Task<Guid> CreateAsync(
        Entity entity,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            return await asyncService.CreateAsync(entity, cancellationToken);
        return await Task.Run(() => client.Create(entity), cancellationToken);
    }

    private static async Task UpdateEntityAsync(
        Entity entity,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            await asyncService.UpdateAsync(entity, cancellationToken);
        else
            await Task.Run(() => client.Update(entity), cancellationToken);
    }

    private static async Task DeleteEntityAsync(
        string entityName,
        Guid id,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            await asyncService.DeleteAsync(entityName, id, cancellationToken);
        else
            await Task.Run(() => client.Delete(entityName, id), cancellationToken);
    }

    #endregion
}
