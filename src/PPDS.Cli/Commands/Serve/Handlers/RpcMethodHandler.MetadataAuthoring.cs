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
    #region Metadata Authoring Methods

    /// <summary>
    /// Creates a new Dataverse table (entity) in the specified solution.
    /// </summary>
    [JsonRpcMethod("metadata/createTable")]
    public async Task<MetadataAuthoringResponse> MetadataCreateTableAsync(
        string solutionUniqueName,
        string schemaName,
        string displayName,
        string pluralDisplayName,
        string description,
        string ownershipType,
        bool dryRun = false,
        string? primaryAttributeSchemaName = null,
        string? primaryAttributeDisplayName = null,
        int? primaryAttributeMaxLength = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'displayName' parameter is required");
        if (string.IsNullOrWhiteSpace(pluralDisplayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'pluralDisplayName' parameter is required");
        if (string.IsNullOrWhiteSpace(ownershipType))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'ownershipType' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                var result = await service.CreateTableAsync(new Authoring.CreateTableRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    SchemaName = schemaName,
                    DisplayName = displayName,
                    PluralDisplayName = pluralDisplayName,
                    Description = description,
                    OwnershipType = ownershipType,
                    PrimaryAttributeSchemaName = primaryAttributeSchemaName,
                    PrimaryAttributeDisplayName = primaryAttributeDisplayName,
                    PrimaryAttributeMaxLength = primaryAttributeMaxLength,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = result.LogicalName,
                    MetadataId = result.MetadataId,
                    WasDryRun = result.WasDryRun,
                    ValidationMessages = result.ValidationMessages.Select(MapValidationMessage).ToList(),
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Updates an existing Dataverse table (entity).
    /// </summary>
    [JsonRpcMethod("metadata/updateTable")]
    public async Task<MetadataAuthoringResponse> MetadataUpdateTableAsync(
        string solutionUniqueName,
        string entityLogicalName,
        string? displayName = null,
        string? pluralDisplayName = null,
        string? description = null,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.UpdateTableAsync(new Authoring.UpdateTableRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    DisplayName = displayName,
                    PluralDisplayName = pluralDisplayName,
                    Description = description,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = entityLogicalName,
                    WasDryRun = dryRun,
                    RequiresPublish = !dryRun,
                    PublishHint = dryRun ? null : $"ppds metadata publish {entityLogicalName}",
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a Dataverse table (entity).
    /// </summary>
    [JsonRpcMethod("metadata/deleteTable")]
    public async Task<MetadataDeleteResponse> MetadataDeleteTableAsync(
        string solutionUniqueName,
        string entityLogicalName,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.DeleteTableAsync(new Authoring.DeleteTableRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataDeleteResponse { Success = true };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return new MetadataDeleteResponse
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorCode = ex.ErrorCode,
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a new column (attribute) on a Dataverse table.
    /// </summary>
    [JsonRpcMethod("metadata/createColumn")]
    public async Task<MetadataAuthoringResponse> MetadataCreateColumnAsync(
        string solutionUniqueName,
        string entityLogicalName,
        string schemaName,
        string displayName,
        string description,
        string columnType,
        bool dryRun = false,
        string? requiredLevel = null,
        int? maxLength = null,
        double? minValue = null,
        double? maxValue = null,
        int? precision = null,
        string? format = null,
        string? dateTimeBehavior = null,
        string? optionSetName = null,
        int? defaultValue = null,
        string? trueLabel = null,
        string? falseLabel = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'displayName' parameter is required");
        if (string.IsNullOrWhiteSpace(columnType))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'columnType' parameter is required");

        if (!Enum.TryParse<Authoring.SchemaColumnType>(columnType, ignoreCase: true, out var parsedColumnType))
            throw new RpcException(ErrorCodes.Validation.InvalidValue, $"Invalid column type '{columnType}'");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                var result = await service.CreateColumnAsync(new Authoring.CreateColumnRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    SchemaName = schemaName,
                    DisplayName = displayName,
                    Description = description,
                    ColumnType = parsedColumnType,
                    RequiredLevel = requiredLevel,
                    MaxLength = maxLength,
                    MinValue = minValue,
                    MaxValue = maxValue,
                    Precision = precision,
                    Format = format,
                    DateTimeBehavior = dateTimeBehavior,
                    OptionSetName = optionSetName,
                    DefaultValue = defaultValue,
                    TrueLabel = trueLabel,
                    FalseLabel = falseLabel,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = result.LogicalName,
                    MetadataId = result.MetadataId,
                    WasDryRun = result.WasDryRun,
                    ValidationMessages = result.ValidationMessages.Select(MapValidationMessage).ToList(),
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Updates an existing column (attribute) on a Dataverse table.
    /// </summary>
    [JsonRpcMethod("metadata/updateColumn")]
    public async Task<MetadataAuthoringResponse> MetadataUpdateColumnAsync(
        string solutionUniqueName,
        string entityLogicalName,
        string columnLogicalName,
        string? displayName = null,
        string? description = null,
        string? requiredLevel = null,
        int? maxLength = null,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(columnLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'columnLogicalName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.UpdateColumnAsync(new Authoring.UpdateColumnRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    ColumnLogicalName = columnLogicalName,
                    DisplayName = displayName,
                    Description = description,
                    RequiredLevel = requiredLevel,
                    MaxLength = maxLength,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = columnLogicalName,
                    WasDryRun = dryRun,
                    RequiresPublish = !dryRun,
                    PublishHint = dryRun ? null : $"ppds metadata publish {entityLogicalName}",
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a column (attribute) from a Dataverse table.
    /// </summary>
    [JsonRpcMethod("metadata/deleteColumn")]
    public async Task<MetadataDeleteResponse> MetadataDeleteColumnAsync(
        string solutionUniqueName,
        string entityLogicalName,
        string columnLogicalName,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(columnLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'columnLogicalName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.DeleteColumnAsync(new Authoring.DeleteColumnRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    ColumnLogicalName = columnLogicalName,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataDeleteResponse { Success = true };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return new MetadataDeleteResponse
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorCode = ex.ErrorCode,
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a one-to-many (1:N) relationship.
    /// </summary>
    [JsonRpcMethod("metadata/createOneToMany")]
    public async Task<MetadataAuthoringResponse> MetadataCreateOneToManyAsync(
        string solutionUniqueName,
        string referencedEntity,
        string referencingEntity,
        string schemaName,
        string lookupSchemaName,
        string lookupDisplayName,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(referencedEntity))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'referencedEntity' parameter is required");
        if (string.IsNullOrWhiteSpace(referencingEntity))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'referencingEntity' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");
        if (string.IsNullOrWhiteSpace(lookupSchemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'lookupSchemaName' parameter is required");
        if (string.IsNullOrWhiteSpace(lookupDisplayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'lookupDisplayName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                var result = await service.CreateOneToManyAsync(new Authoring.CreateOneToManyRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    ReferencedEntity = referencedEntity,
                    ReferencingEntity = referencingEntity,
                    SchemaName = schemaName,
                    LookupSchemaName = lookupSchemaName,
                    LookupDisplayName = lookupDisplayName,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = result.SchemaName,
                    MetadataId = result.MetadataId,
                    WasDryRun = result.WasDryRun,
                    ValidationMessages = result.ValidationMessages.Select(MapValidationMessage).ToList(),
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a many-to-many (N:N) relationship.
    /// </summary>
    [JsonRpcMethod("metadata/createManyToMany")]
    public async Task<MetadataAuthoringResponse> MetadataCreateManyToManyAsync(
        string solutionUniqueName,
        string entity1LogicalName,
        string entity2LogicalName,
        string schemaName,
        bool dryRun = false,
        string? intersectEntitySchemaName = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entity1LogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entity1LogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(entity2LogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entity2LogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                var result = await service.CreateManyToManyAsync(new Authoring.CreateManyToManyRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    Entity1LogicalName = entity1LogicalName,
                    Entity2LogicalName = entity2LogicalName,
                    SchemaName = schemaName,
                    IntersectEntitySchemaName = intersectEntitySchemaName,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = result.SchemaName,
                    MetadataId = result.MetadataId,
                    WasDryRun = result.WasDryRun,
                    ValidationMessages = result.ValidationMessages.Select(MapValidationMessage).ToList(),
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a relationship.
    /// </summary>
    [JsonRpcMethod("metadata/deleteRelationship")]
    public async Task<MetadataDeleteResponse> MetadataDeleteRelationshipAsync(
        string solutionUniqueName,
        string schemaName,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.DeleteRelationshipAsync(new Authoring.DeleteRelationshipRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    SchemaName = schemaName,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataDeleteResponse { Success = true };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return new MetadataDeleteResponse
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorCode = ex.ErrorCode,
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a new global choice (option set).
    /// </summary>
    [JsonRpcMethod("metadata/createGlobalChoice")]
    public async Task<MetadataAuthoringResponse> MetadataCreateGlobalChoiceAsync(
        string solutionUniqueName,
        string schemaName,
        string displayName,
        string description,
        bool dryRun = false,
        bool isMultiSelect = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'displayName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                var result = await service.CreateGlobalChoiceAsync(new Authoring.CreateGlobalChoiceRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    SchemaName = schemaName,
                    DisplayName = displayName,
                    Description = description,
                    IsMultiSelect = isMultiSelect,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = result.Name,
                    MetadataId = result.MetadataId,
                    WasDryRun = result.WasDryRun,
                    ValidationMessages = result.ValidationMessages.Select(MapValidationMessage).ToList(),
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a global choice (option set).
    /// </summary>
    [JsonRpcMethod("metadata/deleteGlobalChoice")]
    public async Task<MetadataDeleteResponse> MetadataDeleteGlobalChoiceAsync(
        string solutionUniqueName,
        string name,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.DeleteGlobalChoiceAsync(new Authoring.DeleteGlobalChoiceRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    Name = name,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataDeleteResponse { Success = true };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return new MetadataDeleteResponse
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorCode = ex.ErrorCode,
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates an alternate key on a Dataverse table.
    /// </summary>
    [JsonRpcMethod("metadata/createKey")]
    public async Task<MetadataAuthoringResponse> MetadataCreateKeyAsync(
        string solutionUniqueName,
        string entityLogicalName,
        string schemaName,
        string displayName,
        string[] keyAttributes,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(schemaName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'schemaName' parameter is required");
        if (string.IsNullOrWhiteSpace(displayName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'displayName' parameter is required");
        if (keyAttributes == null || keyAttributes.Length == 0)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'keyAttributes' parameter is required and must not be empty");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                var result = await service.CreateKeyAsync(new Authoring.CreateKeyRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    SchemaName = schemaName,
                    DisplayName = displayName,
                    KeyAttributes = keyAttributes,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataAuthoringResponse
                {
                    Success = true,
                    LogicalName = result.SchemaName,
                    MetadataId = result.MetadataId,
                    WasDryRun = result.WasDryRun,
                    ValidationMessages = result.ValidationMessages.Select(MapValidationMessage).ToList(),
                };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return MapValidationExceptionToResponse(ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes an alternate key from a Dataverse table.
    /// </summary>
    [JsonRpcMethod("metadata/deleteKey")]
    public async Task<MetadataDeleteResponse> MetadataDeleteKeyAsync(
        string solutionUniqueName,
        string entityLogicalName,
        string keyLogicalName,
        bool dryRun = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionUniqueName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'solutionUniqueName' parameter is required");
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");
        if (string.IsNullOrWhiteSpace(keyLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'keyLogicalName' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IMetadataAuthoringService>();
            try
            {
                await service.DeleteKeyAsync(new Authoring.DeleteKeyRequest
                {
                    SolutionUniqueName = solutionUniqueName,
                    EntityLogicalName = entityLogicalName,
                    KeyLogicalName = keyLogicalName,
                    DryRun = dryRun,
                }, ct: ct).ConfigureAwait(false);

                return new MetadataDeleteResponse { Success = true };
            }
            catch (Authoring.MetadataValidationException ex)
            {
                return new MetadataDeleteResponse
                {
                    Success = false,
                    Error = ex.Message,
                    ErrorCode = ex.ErrorCode,
                };
            }
        }, cancellationToken);
    }

    private static MetadataAuthoringResponse MapValidationExceptionToResponse(Authoring.MetadataValidationException ex)
    {
        return new MetadataAuthoringResponse
        {
            Success = false,
            Error = ex.Message,
            ErrorCode = ex.ErrorCode,
            ValidationMessages = ex.ValidationMessages.Select(MapValidationMessage).ToList(),
        };
    }

    private static ValidationMessageDto MapValidationMessage(Authoring.ValidationMessage vm) =>
        new() { Field = vm.Field, Rule = vm.Rule, Message = vm.Message };

    #endregion
}

#region Metadata Authoring DTOs

/// <summary>
/// Response for metadata authoring create/update operations.
/// </summary>
public class MetadataAuthoringResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("logicalName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LogicalName { get; set; }

    [JsonPropertyName("metadataId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? MetadataId { get; set; }

    [JsonPropertyName("wasDryRun")]
    public bool WasDryRun { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("validationMessages")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ValidationMessageDto>? ValidationMessages { get; set; }

    // Issue #1009: signal that the change needs to be published before consumers see it.
    [JsonPropertyName("requiresPublish")]
    public bool RequiresPublish { get; set; }

    [JsonPropertyName("publishHint")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublishHint { get; set; }
}

/// <summary>
/// Response for metadata authoring delete operations.
/// </summary>
public class MetadataDeleteResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("dependencies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DependencyDto>? Dependencies { get; set; }

    [JsonPropertyName("dependencyCount")]
    public int DependencyCount { get; set; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; set; }

    [JsonPropertyName("errorCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; set; }
}

/// <summary>
/// Validation message from a metadata authoring operation.
/// </summary>
public class ValidationMessageDto
{
    [JsonPropertyName("field")]
    public string Field { get; set; } = "";

    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}

/// <summary>
/// Dependency information from a metadata delete dry-run.
/// </summary>
public class DependencyDto
{
    [JsonPropertyName("dependentComponentType")]
    public string DependentComponentType { get; set; } = "";

    [JsonPropertyName("dependentComponentName")]
    public string DependentComponentName { get; set; } = "";

    [JsonPropertyName("dependentComponentSchemaName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DependentComponentSchemaName { get; set; }
}

#endregion
