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
    #region Service Endpoints

    // ── Service Endpoints ──

    /// <summary>
    /// Lists all service endpoints and webhooks in the environment.
    /// Maps to: ppds service-endpoints list --json
    /// </summary>
    [JsonRpcMethod("serviceEndpoints/list")]
    public async Task<ServiceEndpointsListResponse> ServiceEndpointsListAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IServiceEndpointService>();
            var endpoints = await service.ListAsync(ct);

            return new ServiceEndpointsListResponse
            {
                Endpoints = endpoints.Select(MapServiceEndpointToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single service endpoint by ID.
    /// Maps to: ppds service-endpoints get --json
    /// </summary>
    [JsonRpcMethod("serviceEndpoints/get")]
    public async Task<ServiceEndpointsGetResponse> ServiceEndpointsGetAsync(
        string id,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var endpointId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IServiceEndpointService>();
            var endpoint = await service.GetByIdAsync(endpointId, ct)
                ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Service endpoint '{id}' not found");

            return new ServiceEndpointsGetResponse
            {
                Endpoint = MapServiceEndpointToDto(endpoint)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers a new service endpoint or webhook.
    /// Maps to: ppds service-endpoints register --json
    /// </summary>
    [JsonRpcMethod("serviceEndpoints/register")]
    public async Task<ServiceEndpointsRegisterResponse> ServiceEndpointsRegisterAsync(
        string name,
        string contractType,
        // Webhook fields
        string? url = null,
        // Service Bus fields
        string? namespaceAddress = null,
        string? path = null,
        // Auth
        string? authType = null,
        string? authValue = null,
        string? sasKeyName = null,
        string? sasKey = null,
        string? sasToken = null,
        // Optional service bus extras
        string? messageFormat = null,
        string? userClaim = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(contractType))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'contractType' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IServiceEndpointService>();
            Guid newId;

            // Webhook: contractType == "Webhook"
            if (string.Equals(contractType, "Webhook", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(url))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'url' parameter is required for webhook registration");
                if (string.IsNullOrWhiteSpace(authType))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'authType' parameter is required for webhook registration");

                newId = await service.RegisterWebhookAsync(
                    new WebhookRegistration(name, url, authType, authValue),
                    ct);
            }
            else
            {
                // Service Bus: Queue, Topic, EventHub, OneWay, TwoWay, Rest
                if (string.IsNullOrWhiteSpace(namespaceAddress))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'namespaceAddress' parameter is required for service bus registration");
                if (string.IsNullOrWhiteSpace(path))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'path' parameter is required for service bus registration");
                if (string.IsNullOrWhiteSpace(authType))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'authType' parameter is required for service bus registration");

                newId = await service.RegisterServiceBusAsync(
                    new ServiceBusRegistration(
                        name,
                        namespaceAddress,
                        path,
                        contractType,
                        authType,
                        sasKeyName,
                        sasKey,
                        sasToken,
                        messageFormat,
                        userClaim),
                    ct);
            }

            return new ServiceEndpointsRegisterResponse { Id = newId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates properties of an existing service endpoint.
    /// Maps to: ppds service-endpoints update --json
    /// </summary>
    [JsonRpcMethod("serviceEndpoints/update")]
    public async Task<ServiceEndpointsUpdateResponse> ServiceEndpointsUpdateAsync(
        string id,
        string? name = null,
        string? description = null,
        string? url = null,
        string? authType = null,
        string? authValue = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var endpointId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IServiceEndpointService>();
            await service.UpdateAsync(
                endpointId,
                new ServiceEndpointUpdateRequest(name, description, url, authType, authValue),
                ct);

            return new ServiceEndpointsUpdateResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Unregisters (deletes) a service endpoint.
    /// Maps to: ppds service-endpoints unregister --json
    /// </summary>
    [JsonRpcMethod("serviceEndpoints/unregister")]
    public async Task<ServiceEndpointsUnregisterResponse> ServiceEndpointsUnregisterAsync(
        string id,
        bool force = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var endpointId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IServiceEndpointService>();
            await service.UnregisterAsync(endpointId, force, cancellationToken: ct);

            return new ServiceEndpointsUnregisterResponse { Success = true };
        }, cancellationToken);
    }

    private static ServiceEndpointDto MapServiceEndpointToDto(ServiceEndpointInfo e) =>
        new()
        {
            Id = e.Id.ToString(),
            Name = e.Name,
            Description = e.Description,
            ContractType = e.ContractType,
            IsWebhook = e.IsWebhook,
            Url = e.Url,
            NamespaceAddress = e.NamespaceAddress,
            Path = e.Path,
            AuthType = e.AuthType,
            MessageFormat = e.MessageFormat,
            UserClaim = e.UserClaim,
            IsManaged = e.IsManaged,
            CreatedOn = e.CreatedOn?.ToString("o"),
            ModifiedOn = e.ModifiedOn?.ToString("o")
        };

    #endregion
}

public class ServiceEndpointsListResponse
{
    [JsonPropertyName("endpoints")]
    public List<ServiceEndpointDto> Endpoints { get; set; } = [];
}

public class ServiceEndpointsGetResponse
{
    [JsonPropertyName("endpoint")]
    public ServiceEndpointDto? Endpoint { get; set; }
}

public class ServiceEndpointDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("contractType")]
    public string ContractType { get; set; } = "";

    [JsonPropertyName("isWebhook")]
    public bool IsWebhook { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("namespaceAddress")]
    public string? NamespaceAddress { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("authType")]
    public string AuthType { get; set; } = "";

    [JsonPropertyName("messageFormat")]
    public string? MessageFormat { get; set; }

    [JsonPropertyName("userClaim")]
    public string? UserClaim { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    public string? ModifiedOn { get; set; }
}

public class ServiceEndpointsRegisterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public class ServiceEndpointsUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class ServiceEndpointsUnregisterResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

// ── Custom APIs DTOs ─────────────────────────────────────────────────────────
