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
    #region Plugin Registration Mutations

    /// <summary>
    /// Gets detailed information about a single plugin entity by type and ID.
    /// Supports: assembly, package, type, step, image.
    /// </summary>
    [JsonRpcMethod("plugins/get")]
    public async Task<PluginsGetResponse> PluginsGetAsync(
        string type,
        string id,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var entityId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            var response = new PluginsGetResponse { Type = type };

            switch (type.ToLowerInvariant())
            {
                case "assembly":
                {
                    var asm = await registrationService.GetAssemblyByIdAsync(entityId, ct)
                        ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Assembly '{id}' not found");
                    response.Assembly = MapAssemblyInfoToDetail(asm);
                    break;
                }
                case "package":
                {
                    var pkg = await registrationService.GetPackageByIdAsync(entityId, ct)
                        ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Package '{id}' not found");
                    response.Package = MapPackageInfoToDetail(pkg);
                    break;
                }
                case "type":
                {
                    var t = await registrationService.GetPluginTypeByNameOrIdAsync(id, ct)
                        ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Plugin type '{id}' not found");
                    response.PluginType = MapPluginTypeInfoToDetail(t);
                    break;
                }
                case "step":
                {
                    var step = await registrationService.GetStepByNameOrIdAsync(id, ct)
                        ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Step '{id}' not found");
                    response.Step = MapStepInfoToDetail(step);
                    break;
                }
                case "image":
                {
                    var img = await registrationService.GetImageByNameOrIdAsync(id, ct)
                        ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Image '{id}' not found");
                    response.Image = MapImageInfoToDetail(img);
                    break;
                }
                default:
                    throw new RpcException(ErrorCodes.Validation.InvalidArguments,
                        $"Unknown type '{type}'. Valid values: assembly, package, type, step, image");
            }

            return response;
        }, cancellationToken);
    }

    /// <summary>
    /// Lists available SDK messages, optionally filtered by name.
    /// </summary>
    [JsonRpcMethod("plugins/messages")]
    public async Task<PluginsMessagesResponse> PluginsMessagesAsync(
        string? filter = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();
            var messages = await registrationService.ListMessagesAsync(filter, ct);
            return new PluginsMessagesResponse { Messages = messages };
        }, cancellationToken);
    }

    /// <summary>
    /// Returns attribute metadata for an entity.
    /// </summary>
    [JsonRpcMethod("plugins/entityAttributes")]
    public async Task<PluginsEntityAttributesResponse> PluginsEntityAttributesAsync(
        string entityLogicalName,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();
            var attributes = await registrationService.ListEntityAttributesAsync(entityLogicalName, ct);
            return new PluginsEntityAttributesResponse
            {
                Attributes = attributes.Select(a => new AttributeInfoDto
                {
                    LogicalName = a.LogicalName,
                    DisplayName = a.DisplayName,
                    AttributeType = a.AttributeType
                }).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Enables or disables a plugin processing step.
    /// </summary>
    [JsonRpcMethod("plugins/toggleStep")]
    public async Task<PluginsToggleStepResponse> PluginsToggleStepAsync(
        string id,
        bool enabled,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var stepId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            if (enabled)
                await registrationService.EnableStepAsync(stepId, ct);
            else
                await registrationService.DisableStepAsync(stepId, ct);

            return new PluginsToggleStepResponse { Id = id, Enabled = enabled };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers or updates a plugin assembly from base64-encoded DLL content.
    /// </summary>
    [JsonRpcMethod("plugins/registerAssembly")]
    public async Task<PluginsRegisterResponse> PluginsRegisterAssemblyAsync(
        string name,
        string content,
        string? solutionName = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(content))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'content' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            var bytes = Convert.FromBase64String(content);
            var assemblyId = await registrationService.UpsertAssemblyAsync(name, bytes, solutionName, ct);

            return new PluginsRegisterResponse { Id = assemblyId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers or updates a plugin package from base64-encoded .nupkg content.
    /// </summary>
    [JsonRpcMethod("plugins/registerPackage")]
    public async Task<PluginsRegisterResponse> PluginsRegisterPackageAsync(
        string name,
        string content,
        string? solutionName = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(content))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'content' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            var bytes = Convert.FromBase64String(content);
            var packageId = await registrationService.UpsertPackageAsync(name, bytes, solutionName, ct);

            return new PluginsRegisterResponse { Id = packageId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers or updates a plugin step.
    /// </summary>
    [JsonRpcMethod("plugins/registerStep")]
    public async Task<PluginsRegisterResponse> PluginsRegisterStepAsync(
        string eventHandlerId,
        string message,
        string entity,
        string stage,
        string mode = "Synchronous",
        string eventHandlerType = "pluginType",
        int executionOrder = 1,
        string? filteringAttributes = null,
        string? name = null,
        string? description = null,
        string? unsecureConfiguration = null,
        string? secureConfiguration = null,
        string? deployment = null,
        string? runAsUser = null,
        bool? canBeBypassed = null,
        bool? canUseReadOnlyConnection = null,
        string? invocationSource = null,
        bool asyncAutoDelete = false,
        string? secondaryEntity = null,
        string? solutionName = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(eventHandlerId) || !Guid.TryParse(eventHandlerId, out var typeId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'eventHandlerId' parameter must be a valid GUID");
        if (string.IsNullOrWhiteSpace(message))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'message' parameter is required");
        if (string.IsNullOrWhiteSpace(entity))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entity' parameter is required");
        if (string.IsNullOrWhiteSpace(stage))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'stage' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            var messageId = await registrationService.GetSdkMessageIdAsync(message, ct)
                ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"SDK message '{message}' not found");

            var filterId = await registrationService.GetSdkMessageFilterIdAsync(messageId, entity, secondaryEntity, ct);

            var stepConfig = new PluginStepConfig
            {
                Name = name ?? $"{message} of {entity}",
                Message = message,
                Entity = entity,
                Stage = stage,
                Mode = mode,
                ExecutionOrder = executionOrder,
                FilteringAttributes = filteringAttributes,
                Description = description,
                UnsecureConfiguration = unsecureConfiguration,
                SecureConfiguration = secureConfiguration,
                Deployment = deployment,
                RunAsUser = runAsUser,
                CanBeBypassed = canBeBypassed,
                CanUseReadOnlyConnection = canUseReadOnlyConnection,
                InvocationSource = invocationSource,
                AsyncAutoDelete = asyncAutoDelete,
                SecondaryEntity = secondaryEntity
            };

            var stepId = await registrationService.UpsertStepAsync(typeId, eventHandlerType, stepConfig, messageId, filterId, solutionName, ct);

            return new PluginsRegisterResponse { Id = stepId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers or updates a step image.
    /// </summary>
    [JsonRpcMethod("plugins/registerImage")]
    public async Task<PluginsRegisterResponse> PluginsRegisterImageAsync(
        string stepId,
        string name,
        string imageType,
        string? attributes = null,
        string? entityAlias = null,
        string? messageName = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepId) || !Guid.TryParse(stepId, out var parsedStepId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'stepId' parameter must be a valid GUID");
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(imageType))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'imageType' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            // Resolve message name from step if not provided
            string resolvedMessageName = messageName ?? "Create";
            if (string.IsNullOrWhiteSpace(messageName))
            {
                var step = await registrationService.GetStepByNameOrIdAsync(stepId, ct);
                if (step != null)
                    resolvedMessageName = step.Message;
            }

            var imageConfig = new PluginImageConfig
            {
                Name = name,
                ImageType = imageType,
                Attributes = attributes,
                EntityAlias = entityAlias
            };

            var imageId = await registrationService.UpsertImageAsync(parsedStepId, imageConfig, resolvedMessageName, ct);

            return new PluginsRegisterResponse { Id = imageId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates a plugin processing step.
    /// </summary>
    [JsonRpcMethod("plugins/updateStep")]
    public async Task<PluginsUpdateResponse> PluginsUpdateStepAsync(
        string id,
        string? mode = null,
        string? stage = null,
        int? rank = null,
        string? filteringAttributes = null,
        string? description = null,
        bool? canBeBypassed = null,
        bool? canUseReadOnlyConnection = null,
        string? invocationSource = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var stepId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            var request = new StepUpdateRequest(
                Mode: mode,
                Stage: stage,
                Rank: rank,
                FilteringAttributes: filteringAttributes,
                Description: description,
                CanBeBypassed: canBeBypassed,
                CanUseReadOnlyConnection: canUseReadOnlyConnection,
                InvocationSource: invocationSource
            );

            await registrationService.UpdateStepAsync(stepId, request, ct);

            return new PluginsUpdateResponse { Id = id };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates a step image.
    /// </summary>
    [JsonRpcMethod("plugins/updateImage")]
    public async Task<PluginsUpdateResponse> PluginsUpdateImageAsync(
        string id,
        string? imageAttributes = null,
        string? name = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var imageId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            var request = new ImageUpdateRequest(
                Attributes: imageAttributes,
                Name: name
            );

            await registrationService.UpdateImageAsync(imageId, request, ct);

            return new PluginsUpdateResponse { Id = id };
        }, cancellationToken);
    }

    /// <summary>
    /// Unregisters a plugin entity (assembly, package, type, step, or image).
    /// </summary>
    [JsonRpcMethod("plugins/unregister")]
    public async Task<PluginsUnregisterResponse> PluginsUnregisterAsync(
        string type,
        string id,
        bool force = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var entityId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            UnregisterResult result = type.ToLowerInvariant() switch
            {
                "assembly" => await registrationService.UnregisterAssemblyAsync(entityId, force, ct),
                "package" => await registrationService.UnregisterPackageAsync(entityId, force, ct),
                "type" => await registrationService.UnregisterPluginTypeAsync(entityId, force, ct),
                "step" => await registrationService.UnregisterStepAsync(entityId, force, ct),
                "image" => await registrationService.UnregisterImageAsync(entityId, ct),
                _ => throw new RpcException(ErrorCodes.Validation.InvalidArguments,
                    $"Unknown type '{type}'. Valid values: assembly, package, type, step, image")
            };

            return new PluginsUnregisterResponse
            {
                EntityName = result.EntityName,
                EntityType = result.EntityType,
                PackagesDeleted = result.PackagesDeleted,
                AssembliesDeleted = result.AssembliesDeleted,
                TypesDeleted = result.TypesDeleted,
                StepsDeleted = result.StepsDeleted,
                ImagesDeleted = result.ImagesDeleted,
                TotalDeleted = result.TotalDeleted
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Downloads the binary content of a plugin assembly or package as base64.
    /// </summary>
    [JsonRpcMethod("plugins/downloadBinary")]
    public async Task<PluginsDownloadResponse> PluginsDownloadBinaryAsync(
        string type,
        string id,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var entityId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var registrationService = sp.GetRequiredService<IPluginRegistrationService>();

            (byte[] bytes, string fileName) = type.ToLowerInvariant() switch
            {
                "assembly" => await registrationService.DownloadAssemblyAsync(entityId, ct),
                "package" => await registrationService.DownloadPackageAsync(entityId, ct),
                _ => throw new RpcException(ErrorCodes.Validation.InvalidArguments,
                    $"Unknown type '{type}'. Valid values: assembly, package")
            };

            return new PluginsDownloadResponse
            {
                Content = Convert.ToBase64String(bytes),
                FileName = fileName
            };
        }, cancellationToken);
    }

    private static PluginAssemblyDetailDto MapAssemblyInfoToDetail(PluginAssemblyInfoModel asm) =>
        new()
        {
            Id = asm.Id.ToString(),
            Name = asm.Name,
            Version = asm.Version,
            PublicKeyToken = asm.PublicKeyToken,
            IsolationMode = asm.IsolationMode,
            SourceType = asm.SourceType,
            IsManaged = asm.IsManaged,
            PackageId = asm.PackageId?.ToString(),
            CreatedOn = asm.CreatedOn?.ToString("O"),
            ModifiedOn = asm.ModifiedOn?.ToString("O")
        };

    private static PluginPackageDetailDto MapPackageInfoToDetail(PluginPackageInfoModel pkg) =>
        new()
        {
            Id = pkg.Id.ToString(),
            Name = pkg.Name,
            UniqueName = pkg.UniqueName,
            Version = pkg.Version,
            IsManaged = pkg.IsManaged,
            CreatedOn = pkg.CreatedOn?.ToString("O"),
            ModifiedOn = pkg.ModifiedOn?.ToString("O")
        };

    private static PluginTypeDetailDto MapPluginTypeInfoToDetail(PluginTypeInfoModel t) =>
        new()
        {
            Id = t.Id.ToString(),
            TypeName = t.TypeName,
            FriendlyName = t.FriendlyName,
            AssemblyId = t.AssemblyId?.ToString(),
            AssemblyName = t.AssemblyName,
            IsManaged = t.IsManaged,
            CreatedOn = t.CreatedOn?.ToString("O"),
            ModifiedOn = t.ModifiedOn?.ToString("O")
        };

    private static PluginStepDetailDto MapStepInfoToDetail(PluginStepInfoModel step) =>
        new()
        {
            Id = step.Id.ToString(),
            Name = step.Name,
            Message = step.Message,
            PrimaryEntity = step.PrimaryEntity,
            SecondaryEntity = step.SecondaryEntity,
            Stage = step.Stage,
            Mode = step.Mode,
            ExecutionOrder = step.ExecutionOrder,
            FilteringAttributes = step.FilteringAttributes,
            Configuration = step.Configuration,
            IsEnabled = step.IsEnabled,
            Description = step.Description,
            Deployment = step.Deployment,
            ImpersonatingUserId = step.ImpersonatingUserId?.ToString(),
            ImpersonatingUserName = step.ImpersonatingUserName,
            AsyncAutoDelete = step.AsyncAutoDelete,
            PluginTypeId = step.PluginTypeId?.ToString(),
            PluginTypeName = step.PluginTypeName,
            IsManaged = step.IsManaged,
            IsCustomizable = step.IsCustomizable,
            CreatedOn = step.CreatedOn?.ToString("O"),
            ModifiedOn = step.ModifiedOn?.ToString("O")
        };

    private static PluginImageDetailDto MapImageInfoToDetail(PluginImageInfoModel img) =>
        new()
        {
            Id = img.Id.ToString(),
            Name = img.Name,
            EntityAlias = img.EntityAlias,
            ImageType = img.ImageType,
            Attributes = img.Attributes,
            MessagePropertyName = img.MessagePropertyName,
            StepId = img.StepId?.ToString(),
            StepName = img.StepName,
            IsManaged = img.IsManaged,
            IsCustomizable = img.IsCustomizable,
            CreatedOn = img.CreatedOn?.ToString("O"),
            ModifiedOn = img.ModifiedOn?.ToString("O")
        };

    #endregion
}

// ── Plugin Registration Mutation DTOs ────────────────────────────────────────

/// <summary>
/// Response for plugins/get — returns detailed entity info.
/// </summary>
public class PluginsGetResponse
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("assembly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginAssemblyDetailDto? Assembly { get; set; }

    [JsonPropertyName("package")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginPackageDetailDto? Package { get; set; }

    [JsonPropertyName("pluginType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginTypeDetailDto? PluginType { get; set; }

    [JsonPropertyName("step")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginStepDetailDto? Step { get; set; }

    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PluginImageDetailDto? Image { get; set; }
}

public class PluginAssemblyDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("publicKeyToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKeyToken { get; set; }

    [JsonPropertyName("isolationMode")]
    public int IsolationMode { get; set; }

    [JsonPropertyName("sourceType")]
    public int SourceType { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("packageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PackageId { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class PluginPackageDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class PluginTypeDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("friendlyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FriendlyName { get; set; }

    [JsonPropertyName("assemblyId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyId { get; set; }

    [JsonPropertyName("assemblyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AssemblyName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class PluginStepDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("primaryEntity")]
    public string PrimaryEntity { get; set; } = "";

    [JsonPropertyName("secondaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecondaryEntity { get; set; }

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("executionOrder")]
    public int ExecutionOrder { get; set; }

    [JsonPropertyName("filteringAttributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilteringAttributes { get; set; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("deployment")]
    public string Deployment { get; set; } = "ServerOnly";

    [JsonPropertyName("impersonatingUserId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImpersonatingUserId { get; set; }

    [JsonPropertyName("impersonatingUserName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImpersonatingUserName { get; set; }

    [JsonPropertyName("asyncAutoDelete")]
    public bool AsyncAutoDelete { get; set; }

    [JsonPropertyName("pluginTypeId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginTypeId { get; set; }

    [JsonPropertyName("pluginTypeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginTypeName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("isCustomizable")]
    public bool IsCustomizable { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class PluginImageDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("entityAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityAlias { get; set; }

    [JsonPropertyName("imageType")]
    public string ImageType { get; set; } = "";

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attributes { get; set; }

    [JsonPropertyName("messagePropertyName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessagePropertyName { get; set; }

    [JsonPropertyName("stepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepId { get; set; }

    [JsonPropertyName("stepName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StepName { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("isCustomizable")]
    public bool IsCustomizable { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

/// <summary>
/// Response for plugins/messages.
/// </summary>
public class PluginsMessagesResponse
{
    [JsonPropertyName("messages")]
    public List<string> Messages { get; set; } = [];
}

/// <summary>
/// Response for plugins/entityAttributes.
/// </summary>
public class PluginsEntityAttributesResponse
{
    [JsonPropertyName("attributes")]
    public List<AttributeInfoDto> Attributes { get; set; } = [];
}

/// <summary>
/// Attribute metadata for an entity field.
/// </summary>
public class AttributeInfoDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("attributeType")]
    public string AttributeType { get; set; } = "";
}

/// <summary>
/// Response for plugins/toggleStep.
/// </summary>
public class PluginsToggleStepResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }
}

/// <summary>
/// Response for plugins/register* endpoints.
/// </summary>
public class PluginsRegisterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

/// <summary>
/// Response for plugins/update* endpoints.
/// </summary>
public class PluginsUpdateResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

/// <summary>
/// Response for plugins/unregister.
/// </summary>
public class PluginsUnregisterResponse
{
    [JsonPropertyName("entityName")]
    public string EntityName { get; set; } = "";

    [JsonPropertyName("entityType")]
    public string EntityType { get; set; } = "";

    [JsonPropertyName("packagesDeleted")]
    public int PackagesDeleted { get; set; }

    [JsonPropertyName("assembliesDeleted")]
    public int AssembliesDeleted { get; set; }

    [JsonPropertyName("typesDeleted")]
    public int TypesDeleted { get; set; }

    [JsonPropertyName("stepsDeleted")]
    public int StepsDeleted { get; set; }

    [JsonPropertyName("imagesDeleted")]
    public int ImagesDeleted { get; set; }

    [JsonPropertyName("totalDeleted")]
    public int TotalDeleted { get; set; }
}

/// <summary>
/// Response for plugins/downloadBinary.
/// </summary>
public class PluginsDownloadResponse
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = "";
}

// ── Service Endpoints DTOs ──────────────────────────────────────────────────
