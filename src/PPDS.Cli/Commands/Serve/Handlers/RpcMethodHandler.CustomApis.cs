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
    #region Custom APIs

    // ── Custom APIs ──

    /// <summary>
    /// Lists all Custom APIs with parameters in the environment.
    /// Maps to: ppds custom-apis list --json
    /// </summary>
    [JsonRpcMethod("customApis/list")]
    public async Task<CustomApisListResponse> CustomApisListAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            var apis = await service.ListAsync(ct);

            return new CustomApisListResponse
            {
                Apis = apis.Select(MapCustomApiToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single Custom API by unique name or ID.
    /// Maps to: ppds custom-apis get --json
    /// </summary>
    [JsonRpcMethod("customApis/get")]
    public async Task<CustomApisGetResponse> CustomApisGetAsync(
        string uniqueNameOrId,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uniqueNameOrId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'uniqueNameOrId' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            var api = await service.GetAsync(uniqueNameOrId, ct)
                ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Custom API '{uniqueNameOrId}' not found");

            return new CustomApisGetResponse
            {
                Api = MapCustomApiToDto(api)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers a new Custom API, optionally with request/response parameters.
    /// Maps to: ppds custom-apis register --json
    /// </summary>
    [JsonRpcMethod("customApis/register")]
    public async Task<CustomApisRegisterResponse> CustomApisRegisterAsync(
        string uniqueName,
        string displayName,
        string pluginTypeId,
        string? name = null,
        string? description = null,
        string? bindingType = null,
        string? boundEntity = null,
        bool isFunction = false,
        bool isPrivate = false,
        string? executePrivilegeName = null,
        string? allowedProcessingStepType = null,
        List<CustomApiParameterDto>? parameters = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'uniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'displayName' parameter is required");
        if (string.IsNullOrWhiteSpace(pluginTypeId) || !Guid.TryParse(pluginTypeId, out var pluginTypeGuid))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'pluginTypeId' parameter must be a valid GUID");

        // Validate each nested parameter with the same rules as customApis/addParameter
        // so a null/whitespace field is rejected up front rather than silently coerced
        // to an empty string downstream (issue #1228).
        if (parameters is not null)
        {
            for (var i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                if (p is null)
                    throw new RpcException(ErrorCodes.Validation.RequiredField, $"parameter[{i}] cannot be null");
                if (string.IsNullOrWhiteSpace(p.UniqueName))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, $"The 'uniqueName' field of parameter[{i}] is required");
                if (string.IsNullOrWhiteSpace(p.DisplayName))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, $"The 'displayName' field of parameter[{i}] is required");
                if (string.IsNullOrWhiteSpace(p.Type))
                    throw new RpcException(ErrorCodes.Validation.RequiredField, $"The 'type' field of parameter[{i}] is required");
            }
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();

            var paramRegistrations = parameters?
                .Select(p => new CustomApiParameterRegistration(
                    p.UniqueName!,
                    p.DisplayName!,
                    p.Name,
                    p.Description,
                    p.Type!,
                    p.LogicalEntityName,
                    p.IsOptional,
                    p.Direction ?? "Request"))
                .ToList();

            var newId = await service.RegisterAsync(
                new CustomApiRegistration(
                    uniqueName,
                    displayName,
                    name,
                    description,
                    pluginTypeGuid,
                    bindingType,
                    boundEntity,
                    isFunction,
                    isPrivate,
                    executePrivilegeName,
                    allowedProcessingStepType,
                    paramRegistrations),
                cancellationToken: ct);

            return new CustomApisRegisterResponse { Id = newId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates mutable properties of an existing Custom API.
    /// Maps to: ppds custom-apis update --json
    /// </summary>
    [JsonRpcMethod("customApis/update")]
    public async Task<CustomApisUpdateResponse> CustomApisUpdateAsync(
        string id,
        string? displayName = null,
        string? description = null,
        string? pluginTypeId = null,
        bool? isFunction = null,
        bool? isPrivate = null,
        string? executePrivilegeName = null,
        string? allowedProcessingStepType = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var apiId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        Guid? pluginTypeGuid = null;
        if (!string.IsNullOrWhiteSpace(pluginTypeId))
        {
            if (!Guid.TryParse(pluginTypeId, out var ptg))
                throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'pluginTypeId' parameter must be a valid GUID");
            pluginTypeGuid = ptg;
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            await service.UpdateAsync(
                apiId,
                new CustomApiUpdateRequest(
                    displayName,
                    description,
                    pluginTypeGuid,
                    isFunction,
                    isPrivate,
                    executePrivilegeName,
                    allowedProcessingStepType),
                ct);

            return new CustomApisUpdateResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Unregisters a Custom API and optionally cascade-deletes its parameters.
    /// Maps to: ppds custom-apis unregister --json
    /// </summary>
    [JsonRpcMethod("customApis/unregister")]
    public async Task<CustomApisUnregisterResponse> CustomApisUnregisterAsync(
        string id,
        bool force = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var apiId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            await service.UnregisterAsync(apiId, force, cancellationToken: ct);

            return new CustomApisUnregisterResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Adds a request parameter or response property to an existing Custom API.
    /// Maps to: ppds custom-apis add-parameter --json
    /// </summary>
    [JsonRpcMethod("customApis/addParameter")]
    public async Task<CustomApisAddParameterResponse> CustomApisAddParameterAsync(
        string apiId,
        string uniqueName,
        string displayName,
        string type,
        string direction,
        string? name = null,
        string? description = null,
        string? logicalEntityName = null,
        bool isOptional = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiId) || !Guid.TryParse(apiId, out var apiGuid))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'apiId' parameter must be a valid GUID");
        if (string.IsNullOrWhiteSpace(uniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'uniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'displayName' parameter is required");
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(direction))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'direction' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            var newId = await service.AddParameterAsync(
                apiGuid,
                new CustomApiParameterRegistration(
                    uniqueName,
                    displayName,
                    name,
                    description,
                    type,
                    logicalEntityName,
                    isOptional,
                    direction),
                ct);

            return new CustomApisAddParameterResponse { Id = newId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates mutable properties (display name, description) of a parameter.
    /// Maps to: ppds custom-apis update-parameter --json
    /// </summary>
    [JsonRpcMethod("customApis/updateParameter")]
    public async Task<CustomApisUpdateParameterResponse> CustomApisUpdateParameterAsync(
        string parameterId,
        string? displayName = null,
        string? description = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameterId) || !Guid.TryParse(parameterId, out var paramGuid))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'parameterId' parameter must be a valid GUID");

        if (displayName == null && description == null)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "At least one of 'displayName' or 'description' must be provided");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            await service.UpdateParameterAsync(
                paramGuid,
                new CustomApiParameterUpdateRequest(displayName, description),
                ct);

            return new CustomApisUpdateParameterResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Removes a request parameter or response property by ID.
    /// Maps to: ppds custom-apis remove-parameter --json
    /// </summary>
    [JsonRpcMethod("customApis/removeParameter")]
    public async Task<CustomApisRemoveParameterResponse> CustomApisRemoveParameterAsync(
        string parameterId,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(parameterId) || !Guid.TryParse(parameterId, out var paramGuid))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'parameterId' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ICustomApiService>();
            await service.RemoveParameterAsync(paramGuid, ct);

            return new CustomApisRemoveParameterResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Sets or clears the implementing plugin type on a Custom API.
    /// Maps to: ppds custom-apis set-plugin --json
    /// </summary>
    [JsonRpcMethod("customApis/setPlugin")]
    public async Task<CustomApisSetPluginResponse> CustomApisSetPluginAsync(
        string nameOrId,
        string? pluginTypeName = null,
        string? assemblyName = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'nameOrId' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var customApiService = sp.GetRequiredService<ICustomApiService>();

            // Resolve Custom API
            var api = await customApiService.GetAsync(nameOrId, ct)
                ?? throw new RpcException(ErrorCodes.CustomApi.NotFound, $"Custom API '{nameOrId}' not found.");

            await customApiService.SetPluginTypeAsync(api.Id, pluginTypeName, assemblyName, ct);

            return new CustomApisSetPluginResponse { Success = true };
        }, cancellationToken);
    }

    private static CustomApiDto MapCustomApiToDto(CustomApiInfo api) =>
        new()
        {
            Id = api.Id.ToString(),
            UniqueName = api.UniqueName,
            DisplayName = api.DisplayName,
            Name = api.Name,
            Description = api.Description,
            PluginTypeId = api.PluginTypeId?.ToString(),
            PluginTypeName = api.PluginTypeName,
            BindingType = api.BindingType,
            BoundEntity = api.BoundEntity,
            AllowedProcessingStepType = api.AllowedProcessingStepType,
            IsFunction = api.IsFunction,
            IsPrivate = api.IsPrivate,
            ExecutePrivilegeName = api.ExecutePrivilegeName,
            IsManaged = api.IsManaged,
            CreatedOn = api.CreatedOn?.ToString("o"),
            ModifiedOn = api.ModifiedOn?.ToString("o"),
            RequestParameters = api.RequestParameters.Select(MapCustomApiParameterToDto).ToList(),
            ResponseProperties = api.ResponseProperties.Select(MapCustomApiParameterToDto).ToList()
        };

    private static CustomApiParameterDto MapCustomApiParameterToDto(CustomApiParameterInfo p) =>
        new()
        {
            Id = p.Id.ToString(),
            UniqueName = p.UniqueName,
            DisplayName = p.DisplayName,
            Name = p.Name,
            Description = p.Description,
            Type = p.Type,
            LogicalEntityName = p.LogicalEntityName,
            IsOptional = p.IsOptional,
            IsManaged = p.IsManaged
        };

    #endregion
}

public class CustomApisListResponse
{
    [JsonPropertyName("apis")]
    public List<CustomApiDto> Apis { get; set; } = [];
}

public class CustomApisGetResponse
{
    [JsonPropertyName("api")]
    public CustomApiDto? Api { get; set; }
}

public class CustomApiDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("pluginTypeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginTypeId { get; set; }

    [JsonPropertyName("pluginTypeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginTypeName { get; set; }

    [JsonPropertyName("bindingType")]
    public string BindingType { get; set; } = "Global";

    [JsonPropertyName("boundEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BoundEntity { get; set; }

    [JsonPropertyName("allowedProcessingStepType")]
    public string AllowedProcessingStepType { get; set; } = "None";

    [JsonPropertyName("isFunction")]
    public bool IsFunction { get; set; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("executePrivilegeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutePrivilegeName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("requestParameters")]
    public List<CustomApiParameterDto> RequestParameters { get; set; } = [];

    [JsonPropertyName("responseProperties")]
    public List<CustomApiParameterDto> ResponseProperties { get; set; } = [];
}

public class CustomApiParameterDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    public string? UniqueName { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("logicalEntityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalEntityName { get; set; }

    [JsonPropertyName("isOptional")]
    public bool IsOptional { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("direction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Direction { get; set; }
}

public class CustomApisRegisterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public class CustomApisUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class CustomApisUnregisterResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class CustomApisAddParameterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public class CustomApisUpdateParameterResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class CustomApisRemoveParameterResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class CustomApisSetPluginResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

// ── Data Providers DTOs ───────────────────────────────────────────────────────
