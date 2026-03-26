using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Services;

/// <summary>
/// Service for managing Dataverse Custom APIs and their request/response parameters.
/// </summary>
/// <remarks>
/// Uses connection pooling so each method acquires its own client for safe parallel use.
/// </remarks>
public sealed class CustomApiService : ICustomApiService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<CustomApiService> _logger;

    // BindingType OptionSet values
    private const int BindingTypeGlobal = 0;
    private const int BindingTypeEntity = 1;
    private const int BindingTypeEntityCollection = 2;

    // AllowedCustomProcessingStepType OptionSet values
    private const int ProcessingStepNone = 0;
    private const int ProcessingStepAsyncOnly = 1;
    private const int ProcessingStepSyncAndAsync = 2;

    // Parameter type OptionSet values (same enum for both request params and response props)
    private const int TypeBoolean = 0;
    private const int TypeDateTime = 1;
    private const int TypeDecimal = 2;
    private const int TypeEntity = 3;
    private const int TypeEntityCollection = 4;
    private const int TypeEntityReference = 5;
    private const int TypeFloat = 6;
    private const int TypeInteger = 7;
    private const int TypeMoney = 8;
    private const int TypePicklist = 9;
    private const int TypeString = 10;
    private const int TypeStringArray = 11;
    private const int TypeGuid = 12;

    /// <summary>
    /// Creates a new instance of <see cref="CustomApiService"/>.
    /// </summary>
    public CustomApiService(IDataverseConnectionPool pool, ILogger<CustomApiService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Query Operations

    /// <inheritdoc />
    public async Task<List<CustomApiInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var query = BuildApiListQuery();
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities
            .Select(e => MapToInfo(e, requestParameters: [], responseProperties: []))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CustomApiInfo?> GetAsync(string uniqueNameOrId, CancellationToken cancellationToken = default)
    {
        // Fetch the API entity
        var query = BuildApiListQuery();
        if (Guid.TryParse(uniqueNameOrId, out var id))
        {
            query.Criteria.AddCondition(CustomAPI.Fields.CustomAPIId, ConditionOperator.Equal, id);
        }
        else
        {
            query.Criteria.AddCondition(CustomAPI.Fields.UniqueName, ConditionOperator.Equal, uniqueNameOrId);
        }

        await using var apiClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var apiResults = await RetrieveMultipleAsync(query, apiClient, cancellationToken);
        var apiEntity = apiResults.Entities.FirstOrDefault();
        if (apiEntity is null) return null;

        // Fetch request parameters and response properties in parallel
        var apiId = apiEntity.Id;

        await using var reqClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var reqQuery = BuildRequestParameterQuery(apiId);
        var reqResults = await RetrieveMultipleAsync(reqQuery, reqClient, cancellationToken);

        await using var respClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var respQuery = BuildResponsePropertyQuery(apiId);
        var respResults = await RetrieveMultipleAsync(respQuery, respClient, cancellationToken);

        var requestParams = reqResults.Entities.Select(MapToParameterInfo).ToList();
        var responseProps = respResults.Entities.Select(MapToParameterInfo).ToList();

        return MapToInfo(apiEntity, requestParams, responseProps);
    }

    #endregion

    #region Register Operations

    /// <inheritdoc />
    public async Task<Guid> RegisterAsync(
        CustomApiRegistration registration,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        // Validate unique name
        if (string.IsNullOrWhiteSpace(registration.UniqueName))
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.ValidationFailed,
                "UniqueName is required.");
        }

        // Validate binding type / bound entity
        if (string.Equals(registration.BindingType, "Entity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(registration.BindingType, "EntityCollection", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(registration.BoundEntity))
            {
                throw new PpdsException(
                    ErrorCodes.CustomApi.ValidationFailed,
                    $"BoundEntity is required when BindingType is '{registration.BindingType}'.");
            }
        }

        // Check name uniqueness
        await EnsureUniqueNameAvailableAsync(registration.UniqueName, cancellationToken);

        // Build and create the API entity
        var entity = BuildApiCreateEntity(registration);
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var apiId = await CreateAsync(entity, client, cancellationToken);

        _logger.LogInformation("Registered Custom API '{Name}' (ID: {Id})", registration.UniqueName, apiId);

        // Create parameters/properties if provided
        var parameters = registration.Parameters;
        if (parameters is { Count: > 0 })
        {
            progressReporter?.ReportPhase("Creating parameters", $"{parameters.Count} parameter(s)");
            for (var i = 0; i < parameters.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progressReporter?.ReportProgress(new ProgressSnapshot
                {
                    CurrentItem = i,
                    TotalItems = parameters.Count,
                    CurrentEntity = parameters[i].UniqueName,
                    StatusMessage = "Creating parameter"
                });
                await AddParameterInternalAsync(apiId, parameters[i], cancellationToken);
            }
        }

        return apiId;
    }

    #endregion

    #region Update Operations

    /// <inheritdoc />
    public async Task UpdateAsync(
        Guid id,
        CustomApiUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetByIdInternalAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.NotFound,
                $"Custom API with ID '{id}' was not found.");
        }

        var update = new Entity(CustomAPI.EntityLogicalName) { Id = id };
        var hasChanges = false;

        if (request.DisplayName is not null)
        {
            update[CustomAPI.Fields.DisplayName] = request.DisplayName;
            hasChanges = true;
        }

        if (request.Description is not null)
        {
            update[CustomAPI.Fields.Description] = request.Description;
            hasChanges = true;
        }

        if (request.PluginTypeId.HasValue)
        {
            update[CustomAPI.Fields.PluginTypeId] = new EntityReference("plugintype", request.PluginTypeId.Value);
            hasChanges = true;
        }

        if (request.IsFunction.HasValue)
        {
            update[CustomAPI.Fields.IsFunction] = request.IsFunction.Value;
            hasChanges = true;
        }

        if (request.IsPrivate.HasValue)
        {
            update[CustomAPI.Fields.IsPrivate] = request.IsPrivate.Value;
            hasChanges = true;
        }

        if (request.ExecutePrivilegeName is not null)
        {
            update[CustomAPI.Fields.ExecutePrivilegeName] = request.ExecutePrivilegeName;
            hasChanges = true;
        }

        if (request.AllowedProcessingStepType is not null)
        {
            update[CustomAPI.Fields.AllowedCustomProcessingStepType] =
                new OptionSetValue(MapProcessingStepTypeToValue(request.AllowedProcessingStepType));
            hasChanges = true;
        }

        if (!hasChanges) return;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await UpdateEntityAsync(update, client, cancellationToken);
    }

    #endregion

    #region Unregister Operations

    /// <inheritdoc />
    public async Task UnregisterAsync(
        Guid id,
        bool force = false,
        IProgressReporter? progressReporter = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetByIdInternalAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.NotFound,
                $"Custom API with ID '{id}' was not found.");
        }

        // Fetch request parameters and response properties
        await using var reqClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var reqResults = await RetrieveMultipleAsync(BuildRequestParameterQuery(id), reqClient, cancellationToken);

        await using var respClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var respResults = await RetrieveMultipleAsync(BuildResponsePropertyQuery(id), respClient, cancellationToken);

        var allDependents = reqResults.Entities.Concat(respResults.Entities).ToList();

        if (allDependents.Count > 0 && !force)
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.HasDependents,
                $"Custom API '{existing.UniqueName}' has {allDependents.Count} parameter(s)/property(ies). " +
                "Use --force to cascade delete.");
        }

        if (allDependents.Count > 0)
        {
            progressReporter?.ReportPhase("Deleting parameters", $"{allDependents.Count} item(s)");
            for (var i = 0; i < allDependents.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var dep = allDependents[i];
                progressReporter?.ReportProgress(new ProgressSnapshot
                {
                    CurrentItem = i,
                    TotalItems = allDependents.Count,
                    CurrentEntity = dep.GetAttributeValue<string>("uniquename"),
                    StatusMessage = "Deleting parameter"
                });
                await using var depClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
                await DeleteEntityAsync(dep.LogicalName, dep.Id, depClient, cancellationToken);
            }
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteEntityAsync(CustomAPI.EntityLogicalName, id, client, cancellationToken);

        _logger.LogInformation("Unregistered Custom API '{Name}' (ID: {Id})", existing.UniqueName, id);
    }

    #endregion

    #region Parameter Operations

    /// <inheritdoc />
    public async Task<Guid> AddParameterAsync(
        Guid apiId,
        CustomApiParameterRegistration parameter,
        CancellationToken cancellationToken = default)
    {
        ValidateParameterRegistration(parameter);
        return await AddParameterInternalAsync(apiId, parameter, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UpdateParameterAsync(
        Guid parameterId,
        CustomApiParameterUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var existing = await FindParameterByIdAsync(parameterId, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.ParameterNotFound,
                $"Custom API parameter with ID '{parameterId}' was not found.");
        }

        var update = new Entity(existing.LogicalName) { Id = parameterId };
        var hasChanges = false;

        if (request.DisplayName is not null)
        {
            update[CustomAPIRequestParameter.Fields.DisplayName] = request.DisplayName;
            hasChanges = true;
        }

        if (request.Description is not null)
        {
            update[CustomAPIRequestParameter.Fields.Description] = request.Description;
            hasChanges = true;
        }

        if (!hasChanges) return;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await UpdateEntityAsync(update, client, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveParameterAsync(Guid parameterId, CancellationToken cancellationToken = default)
    {
        var existing = await FindParameterByIdAsync(parameterId, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.ParameterNotFound,
                $"Custom API parameter with ID '{parameterId}' was not found.");
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteEntityAsync(existing.LogicalName, parameterId, client, cancellationToken);
    }

    #endregion

    #region Private Helpers

    private async Task<CustomApiInfo?> GetByIdInternalAsync(Guid id, CancellationToken cancellationToken)
    {
        var query = BuildApiListQuery();
        query.Criteria.AddCondition(CustomAPI.Fields.CustomAPIId, ConditionOperator.Equal, id);
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } e
            ? MapToInfo(e, requestParameters: [], responseProperties: [])
            : null;
    }

    private async Task EnsureUniqueNameAvailableAsync(string uniqueName, CancellationToken cancellationToken)
    {
        var query = new QueryExpression(CustomAPI.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(CustomAPI.Fields.UniqueName),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(CustomAPI.Fields.UniqueName, ConditionOperator.Equal, uniqueName)
                }
            },
            TopCount = 1
        };
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        if (results.Entities.Count > 0)
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.NameInUse,
                $"A Custom API with unique name '{uniqueName}' already exists.");
        }
    }

    private async Task<Entity?> FindParameterByIdAsync(Guid parameterId, CancellationToken cancellationToken)
    {
        // Try request parameters first
        var reqQuery = new QueryExpression(CustomAPIRequestParameter.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                CustomAPIRequestParameter.Fields.UniqueName,
                CustomAPIRequestParameter.Fields.DisplayName,
                CustomAPIRequestParameter.Fields.Description,
                CustomAPIRequestParameter.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(
                        CustomAPIRequestParameter.Fields.CustomAPIRequestParameterId,
                        ConditionOperator.Equal,
                        parameterId)
                }
            },
            TopCount = 1
        };

        await using var reqClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var reqResults = await RetrieveMultipleAsync(reqQuery, reqClient, cancellationToken);
        if (reqResults.Entities.Count > 0) return reqResults.Entities[0];

        // Try response properties
        var respQuery = new QueryExpression(CustomAPIResponseProperty.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                CustomAPIResponseProperty.Fields.UniqueName,
                CustomAPIResponseProperty.Fields.DisplayName,
                CustomAPIResponseProperty.Fields.Description,
                CustomAPIResponseProperty.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(
                        CustomAPIResponseProperty.Fields.CustomAPIResponsePropertyId,
                        ConditionOperator.Equal,
                        parameterId)
                }
            },
            TopCount = 1
        };

        await using var respClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var respResults = await RetrieveMultipleAsync(respQuery, respClient, cancellationToken);
        return respResults.Entities.FirstOrDefault();
    }

    private async Task<Guid> AddParameterInternalAsync(
        Guid apiId,
        CustomApiParameterRegistration parameter,
        CancellationToken cancellationToken)
    {
        var isRequest = string.Equals(parameter.Direction, "Request", StringComparison.OrdinalIgnoreCase);
        var entityName = isRequest
            ? CustomAPIRequestParameter.EntityLogicalName
            : CustomAPIResponseProperty.EntityLogicalName;

        var entity = new Entity(entityName);
        entity[CustomAPIRequestParameter.Fields.CustomAPIId] =
            new EntityReference(CustomAPI.EntityLogicalName, apiId);
        entity[CustomAPIRequestParameter.Fields.UniqueName] = parameter.UniqueName;
        entity[CustomAPIRequestParameter.Fields.DisplayName] = parameter.DisplayName;
        entity[CustomAPIRequestParameter.Fields.Type] = new OptionSetValue(MapParameterTypeToValue(parameter.Type));

        if (!string.IsNullOrWhiteSpace(parameter.Name))
            entity[CustomAPIRequestParameter.Fields.Name] = parameter.Name;

        if (!string.IsNullOrWhiteSpace(parameter.Description))
            entity[CustomAPIRequestParameter.Fields.Description] = parameter.Description;

        if (!string.IsNullOrWhiteSpace(parameter.LogicalEntityName))
            entity[CustomAPIRequestParameter.Fields.LogicalEntityName] = parameter.LogicalEntityName;

        if (isRequest)
            entity[CustomAPIRequestParameter.Fields.IsOptional] = parameter.IsOptional;

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        return await CreateAsync(entity, client, cancellationToken);
    }

    private static void ValidateParameterRegistration(CustomApiParameterRegistration parameter)
    {
        // Validate Direction
        if (!string.Equals(parameter.Direction, "Request", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(parameter.Direction, "Response", StringComparison.OrdinalIgnoreCase))
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.ValidationFailed,
                $"Direction '{parameter.Direction}' is invalid. Must be 'Request' or 'Response'.");
        }

        // Types requiring logical entity name
        var entityTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Entity", "EntityCollection", "EntityReference"
        };
        if (entityTypes.Contains(parameter.Type) && string.IsNullOrWhiteSpace(parameter.LogicalEntityName))
        {
            throw new PpdsException(
                ErrorCodes.CustomApi.ValidationFailed,
                $"LogicalEntityName is required when Type is '{parameter.Type}'.");
        }
    }

    private static QueryExpression BuildApiListQuery() =>
        new(CustomAPI.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                CustomAPI.Fields.UniqueName,
                CustomAPI.Fields.DisplayName,
                CustomAPI.Fields.Name,
                CustomAPI.Fields.Description,
                CustomAPI.Fields.PluginTypeId,
                CustomAPI.Fields.BindingType,
                CustomAPI.Fields.BoundEntityLogicalName,
                CustomAPI.Fields.AllowedCustomProcessingStepType,
                CustomAPI.Fields.IsFunction,
                CustomAPI.Fields.IsPrivate,
                CustomAPI.Fields.ExecutePrivilegeName,
                CustomAPI.Fields.IsManaged,
                CustomAPI.Fields.CreatedOn,
                CustomAPI.Fields.ModifiedOn),
            Orders = { new OrderExpression(CustomAPI.Fields.UniqueName, OrderType.Ascending) }
        };

    private static QueryExpression BuildRequestParameterQuery(Guid apiId) =>
        new(CustomAPIRequestParameter.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                CustomAPIRequestParameter.Fields.UniqueName,
                CustomAPIRequestParameter.Fields.DisplayName,
                CustomAPIRequestParameter.Fields.Name,
                CustomAPIRequestParameter.Fields.Description,
                CustomAPIRequestParameter.Fields.Type,
                CustomAPIRequestParameter.Fields.LogicalEntityName,
                CustomAPIRequestParameter.Fields.IsOptional,
                CustomAPIRequestParameter.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(
                        CustomAPIRequestParameter.Fields.CustomAPIId,
                        ConditionOperator.Equal,
                        apiId)
                }
            },
            Orders = { new OrderExpression(CustomAPIRequestParameter.Fields.UniqueName, OrderType.Ascending) }
        };

    private static QueryExpression BuildResponsePropertyQuery(Guid apiId) =>
        new(CustomAPIResponseProperty.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                CustomAPIResponseProperty.Fields.UniqueName,
                CustomAPIResponseProperty.Fields.DisplayName,
                CustomAPIResponseProperty.Fields.Name,
                CustomAPIResponseProperty.Fields.Description,
                CustomAPIResponseProperty.Fields.Type,
                CustomAPIResponseProperty.Fields.LogicalEntityName,
                CustomAPIResponseProperty.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(
                        CustomAPIResponseProperty.Fields.CustomAPIId,
                        ConditionOperator.Equal,
                        apiId)
                }
            },
            Orders = { new OrderExpression(CustomAPIResponseProperty.Fields.UniqueName, OrderType.Ascending) }
        };

    private static Entity BuildApiCreateEntity(CustomApiRegistration registration)
    {
        var entity = new Entity(CustomAPI.EntityLogicalName);
        entity[CustomAPI.Fields.UniqueName] = registration.UniqueName;
        entity[CustomAPI.Fields.DisplayName] = registration.DisplayName;

        if (!string.IsNullOrWhiteSpace(registration.Name))
            entity[CustomAPI.Fields.Name] = registration.Name;

        if (!string.IsNullOrWhiteSpace(registration.Description))
            entity[CustomAPI.Fields.Description] = registration.Description;

        entity[CustomAPI.Fields.PluginTypeId] = new EntityReference("plugintype", registration.PluginTypeId);

        entity[CustomAPI.Fields.BindingType] =
            new OptionSetValue(MapBindingTypeToValue(registration.BindingType));

        if (!string.IsNullOrWhiteSpace(registration.BoundEntity))
            entity[CustomAPI.Fields.BoundEntityLogicalName] = registration.BoundEntity;

        entity[CustomAPI.Fields.IsFunction] = registration.IsFunction;
        entity[CustomAPI.Fields.IsPrivate] = registration.IsPrivate;

        if (!string.IsNullOrWhiteSpace(registration.ExecutePrivilegeName))
            entity[CustomAPI.Fields.ExecutePrivilegeName] = registration.ExecutePrivilegeName;

        entity[CustomAPI.Fields.AllowedCustomProcessingStepType] =
            new OptionSetValue(MapProcessingStepTypeToValue(registration.AllowedProcessingStepType));

        return entity;
    }

    private static CustomApiInfo MapToInfo(
        Entity e,
        List<CustomApiParameterInfo> requestParameters,
        List<CustomApiParameterInfo> responseProperties)
    {
        var bindingValue = e.GetAttributeValue<OptionSetValue>(CustomAPI.Fields.BindingType)?.Value ?? 0;
        var stepTypeValue = e.GetAttributeValue<OptionSetValue>(CustomAPI.Fields.AllowedCustomProcessingStepType)?.Value ?? 0;
        var pluginTypeRef = e.GetAttributeValue<EntityReference>(CustomAPI.Fields.PluginTypeId);

        return new CustomApiInfo
        {
            Id = e.Id,
            UniqueName = e.GetAttributeValue<string>(CustomAPI.Fields.UniqueName) ?? "",
            DisplayName = e.GetAttributeValue<string>(CustomAPI.Fields.DisplayName) ?? "",
            Name = e.GetAttributeValue<string>(CustomAPI.Fields.Name),
            Description = e.GetAttributeValue<string>(CustomAPI.Fields.Description),
            PluginTypeId = pluginTypeRef?.Id,
            PluginTypeName = pluginTypeRef?.Name,
            BindingType = MapBindingTypeFromValue(bindingValue),
            BoundEntity = e.GetAttributeValue<string>(CustomAPI.Fields.BoundEntityLogicalName),
            AllowedProcessingStepType = MapProcessingStepTypeFromValue(stepTypeValue),
            IsFunction = e.GetAttributeValue<bool?>(CustomAPI.Fields.IsFunction) ?? false,
            IsPrivate = e.GetAttributeValue<bool?>(CustomAPI.Fields.IsPrivate) ?? false,
            ExecutePrivilegeName = e.GetAttributeValue<string>(CustomAPI.Fields.ExecutePrivilegeName),
            IsManaged = e.GetAttributeValue<bool?>(CustomAPI.Fields.IsManaged) ?? false,
            CreatedOn = e.GetAttributeValue<DateTime?>(CustomAPI.Fields.CreatedOn),
            ModifiedOn = e.GetAttributeValue<DateTime?>(CustomAPI.Fields.ModifiedOn),
            RequestParameters = requestParameters,
            ResponseProperties = responseProperties
        };
    }

    private static CustomApiParameterInfo MapToParameterInfo(Entity e)
    {
        var typeValue = e.GetAttributeValue<OptionSetValue>(CustomAPIRequestParameter.Fields.Type)?.Value ?? 0;

        return new CustomApiParameterInfo
        {
            Id = e.Id,
            UniqueName = e.GetAttributeValue<string>(CustomAPIRequestParameter.Fields.UniqueName) ?? "",
            DisplayName = e.GetAttributeValue<string>(CustomAPIRequestParameter.Fields.DisplayName) ?? "",
            Name = e.GetAttributeValue<string>(CustomAPIRequestParameter.Fields.Name),
            Description = e.GetAttributeValue<string>(CustomAPIRequestParameter.Fields.Description),
            Type = MapParameterTypeFromValue(typeValue),
            LogicalEntityName = e.GetAttributeValue<string>(CustomAPIRequestParameter.Fields.LogicalEntityName),
            IsOptional = e.GetAttributeValue<bool?>(CustomAPIRequestParameter.Fields.IsOptional) ?? false,
            IsManaged = e.GetAttributeValue<bool?>(CustomAPIRequestParameter.Fields.IsManaged) ?? false
        };
    }

    // Value mapping helpers

    private static string MapBindingTypeFromValue(int value) => value switch
    {
        BindingTypeGlobal => "Global",
        BindingTypeEntity => "Entity",
        BindingTypeEntityCollection => "EntityCollection",
        _ => value.ToString()
    };

    private static int MapBindingTypeToValue(string? bindingType) => bindingType switch
    {
        "Entity" => BindingTypeEntity,
        "EntityCollection" => BindingTypeEntityCollection,
        _ => BindingTypeGlobal // Global is the default
    };

    private static string MapProcessingStepTypeFromValue(int value) => value switch
    {
        ProcessingStepNone => "None",
        ProcessingStepAsyncOnly => "AsyncOnly",
        ProcessingStepSyncAndAsync => "SyncAndAsync",
        _ => value.ToString()
    };

    private static int MapProcessingStepTypeToValue(string? stepType) => stepType switch
    {
        "AsyncOnly" => ProcessingStepAsyncOnly,
        "SyncAndAsync" => ProcessingStepSyncAndAsync,
        _ => ProcessingStepNone // None is the default
    };

    private static string MapParameterTypeFromValue(int value) => value switch
    {
        TypeBoolean => "Boolean",
        TypeDateTime => "DateTime",
        TypeDecimal => "Decimal",
        TypeEntity => "Entity",
        TypeEntityCollection => "EntityCollection",
        TypeEntityReference => "EntityReference",
        TypeFloat => "Float",
        TypeInteger => "Integer",
        TypeMoney => "Money",
        TypePicklist => "Picklist",
        TypeString => "String",
        TypeStringArray => "StringArray",
        TypeGuid => "Guid",
        _ => value.ToString()
    };

    private static int MapParameterTypeToValue(string type) => type switch
    {
        "Boolean" => TypeBoolean,
        "DateTime" => TypeDateTime,
        "Decimal" => TypeDecimal,
        "Entity" => TypeEntity,
        "EntityCollection" => TypeEntityCollection,
        "EntityReference" => TypeEntityReference,
        "Float" => TypeFloat,
        "Integer" => TypeInteger,
        "Money" => TypeMoney,
        "Picklist" => TypePicklist,
        "String" => TypeString,
        "StringArray" => TypeStringArray,
        "Guid" => TypeGuid,
        _ => int.TryParse(type, out var v) ? v : TypeString
    };

    // Async Dataverse helpers (same pattern as ServiceEndpointService)

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
