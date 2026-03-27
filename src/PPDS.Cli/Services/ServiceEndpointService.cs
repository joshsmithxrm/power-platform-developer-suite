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
/// Service for managing Dataverse service endpoints (Azure Service Bus, EventHub) and webhooks.
/// </summary>
/// <remarks>
/// Uses connection pooling so each method acquires its own client for safe parallel use.
/// </remarks>
public sealed class ServiceEndpointService : IServiceEndpointService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<ServiceEndpointService> _logger;

    // Contract OptionSet values
    private const int ContractOneWay = 1;
    private const int ContractQueue = 2;
    private const int ContractRest = 3;
    private const int ContractTwoWay = 4;
    private const int ContractTopic = 5;
    private const int ContractEventHub = 7;
    private const int ContractWebhook = 8;

    // AuthType OptionSet values
    private const int AuthSASKey = 2;
    private const int AuthSASToken = 3;
    private const int AuthWebhookKey = 4;
    private const int AuthHttpHeader = 5;
    private const int AuthHttpQueryString = 6;

    // MessageFormat OptionSet values
    private const int MessageFormatBinaryXml = 1;
    private const int MessageFormatJson = 2;
    private const int MessageFormatTextXml = 3;

    // UserClaim OptionSet values
    private const int UserClaimNone = 1;
    private const int UserClaimUserId = 2;

    /// <summary>
    /// Creates a new instance of <see cref="ServiceEndpointService"/>.
    /// </summary>
    public ServiceEndpointService(IDataverseConnectionPool pool, ILogger<ServiceEndpointService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Query Operations

    /// <inheritdoc />
    public async Task<List<ServiceEndpointInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var query = BuildListQuery();
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.Select(MapToInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<ServiceEndpointInfo?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var query = BuildListQuery();
        query.Criteria.AddCondition(
            ServiceEndpoint.Fields.ServiceEndpointId,
            ConditionOperator.Equal,
            id);

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } entity ? MapToInfo(entity) : null;
    }

    /// <inheritdoc />
    public async Task<ServiceEndpointInfo?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var query = BuildListQuery();
        query.Criteria.AddCondition(
            ServiceEndpoint.Fields.Name,
            ConditionOperator.Equal,
            name);

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault() is { } entity ? MapToInfo(entity) : null;
    }

    #endregion

    #region Register Operations

    /// <inheritdoc />
    public async Task<Guid> RegisterWebhookAsync(
        WebhookRegistration registration,
        CancellationToken cancellationToken = default)
    {
        // Validate URL
        if (!Uri.TryCreate(registration.Url, UriKind.Absolute, out _))
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.ValidationFailed,
                $"Webhook URL '{registration.Url}' is not a valid absolute URI.");
        }

        // Check name uniqueness
        await EnsureNameAvailableAsync(registration.Name, cancellationToken);

        // Map auth type
        var authType = MapAuthTypeToValue(registration.AuthType);

        var entity = new Entity(ServiceEndpoint.EntityLogicalName);
        entity[ServiceEndpoint.Fields.Name] = registration.Name;
        entity[ServiceEndpoint.Fields.Contract] = new OptionSetValue(ContractWebhook);
        entity[ServiceEndpoint.Fields.AuthType] = new OptionSetValue(authType);
        entity[ServiceEndpoint.Fields.Url] = registration.Url;
        entity[ServiceEndpoint.Fields.ConnectionMode] = new OptionSetValue(1); // Normal

        if (!string.IsNullOrEmpty(registration.AuthValue))
        {
            entity[ServiceEndpoint.Fields.AuthValue] = registration.AuthValue;
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        return await CreateAsync(entity, client, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Guid> RegisterServiceBusAsync(
        ServiceBusRegistration registration,
        CancellationToken cancellationToken = default)
    {
        // Validate namespace address
        if (!registration.NamespaceAddress.StartsWith("sb://", StringComparison.OrdinalIgnoreCase))
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.ValidationFailed,
                $"Namespace address '{registration.NamespaceAddress}' must start with 'sb://'.");
        }

        // Validate SAS key length when provided
        if (!string.IsNullOrEmpty(registration.SasKey) && registration.SasKey.Length != 44)
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.ValidationFailed,
                "SAS key must be exactly 44 characters.");
        }

        // Check name uniqueness
        await EnsureNameAvailableAsync(registration.Name, cancellationToken);

        var contractValue = MapContractToValue(registration.Contract);
        var authType = MapAuthTypeToValue(registration.AuthType);

        var entity = new Entity(ServiceEndpoint.EntityLogicalName);
        entity[ServiceEndpoint.Fields.Name] = registration.Name;
        entity[ServiceEndpoint.Fields.Contract] = new OptionSetValue(contractValue);
        entity[ServiceEndpoint.Fields.AuthType] = new OptionSetValue(authType);
        entity[ServiceEndpoint.Fields.NamespaceAddress] = registration.NamespaceAddress;
        entity[ServiceEndpoint.Fields.Path] = registration.Path;
        entity[ServiceEndpoint.Fields.ConnectionMode] = new OptionSetValue(1); // Normal
        entity[ServiceEndpoint.Fields.NamespaceFormat] = new OptionSetValue(2); // NamespaceAddress

        if (!string.IsNullOrEmpty(registration.SasKeyName))
        {
            entity[ServiceEndpoint.Fields.SASKeyName] = registration.SasKeyName;
        }

        if (!string.IsNullOrEmpty(registration.SasKey))
        {
            entity[ServiceEndpoint.Fields.SASKey] = registration.SasKey;
        }

        if (!string.IsNullOrEmpty(registration.SasToken))
        {
            entity[ServiceEndpoint.Fields.SASToken] = registration.SasToken;
        }

        if (!string.IsNullOrEmpty(registration.MessageFormat))
        {
            entity[ServiceEndpoint.Fields.MessageFormat] = new OptionSetValue(MapMessageFormatToValue(registration.MessageFormat));
        }

        if (!string.IsNullOrEmpty(registration.UserClaim))
        {
            entity[ServiceEndpoint.Fields.UserClaim] = new OptionSetValue(MapUserClaimToValue(registration.UserClaim));
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        return await CreateAsync(entity, client, cancellationToken);
    }

    #endregion

    #region Update Operations

    /// <inheritdoc />
    public async Task UpdateAsync(
        Guid id,
        ServiceEndpointUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Fetch the existing endpoint to verify it exists and check managed state
        var existing = await GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.NotFound,
                $"Service endpoint with ID '{id}' was not found.");
        }

        var update = new Entity(ServiceEndpoint.EntityLogicalName) { Id = id };
        var hasChanges = false;

        if (request.Name is not null)
        {
            update[ServiceEndpoint.Fields.Name] = request.Name;
            hasChanges = true;
        }

        if (request.Description is not null)
        {
            update[ServiceEndpoint.Fields.Description] = request.Description;
            hasChanges = true;
        }

        if (request.Url is not null)
        {
            update[ServiceEndpoint.Fields.Url] = request.Url;
            hasChanges = true;
        }

        if (request.AuthType is not null)
        {
            update[ServiceEndpoint.Fields.AuthType] = new OptionSetValue(MapAuthTypeToValue(request.AuthType));
            hasChanges = true;
        }

        if (request.AuthValue is not null)
        {
            update[ServiceEndpoint.Fields.AuthValue] = request.AuthValue;
            hasChanges = true;
        }

        if (!hasChanges)
        {
            return;
        }

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
        var existing = await GetByIdAsync(id, cancellationToken);
        if (existing is null)
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.NotFound,
                $"Service endpoint with ID '{id}' was not found.");
        }

        // Find dependent steps
        var dependentSteps = await ListDependentStepsAsync(id, cancellationToken);

        if (dependentSteps.Count > 0 && !force)
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.HasDependents,
                $"Service endpoint '{existing.Name}' has {dependentSteps.Count} dependent step registration(s). " +
                "Use --force to cascade delete.");
        }

        if (dependentSteps.Count > 0)
        {
            progressReporter?.ReportPhase("Deleting dependent steps", $"{dependentSteps.Count} step(s)");

            for (var i = 0; i < dependentSteps.Count; i++)
            {
                var step = dependentSteps[i];
                cancellationToken.ThrowIfCancellationRequested();

                progressReporter?.ReportProgress(new ProgressSnapshot
                {
                    CurrentItem = i,
                    TotalItems = dependentSteps.Count,
                    CurrentEntity = step.GetAttributeValue<string>("name"),
                    StatusMessage = "Deleting step"
                });

                await using var stepClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
                await DeleteEntityAsync(SdkMessageProcessingStep.EntityLogicalName, step.Id, stepClient, cancellationToken);
            }
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteEntityAsync(ServiceEndpoint.EntityLogicalName, id, client, cancellationToken);

        _logger.LogInformation("Unregistered service endpoint '{Name}' (ID: {Id})", existing.Name, id);
    }

    #endregion

    #region Private Helpers

    private async Task EnsureNameAvailableAsync(string name, CancellationToken cancellationToken)
    {
        var existing = await GetByNameAsync(name, cancellationToken);
        if (existing is not null)
        {
            throw new PpdsException(
                ErrorCodes.ServiceEndpoint.NameInUse,
                $"A service endpoint named '{name}' already exists.");
        }
    }

    private async Task<List<Entity>> ListDependentStepsAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression(SdkMessageProcessingStep.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStep.Fields.Name,
                SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    // eventhandler is a polymorphic lookup; filter by objectid/objecttypecode
                    // to find steps registered on this specific service endpoint
                    new ConditionExpression(
                        SdkMessageProcessingStep.Fields.EventHandler,
                        ConditionOperator.Equal,
                        endpointId)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.ToList();
    }

    private static QueryExpression BuildListQuery() =>
        new(ServiceEndpoint.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                ServiceEndpoint.Fields.Name,
                ServiceEndpoint.Fields.Description,
                ServiceEndpoint.Fields.Contract,
                ServiceEndpoint.Fields.AuthType,
                ServiceEndpoint.Fields.Url,
                ServiceEndpoint.Fields.NamespaceAddress,
                ServiceEndpoint.Fields.Path,
                ServiceEndpoint.Fields.MessageFormat,
                ServiceEndpoint.Fields.UserClaim,
                ServiceEndpoint.Fields.IsManaged,
                ServiceEndpoint.Fields.CreatedOn,
                ServiceEndpoint.Fields.ModifiedOn),
            Orders = { new OrderExpression(ServiceEndpoint.Fields.Name, OrderType.Ascending) }
        };

    private static ServiceEndpointInfo MapToInfo(Entity e)
    {
        var contractValue = e.GetAttributeValue<OptionSetValue>(ServiceEndpoint.Fields.Contract)?.Value ?? 0;
        var authTypeValue = e.GetAttributeValue<OptionSetValue>(ServiceEndpoint.Fields.AuthType)?.Value ?? 0;
        var messageFormatValue = e.GetAttributeValue<OptionSetValue>(ServiceEndpoint.Fields.MessageFormat)?.Value;
        var userClaimValue = e.GetAttributeValue<OptionSetValue>(ServiceEndpoint.Fields.UserClaim)?.Value;

        var contractType = MapContractFromValue(contractValue);
        var isWebhook = contractValue == ContractWebhook;

        return new ServiceEndpointInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(ServiceEndpoint.Fields.Name) ?? string.Empty,
            Description = e.GetAttributeValue<string>(ServiceEndpoint.Fields.Description),
            ContractType = contractType,
            IsWebhook = isWebhook,
            Url = e.GetAttributeValue<string>(ServiceEndpoint.Fields.Url),
            NamespaceAddress = e.GetAttributeValue<string>(ServiceEndpoint.Fields.NamespaceAddress),
            Path = e.GetAttributeValue<string>(ServiceEndpoint.Fields.Path),
            AuthType = MapAuthTypeFromValue(authTypeValue),
            MessageFormat = messageFormatValue.HasValue ? MapMessageFormatFromValue(messageFormatValue.Value) : null,
            UserClaim = userClaimValue.HasValue ? MapUserClaimFromValue(userClaimValue.Value) : null,
            IsManaged = e.GetAttributeValue<bool?>(ServiceEndpoint.Fields.IsManaged) ?? false,
            CreatedOn = e.GetAttributeValue<DateTime?>(ServiceEndpoint.Fields.CreatedOn),
            ModifiedOn = e.GetAttributeValue<DateTime?>(ServiceEndpoint.Fields.ModifiedOn)
        };
    }

    // Value mapping helpers

    private static string MapContractFromValue(int value) => value switch
    {
        ContractOneWay => "OneWay",
        ContractQueue => "Queue",
        ContractRest => "Rest",
        ContractTwoWay => "TwoWay",
        ContractTopic => "Topic",
        ContractEventHub => "EventHub",
        ContractWebhook => "Webhook",
        _ => value.ToString()
    };

    private static int MapContractToValue(string contract) => contract switch
    {
        "OneWay" => ContractOneWay,
        "Queue" => ContractQueue,
        "Rest" => ContractRest,
        "TwoWay" => ContractTwoWay,
        "Topic" => ContractTopic,
        "EventHub" => ContractEventHub,
        "Webhook" => ContractWebhook,
        _ => int.TryParse(contract, out var v) ? v : ContractQueue
    };

    private static string MapAuthTypeFromValue(int value) => value switch
    {
        AuthSASKey => "SASKey",
        AuthSASToken => "SASToken",
        AuthWebhookKey => "WebhookKey",
        AuthHttpHeader => "HttpHeader",
        AuthHttpQueryString => "HttpQueryString",
        _ => value.ToString()
    };

    private static int MapAuthTypeToValue(string authType) => authType switch
    {
        "SASKey" => AuthSASKey,
        "SASToken" => AuthSASToken,
        "WebhookKey" => AuthWebhookKey,
        "HttpHeader" => AuthHttpHeader,
        "HttpQueryString" => AuthHttpQueryString,
        _ => int.TryParse(authType, out var v) ? v : AuthWebhookKey
    };

    private static string MapMessageFormatFromValue(int value) => value switch
    {
        MessageFormatBinaryXml => "BinaryXML",
        MessageFormatJson => "Json",
        MessageFormatTextXml => "TextXML",
        _ => value.ToString()
    };

    private static int MapMessageFormatToValue(string format) => format switch
    {
        "BinaryXML" => MessageFormatBinaryXml,
        "Json" => MessageFormatJson,
        "TextXML" => MessageFormatTextXml,
        _ => int.TryParse(format, out var v) ? v : MessageFormatJson
    };

    private static string MapUserClaimFromValue(int value) => value switch
    {
        UserClaimNone => "None",
        UserClaimUserId => "UserId",
        _ => value.ToString()
    };

    private static int MapUserClaimToValue(string userClaim) => userClaim switch
    {
        "None" => UserClaimNone,
        "UserId" => UserClaimUserId,
        _ => int.TryParse(userClaim, out var v) ? v : UserClaimNone
    };

    // Async Dataverse helpers (same pattern as PluginRegistrationService)

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
