using System.Collections.Concurrent;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Plugins.Registration;

/// <summary>
/// Service for managing plugin registrations in Dataverse.
/// </summary>
/// <remarks>
/// This service uses connection pooling to enable parallel Dataverse operations.
/// Each method acquires its own client from the pool, enabling DOP-based parallelism.
/// See ADR-0002 and ADR-0005 for pool architecture details.
/// </remarks>
public sealed class PluginRegistrationService : IPluginRegistrationService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<PluginRegistrationService> _logger;

    // Cache for entity type codes (ETCs) - some like pluginpackage vary by environment
    private readonly ConcurrentDictionary<string, int> _entityTypeCodeCache = new();

    #region Dataverse Constants

    // Solution component type codes (from Microsoft.Crm.Sdk.Messages.ComponentType)
    private const int ComponentTypePluginAssembly = 91;
    private const int ComponentTypeSdkMessageProcessingStep = 92;

    // Well-known component type codes that are consistent across all environments
    private static readonly Dictionary<string, int> WellKnownComponentTypes = new()
    {
        [PluginAssembly.EntityLogicalName] = ComponentTypePluginAssembly,
        [SdkMessageProcessingStep.EntityLogicalName] = ComponentTypeSdkMessageProcessingStep
    };

    // Pipeline stage values (from SDK Message Processing Step entity)
    private const int StagePreValidation = 10;
    private const int StagePreOperation = 20;
    private const int StageMainOperation = 30;
    private const int StagePostOperation = 40;

    /// <summary>
    /// Default image property names per SDK message.
    /// This is static knowledge matching Plugin Registration Tool behavior.
    /// Source: extension/src/features/pluginRegistration/domain/services/MessageMetadataService.ts
    /// </summary>
    private static readonly Dictionary<string, string> DefaultImagePropertyNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Create"] = "id",
            ["CreateMultiple"] = "Ids",
            ["Update"] = "Target",
            ["UpdateMultiple"] = "Targets",
            ["Delete"] = "Target",
            ["Assign"] = "Target",
            ["SetState"] = "EntityMoniker",
            ["SetStateDynamicEntity"] = "EntityMoniker",
            ["Route"] = "Target",
            ["Send"] = "EmailId",
            ["DeliverIncoming"] = "EmailId",
            ["DeliverPromote"] = "EmailId",
            ["ExecuteWorkflow"] = "Target",
            ["Merge"] = "Target"
        };

    #endregion

    /// <summary>
    /// Gets the default message property name for an SDK message.
    /// </summary>
    /// <param name="messageName">The SDK message name (e.g., "Create", "Update", "SetState").</param>
    /// <returns>The message property name, or null if message doesn't support images.</returns>
    public static string? GetDefaultImagePropertyName(string messageName)
        => DefaultImagePropertyNames.GetValueOrDefault(messageName);

    /// <summary>
    /// Creates a new instance of the plugin registration service.
    /// </summary>
    /// <param name="pool">The Dataverse connection pool for acquiring clients.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PluginRegistrationService(IDataverseConnectionPool pool, ILogger<PluginRegistrationService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    #region Query Operations

    /// <summary>
    /// Lists all plugin assemblies in the environment.
    /// </summary>
    /// <param name="assemblyNameFilter">Optional filter by assembly name.</param>
    /// <param name="options">Filtering options (hidden steps, Microsoft assemblies).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginAssemblyInfo>> ListAssembliesAsync(
        string? assemblyNameFilter = null,
        PluginListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PluginListOptions();

        var query = new QueryExpression(PluginAssembly.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginAssembly.Fields.Name,
                PluginAssembly.Fields.Version,
                PluginAssembly.Fields.PublicKeyToken,
                PluginAssembly.Fields.Culture,
                PluginAssembly.Fields.IsolationMode,
                PluginAssembly.Fields.SourceType,
                PluginAssembly.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    // Exclude system assemblies
                    new ConditionExpression(PluginAssembly.Fields.IsHidden, ConditionOperator.Equal, false)
                }
            },
            Orders = { new OrderExpression(PluginAssembly.Fields.Name, OrderType.Ascending) }
        };

        if (!string.IsNullOrEmpty(assemblyNameFilter))
        {
            query.Criteria.AddCondition(PluginAssembly.Fields.Name, ConditionOperator.Equal, assemblyNameFilter);
        }

        // Filter out Microsoft.* assemblies by default (except Microsoft.Crm.ServiceBus)
        if (!options.IncludeMicrosoft)
        {
            var microsoftFilter = new FilterExpression(LogicalOperator.Or);
            microsoftFilter.AddCondition(PluginAssembly.Fields.Name, ConditionOperator.NotLike, "Microsoft%");
            microsoftFilter.AddCondition(PluginAssembly.Fields.Name, ConditionOperator.Equal, "Microsoft.Crm.ServiceBus");
            query.Criteria.AddFilter(microsoftFilter);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);

        return results.Entities.Select(e => new PluginAssemblyInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(PluginAssembly.Fields.Name) ?? string.Empty,
            Version = e.GetAttributeValue<string>(PluginAssembly.Fields.Version),
            PublicKeyToken = e.GetAttributeValue<string>(PluginAssembly.Fields.PublicKeyToken),
            IsolationMode = e.GetAttributeValue<OptionSetValue>(PluginAssembly.Fields.IsolationMode)?.Value ?? (int)pluginassembly_isolationmode.Sandbox,
            IsManaged = e.GetAttributeValue<bool?>(PluginAssembly.Fields.IsManaged) ?? false
        }).ToList();
    }

    /// <summary>
    /// Lists all plugin packages in the environment.
    /// </summary>
    /// <param name="packageNameFilter">Optional filter by package name or unique name.</param>
    /// <param name="options">Filtering options (hidden steps, Microsoft assemblies).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginPackageInfo>> ListPackagesAsync(
        string? packageNameFilter = null,
        PluginListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PluginListOptions();

        var query = new QueryExpression(PluginPackage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginPackage.Fields.Name,
                PluginPackage.Fields.UniqueName,
                PluginPackage.Fields.Version,
                PluginPackage.Fields.IsManaged),
            Orders = { new OrderExpression(PluginPackage.Fields.Name, OrderType.Ascending) }
        };

        if (!string.IsNullOrEmpty(packageNameFilter))
        {
            // Filter by name or uniquename
            query.Criteria = new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression(PluginPackage.Fields.Name, ConditionOperator.Equal, packageNameFilter),
                    new ConditionExpression(PluginPackage.Fields.UniqueName, ConditionOperator.Equal, packageNameFilter)
                }
            };
        }

        // Filter out Microsoft.* packages by default (except Microsoft.Crm.ServiceBus)
        if (!options.IncludeMicrosoft)
        {
            var microsoftFilter = new FilterExpression(LogicalOperator.Or);
            microsoftFilter.AddCondition(PluginPackage.Fields.Name, ConditionOperator.NotLike, "Microsoft%");
            microsoftFilter.AddCondition(PluginPackage.Fields.Name, ConditionOperator.Equal, "Microsoft.Crm.ServiceBus");
            query.Criteria.AddFilter(microsoftFilter);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);

        return results.Entities.Select(e => new PluginPackageInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(PluginPackage.Fields.Name) ?? string.Empty,
            UniqueName = e.GetAttributeValue<string>(PluginPackage.Fields.UniqueName),
            Version = e.GetAttributeValue<string>(PluginPackage.Fields.Version),
            IsManaged = e.GetAttributeValue<bool?>(PluginPackage.Fields.IsManaged) ?? false
        }).ToList();
    }

    /// <summary>
    /// Lists all assemblies contained in a plugin package.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginAssemblyInfo>> ListAssembliesForPackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(PluginAssembly.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginAssembly.Fields.Name,
                PluginAssembly.Fields.Version,
                PluginAssembly.Fields.PublicKeyToken,
                PluginAssembly.Fields.Culture,
                PluginAssembly.Fields.IsolationMode,
                PluginAssembly.Fields.SourceType,
                PluginAssembly.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginAssembly.Fields.PackageId, ConditionOperator.Equal, packageId)
                }
            },
            Orders = { new OrderExpression(PluginAssembly.Fields.Name, OrderType.Ascending) }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);

        return results.Entities.Select(e => new PluginAssemblyInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(PluginAssembly.Fields.Name) ?? string.Empty,
            Version = e.GetAttributeValue<string>(PluginAssembly.Fields.Version),
            PublicKeyToken = e.GetAttributeValue<string>(PluginAssembly.Fields.PublicKeyToken),
            IsolationMode = e.GetAttributeValue<OptionSetValue>(PluginAssembly.Fields.IsolationMode)?.Value ?? (int)pluginassembly_isolationmode.Sandbox,
            IsManaged = e.GetAttributeValue<bool?>(PluginAssembly.Fields.IsManaged) ?? false
        }).ToList();
    }

    /// <summary>
    /// Lists all plugin types for a package by querying through the package's assemblies.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginTypeInfo>> ListTypesForPackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        var assemblies = await ListAssembliesForPackageAsync(packageId, cancellationToken);

        if (assemblies.Count == 0)
            return [];

        // Fetch types for all assemblies in parallel
        var typeTasks = assemblies.Select(a => ListTypesForAssemblyAsync(a.Id, cancellationToken));
        var typesPerAssembly = await Task.WhenAll(typeTasks);

        return typesPerAssembly.SelectMany(t => t).ToList();
    }

    /// <summary>
    /// Lists all plugin types for an assembly.
    /// </summary>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginTypeInfo>> ListTypesForAssemblyAsync(
        Guid assemblyId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(PluginType.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginType.Fields.TypeName,
                PluginType.Fields.FriendlyName,
                PluginType.Fields.Name,
                PluginType.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginType.Fields.PluginAssemblyId, ConditionOperator.Equal, assemblyId)
                }
            },
            Orders = { new OrderExpression(PluginType.Fields.TypeName, OrderType.Ascending) }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);

        return results.Entities.Select(e => new PluginTypeInfo
        {
            Id = e.Id,
            TypeName = e.GetAttributeValue<string>(PluginType.Fields.TypeName) ?? string.Empty,
            FriendlyName = e.GetAttributeValue<string>(PluginType.Fields.FriendlyName),
            IsManaged = e.GetAttributeValue<bool?>(PluginType.Fields.IsManaged) ?? false
        }).ToList();
    }

    /// <summary>
    /// Lists all processing steps for a plugin type.
    /// </summary>
    /// <param name="pluginTypeId">The plugin type ID.</param>
    /// <param name="options">Filtering options (hidden steps, Microsoft assemblies).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginStepInfo>> ListStepsForTypeAsync(
        Guid pluginTypeId,
        PluginListOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PluginListOptions();

        var query = new QueryExpression(SdkMessageProcessingStep.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStep.Fields.Name,
                SdkMessageProcessingStep.Fields.Stage,
                SdkMessageProcessingStep.Fields.Mode,
                SdkMessageProcessingStep.Fields.Rank,
                SdkMessageProcessingStep.Fields.FilteringAttributes,
                SdkMessageProcessingStep.Fields.Configuration,
                SdkMessageProcessingStep.Fields.StateCode,
                SdkMessageProcessingStep.Fields.Description,
                SdkMessageProcessingStep.Fields.SupportedDeployment,
                SdkMessageProcessingStep.Fields.ImpersonatingUserId,
                SdkMessageProcessingStep.Fields.AsyncAutoDelete,
                SdkMessageProcessingStep.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStep.Fields.EventHandler, ConditionOperator.Equal, pluginTypeId)
                }
            },
            LinkEntities =
            {
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SdkMessage.EntityLogicalName, SdkMessageProcessingStep.Fields.SdkMessageId, SdkMessage.Fields.SdkMessageId, JoinOperator.Inner)
                {
                    Columns = new ColumnSet(SdkMessage.Fields.Name),
                    EntityAlias = "message"
                },
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SdkMessageFilter.EntityLogicalName, SdkMessageProcessingStep.Fields.SdkMessageFilterId, SdkMessageFilter.Fields.SdkMessageFilterId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(SdkMessageFilter.Fields.PrimaryObjectTypeCode, SdkMessageFilter.Fields.SecondaryObjectTypeCode),
                    EntityAlias = "filter"
                },
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SystemUser.EntityLogicalName, SdkMessageProcessingStep.Fields.ImpersonatingUserId, SystemUser.Fields.SystemUserId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(SystemUser.Fields.FullName, SystemUser.Fields.DomainName),
                    EntityAlias = "impersonatinguser"
                }
            },
            Orders = { new OrderExpression(SdkMessageProcessingStep.Fields.Name, OrderType.Ascending) }
        };

        // Exclude hidden steps by default
        if (!options.IncludeHidden)
        {
            query.Criteria.AddCondition(SdkMessageProcessingStep.Fields.IsHidden, ConditionOperator.Equal, false);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);

        return results.Entities.Select(e =>
        {
            var impersonatingUserRef = e.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId);
            var impersonatingUserName = e.GetAttributeValue<AliasedValue>($"impersonatinguser.{SystemUser.Fields.FullName}")?.Value?.ToString()
                ?? e.GetAttributeValue<AliasedValue>($"impersonatinguser.{SystemUser.Fields.DomainName}")?.Value?.ToString();

            return new PluginStepInfo
            {
                Id = e.Id,
                Name = e.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Name) ?? string.Empty,
                Message = e.GetAttributeValue<AliasedValue>($"message.{SdkMessage.Fields.Name}")?.Value?.ToString() ?? string.Empty,
                PrimaryEntity = e.GetAttributeValue<AliasedValue>($"filter.{SdkMessageFilter.Fields.PrimaryObjectTypeCode}")?.Value?.ToString() ?? "none",
                SecondaryEntity = e.GetAttributeValue<AliasedValue>($"filter.{SdkMessageFilter.Fields.SecondaryObjectTypeCode}")?.Value?.ToString(),
                Stage = MapStageFromValue(e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Stage)?.Value ?? StagePostOperation),
                Mode = MapModeFromValue(e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Mode)?.Value ?? 0),
                ExecutionOrder = e.GetAttributeValue<int>(SdkMessageProcessingStep.Fields.Rank),
                FilteringAttributes = e.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.FilteringAttributes),
                Configuration = e.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Configuration),
                IsEnabled = e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.StateCode)?.Value == (int)sdkmessageprocessingstep_statecode.Enabled,
                Description = e.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Description),
                Deployment = MapDeploymentFromValue(e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.SupportedDeployment)?.Value ?? 0),
                ImpersonatingUserId = impersonatingUserRef?.Id,
                ImpersonatingUserName = impersonatingUserName,
                AsyncAutoDelete = e.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.AsyncAutoDelete) ?? false,
                IsManaged = e.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.IsManaged) ?? false
            };
        }).ToList();
    }

    /// <summary>
    /// Lists all images for a processing step.
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<List<PluginImageInfo>> ListImagesForStepAsync(
        Guid stepId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(SdkMessageProcessingStepImage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStepImage.Fields.Name,
                SdkMessageProcessingStepImage.Fields.EntityAlias,
                SdkMessageProcessingStepImage.Fields.ImageType,
                SdkMessageProcessingStepImage.Fields.Attributes1,
                SdkMessageProcessingStepImage.Fields.IsManaged),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepId, ConditionOperator.Equal, stepId)
                }
            },
            Orders = { new OrderExpression(SdkMessageProcessingStepImage.Fields.Name, OrderType.Ascending) }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);

        return results.Entities.Select(e => new PluginImageInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Name) ?? string.Empty,
            EntityAlias = e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.EntityAlias),
            ImageType = MapImageTypeFromValue(e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStepImage.Fields.ImageType)?.Value ?? 0),
            Attributes = e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Attributes1),
            IsManaged = e.GetAttributeValue<bool?>(SdkMessageProcessingStepImage.Fields.IsManaged) ?? false
        }).ToList();
    }

    #endregion

    #region Lookup Operations

    /// <summary>
    /// Gets an assembly by name.
    /// </summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginAssemblyInfo?> GetAssemblyByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        // Use IncludeMicrosoft: true to search all assemblies including Microsoft.* when looking up by exact name
        var assemblies = await ListAssembliesAsync(name, new PluginListOptions(IncludeMicrosoft: true), cancellationToken);
        return assemblies.FirstOrDefault();
    }

    /// <summary>
    /// Gets a plugin package by name or unique name.
    /// </summary>
    /// <param name="name">The package name or unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginPackageInfo?> GetPackageByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        // Use IncludeMicrosoft: true to search all packages including Microsoft.* when looking up by exact name
        var packages = await ListPackagesAsync(name, new PluginListOptions(IncludeMicrosoft: true), cancellationToken);
        return packages.FirstOrDefault();
    }

    /// <summary>
    /// Gets an assembly by ID.
    /// </summary>
    /// <param name="id">The assembly ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginAssemblyInfo?> GetAssemblyByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(PluginAssembly.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginAssembly.Fields.Name,
                PluginAssembly.Fields.Version,
                PluginAssembly.Fields.PublicKeyToken,
                PluginAssembly.Fields.IsolationMode,
                PluginAssembly.Fields.SourceType,
                PluginAssembly.Fields.IsManaged,
                PluginAssembly.Fields.PackageId,
                PluginAssembly.Fields.CreatedOn,
                PluginAssembly.Fields.ModifiedOn),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginAssembly.Fields.PluginAssemblyId, ConditionOperator.Equal, id)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null) return null;

        return new PluginAssemblyInfo
        {
            Id = entity.Id,
            Name = entity.GetAttributeValue<string>(PluginAssembly.Fields.Name) ?? string.Empty,
            Version = entity.GetAttributeValue<string>(PluginAssembly.Fields.Version),
            PublicKeyToken = entity.GetAttributeValue<string>(PluginAssembly.Fields.PublicKeyToken),
            IsolationMode = entity.GetAttributeValue<OptionSetValue>(PluginAssembly.Fields.IsolationMode)?.Value ?? (int)pluginassembly_isolationmode.Sandbox,
            SourceType = entity.GetAttributeValue<OptionSetValue>(PluginAssembly.Fields.SourceType)?.Value ?? (int)pluginassembly_sourcetype.Database,
            IsManaged = entity.GetAttributeValue<bool?>(PluginAssembly.Fields.IsManaged) ?? false,
            PackageId = entity.GetAttributeValue<EntityReference>(PluginAssembly.Fields.PackageId)?.Id,
            CreatedOn = entity.GetAttributeValue<DateTime?>(PluginAssembly.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(PluginAssembly.Fields.ModifiedOn)
        };
    }

    /// <summary>
    /// Gets a plugin package by ID.
    /// </summary>
    /// <param name="id">The package ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginPackageInfo?> GetPackageByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(PluginPackage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginPackage.Fields.Name,
                PluginPackage.Fields.UniqueName,
                PluginPackage.Fields.Version,
                PluginPackage.Fields.IsManaged,
                PluginPackage.Fields.CreatedOn,
                PluginPackage.Fields.ModifiedOn),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginPackage.Fields.PluginPackageId, ConditionOperator.Equal, id)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null) return null;

        return new PluginPackageInfo
        {
            Id = entity.Id,
            Name = entity.GetAttributeValue<string>(PluginPackage.Fields.Name) ?? string.Empty,
            UniqueName = entity.GetAttributeValue<string>(PluginPackage.Fields.UniqueName),
            Version = entity.GetAttributeValue<string>(PluginPackage.Fields.Version),
            IsManaged = entity.GetAttributeValue<bool?>(PluginPackage.Fields.IsManaged) ?? false,
            CreatedOn = entity.GetAttributeValue<DateTime?>(PluginPackage.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(PluginPackage.Fields.ModifiedOn)
        };
    }

    /// <summary>
    /// Gets a plugin type by name or ID.
    /// </summary>
    /// <param name="nameOrId">The plugin type name or ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginTypeInfo?> GetPluginTypeByNameOrIdAsync(
        string nameOrId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(PluginType.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginType.Fields.TypeName,
                PluginType.Fields.FriendlyName,
                PluginType.Fields.Name,
                PluginType.Fields.PluginAssemblyId,
                PluginType.Fields.CreatedOn,
                PluginType.Fields.ModifiedOn),
            LinkEntities =
            {
                new LinkEntity(PluginType.EntityLogicalName, PluginAssembly.EntityLogicalName, PluginType.Fields.PluginAssemblyId, PluginAssembly.Fields.PluginAssemblyId, JoinOperator.Inner)
                {
                    Columns = new ColumnSet(PluginAssembly.Fields.Name),
                    EntityAlias = "assembly"
                }
            }
        };

        if (Guid.TryParse(nameOrId, out var id))
        {
            query.Criteria.AddCondition(PluginType.Fields.PluginTypeId, ConditionOperator.Equal, id);
        }
        else
        {
            query.Criteria.AddCondition(PluginType.Fields.TypeName, ConditionOperator.Equal, nameOrId);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null) return null;

        return new PluginTypeInfo
        {
            Id = entity.Id,
            TypeName = entity.GetAttributeValue<string>(PluginType.Fields.TypeName) ?? string.Empty,
            FriendlyName = entity.GetAttributeValue<string>(PluginType.Fields.FriendlyName),
            AssemblyId = entity.GetAttributeValue<EntityReference>(PluginType.Fields.PluginAssemblyId)?.Id,
            AssemblyName = entity.GetAttributeValue<AliasedValue>($"assembly.{PluginAssembly.Fields.Name}")?.Value?.ToString(),
            CreatedOn = entity.GetAttributeValue<DateTime?>(PluginType.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(PluginType.Fields.ModifiedOn)
        };
    }

    /// <summary>
    /// Gets a processing step by name or ID.
    /// </summary>
    /// <param name="nameOrId">The step name or ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginStepInfo?> GetStepByNameOrIdAsync(
        string nameOrId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(SdkMessageProcessingStep.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStep.Fields.Name,
                SdkMessageProcessingStep.Fields.Stage,
                SdkMessageProcessingStep.Fields.Mode,
                SdkMessageProcessingStep.Fields.Rank,
                SdkMessageProcessingStep.Fields.FilteringAttributes,
                SdkMessageProcessingStep.Fields.Configuration,
                SdkMessageProcessingStep.Fields.StateCode,
                SdkMessageProcessingStep.Fields.Description,
                SdkMessageProcessingStep.Fields.SupportedDeployment,
                SdkMessageProcessingStep.Fields.ImpersonatingUserId,
                SdkMessageProcessingStep.Fields.AsyncAutoDelete,
                SdkMessageProcessingStep.Fields.EventHandler,
                SdkMessageProcessingStep.Fields.IsManaged,
                SdkMessageProcessingStep.Fields.IsCustomizable,
                SdkMessageProcessingStep.Fields.CreatedOn,
                SdkMessageProcessingStep.Fields.ModifiedOn),
            LinkEntities =
            {
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SdkMessage.EntityLogicalName, SdkMessageProcessingStep.Fields.SdkMessageId, SdkMessage.Fields.SdkMessageId, JoinOperator.Inner)
                {
                    Columns = new ColumnSet(SdkMessage.Fields.Name),
                    EntityAlias = "message"
                },
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SdkMessageFilter.EntityLogicalName, SdkMessageProcessingStep.Fields.SdkMessageFilterId, SdkMessageFilter.Fields.SdkMessageFilterId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(SdkMessageFilter.Fields.PrimaryObjectTypeCode, SdkMessageFilter.Fields.SecondaryObjectTypeCode),
                    EntityAlias = "filter"
                },
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SystemUser.EntityLogicalName, SdkMessageProcessingStep.Fields.ImpersonatingUserId, SystemUser.Fields.SystemUserId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(SystemUser.Fields.FullName, SystemUser.Fields.DomainName),
                    EntityAlias = "impersonatinguser"
                },
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, PluginType.EntityLogicalName, SdkMessageProcessingStep.Fields.EventHandler, PluginType.Fields.PluginTypeId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(PluginType.Fields.TypeName),
                    EntityAlias = "plugintype"
                }
            }
        };

        if (Guid.TryParse(nameOrId, out var id))
        {
            query.Criteria.AddCondition(SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId, ConditionOperator.Equal, id);
        }
        else
        {
            query.Criteria.AddCondition(SdkMessageProcessingStep.Fields.Name, ConditionOperator.Equal, nameOrId);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null) return null;

        var impersonatingUserRef = entity.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId);
        var impersonatingUserName = entity.GetAttributeValue<AliasedValue>($"impersonatinguser.{SystemUser.Fields.FullName}")?.Value?.ToString()
            ?? entity.GetAttributeValue<AliasedValue>($"impersonatinguser.{SystemUser.Fields.DomainName}")?.Value?.ToString();

        return new PluginStepInfo
        {
            Id = entity.Id,
            Name = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Name) ?? string.Empty,
            Message = entity.GetAttributeValue<AliasedValue>($"message.{SdkMessage.Fields.Name}")?.Value?.ToString() ?? string.Empty,
            PrimaryEntity = entity.GetAttributeValue<AliasedValue>($"filter.{SdkMessageFilter.Fields.PrimaryObjectTypeCode}")?.Value?.ToString() ?? "none",
            SecondaryEntity = entity.GetAttributeValue<AliasedValue>($"filter.{SdkMessageFilter.Fields.SecondaryObjectTypeCode}")?.Value?.ToString(),
            Stage = MapStageFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Stage)?.Value ?? StagePostOperation),
            Mode = MapModeFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Mode)?.Value ?? 0),
            ExecutionOrder = entity.GetAttributeValue<int>(SdkMessageProcessingStep.Fields.Rank),
            FilteringAttributes = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.FilteringAttributes),
            Configuration = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Configuration),
            IsEnabled = entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.StateCode)?.Value == (int)sdkmessageprocessingstep_statecode.Enabled,
            Description = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Description),
            Deployment = MapDeploymentFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.SupportedDeployment)?.Value ?? 0),
            ImpersonatingUserId = impersonatingUserRef?.Id,
            ImpersonatingUserName = impersonatingUserName,
            AsyncAutoDelete = entity.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.AsyncAutoDelete) ?? false,
            PluginTypeId = entity.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.EventHandler)?.Id,
            PluginTypeName = entity.GetAttributeValue<AliasedValue>($"plugintype.{PluginType.Fields.TypeName}")?.Value?.ToString(),
            IsManaged = entity.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.IsManaged) ?? false,
            IsCustomizable = GetBooleanManagedProperty(entity, SdkMessageProcessingStep.Fields.IsCustomizable),
            CreatedOn = entity.GetAttributeValue<DateTime?>(SdkMessageProcessingStep.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(SdkMessageProcessingStep.Fields.ModifiedOn)
        };
    }

    /// <summary>
    /// Gets a step image by name or ID.
    /// </summary>
    /// <param name="nameOrId">The image name or ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginImageInfo?> GetImageByNameOrIdAsync(
        string nameOrId,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(SdkMessageProcessingStepImage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStepImage.Fields.Name,
                SdkMessageProcessingStepImage.Fields.EntityAlias,
                SdkMessageProcessingStepImage.Fields.ImageType,
                SdkMessageProcessingStepImage.Fields.Attributes1,
                SdkMessageProcessingStepImage.Fields.MessagePropertyName,
                SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepId,
                SdkMessageProcessingStepImage.Fields.IsManaged,
                SdkMessageProcessingStepImage.Fields.IsCustomizable,
                SdkMessageProcessingStepImage.Fields.CreatedOn,
                SdkMessageProcessingStepImage.Fields.ModifiedOn),
            LinkEntities =
            {
                new LinkEntity(SdkMessageProcessingStepImage.EntityLogicalName, SdkMessageProcessingStep.EntityLogicalName, SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepId, SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(SdkMessageProcessingStep.Fields.Name),
                    EntityAlias = "step"
                }
            }
        };

        if (Guid.TryParse(nameOrId, out var id))
        {
            query.Criteria.AddCondition(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepImageId, ConditionOperator.Equal, id);
        }
        else
        {
            query.Criteria.AddCondition(SdkMessageProcessingStepImage.Fields.Name, ConditionOperator.Equal, nameOrId);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null) return null;

        return new PluginImageInfo
        {
            Id = entity.Id,
            Name = entity.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Name) ?? string.Empty,
            EntityAlias = entity.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.EntityAlias),
            ImageType = MapImageTypeFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStepImage.Fields.ImageType)?.Value ?? 0),
            Attributes = entity.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Attributes1),
            MessagePropertyName = entity.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.MessagePropertyName),
            StepId = entity.GetAttributeValue<EntityReference>(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepId)?.Id,
            StepName = entity.GetAttributeValue<AliasedValue>($"step.{SdkMessageProcessingStep.Fields.Name}")?.Value?.ToString(),
            IsManaged = entity.GetAttributeValue<bool?>(SdkMessageProcessingStepImage.Fields.IsManaged) ?? false,
            IsCustomizable = GetBooleanManagedProperty(entity, SdkMessageProcessingStepImage.Fields.IsCustomizable),
            CreatedOn = entity.GetAttributeValue<DateTime?>(SdkMessageProcessingStepImage.Fields.CreatedOn),
            ModifiedOn = entity.GetAttributeValue<DateTime?>(SdkMessageProcessingStepImage.Fields.ModifiedOn)
        };
    }

    /// <summary>
    /// Gets the SDK message ID for a message name.
    /// </summary>
    /// <param name="messageName">The message name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid?> GetSdkMessageIdAsync(
        string messageName,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(SdkMessage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(SdkMessage.Fields.SdkMessageId),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessage.Fields.Name, ConditionOperator.Equal, messageName)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Gets the SDK message filter ID for a message and entity combination.
    /// </summary>
    /// <param name="messageId">The message ID.</param>
    /// <param name="primaryEntity">The primary entity logical name.</param>
    /// <param name="secondaryEntity">Optional secondary entity logical name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid?> GetSdkMessageFilterIdAsync(
        Guid messageId,
        string primaryEntity,
        string? secondaryEntity = null,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(SdkMessageFilter.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(SdkMessageFilter.Fields.SdkMessageFilterId),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageFilter.Fields.SdkMessageId, ConditionOperator.Equal, messageId),
                    new ConditionExpression(SdkMessageFilter.Fields.PrimaryObjectTypeCode, ConditionOperator.Equal, primaryEntity)
                }
            }
        };

        if (!string.IsNullOrEmpty(secondaryEntity))
        {
            query.Criteria.AddCondition(SdkMessageFilter.Fields.SecondaryObjectTypeCode, ConditionOperator.Equal, secondaryEntity);
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault()?.Id;
    }

    #endregion

    #region Create Operations

    /// <summary>
    /// Creates or updates a plugin assembly (for classic DLL assemblies only).
    /// For NuGet packages, use <see cref="UpsertPackageAsync"/> instead.
    /// </summary>
    /// <param name="name">The assembly name.</param>
    /// <param name="content">The assembly DLL content.</param>
    /// <param name="solutionName">Optional solution to add the assembly to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid> UpsertAssemblyAsync(
        string name,
        byte[] content,
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await GetAssemblyByNameAsync(name, cancellationToken);

        var entity = new PluginAssembly
        {
            Name = name,
            Content = Convert.ToBase64String(content),
            IsolationMode = pluginassembly_isolationmode.Sandbox,
            SourceType = pluginassembly_sourcetype.Database
        };

        if (existing != null)
        {
            entity.Id = existing.Id;
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            await UpdateAsync(entity, client, cancellationToken);

            // Add to solution even on update (handles case where component exists but isn't in solution)
            if (!string.IsNullOrEmpty(solutionName))
            {
                await AddToSolutionAsync(existing.Id, ComponentTypePluginAssembly, solutionName, cancellationToken);
            }

            return existing.Id;
        }
        else
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            return await CreateWithSolutionAsync(entity, solutionName, client, cancellationToken);
        }
    }

    /// <summary>
    /// Creates or updates a plugin package (for NuGet packages).
    /// </summary>
    /// <param name="packageName">The package name from .nuspec (e.g., "ppds_MyPlugin"). This is what Dataverse uses as uniquename.</param>
    /// <param name="nupkgContent">The raw .nupkg file content.</param>
    /// <param name="solutionName">Solution to add the package to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The package ID.</returns>
    public async Task<Guid> UpsertPackageAsync(
        string packageName,
        byte[] nupkgContent,
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        // packageName comes from .nuspec <id> (parsed from .nuspec)
        // Dataverse extracts uniquename from the nupkg content, so we use packageName for lookup
        var existing = await GetPackageByNameAsync(packageName, cancellationToken);

        if (existing != null)
        {
            // UPDATE: Only update content, use solution header for solution association
            var updateEntity = new PluginPackage
            {
                Id = existing.Id,
                Content = Convert.ToBase64String(nupkgContent)
            };

            var request = new UpdateRequest { Target = updateEntity };
            if (!string.IsNullOrEmpty(solutionName))
            {
                request.Parameters["SolutionUniqueName"] = solutionName;
            }
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            await ExecuteAsync(request, client, cancellationToken);

            return existing.Id;
        }

        // CREATE: Set name and content only - Dataverse extracts uniquename from .nuspec <id> inside nupkg
        var entity = new PluginPackage
        {
            Name = packageName,
            Content = Convert.ToBase64String(nupkgContent)
        };

        return await CreateWithSolutionHeaderAsync(entity, solutionName, cancellationToken);
    }

    /// <summary>
    /// Gets the assembly ID for an assembly that is part of a plugin package.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="assemblyName">The assembly name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid?> GetAssemblyIdForPackageAsync(
        Guid packageId,
        string assemblyName,
        CancellationToken cancellationToken = default)
    {
        var assemblies = await ListAssembliesForPackageAsync(packageId, cancellationToken);
        return assemblies.FirstOrDefault(a => a.Name == assemblyName)?.Id;
    }

    /// <summary>
    /// Gets a plugin type by its fully qualified type name.
    /// </summary>
    /// <param name="typeName">The fully qualified type name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginTypeInfo?> GetPluginTypeByNameAsync(
        string typeName,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(PluginType.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginType.Fields.TypeName,
                PluginType.Fields.FriendlyName),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginType.Fields.TypeName, ConditionOperator.Equal, typeName)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null)
            return null;

        return new PluginTypeInfo
        {
            Id = entity.Id,
            TypeName = entity.GetAttributeValue<string>(PluginType.Fields.TypeName) ?? string.Empty,
            FriendlyName = entity.GetAttributeValue<string>(PluginType.Fields.FriendlyName)
        };
    }

    /// <summary>
    /// Gets a processing step by its display name.
    /// </summary>
    /// <param name="stepName">The step display name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<PluginStepInfo?> GetStepByNameAsync(
        string stepName,
        CancellationToken cancellationToken = default)
    {
        var query = new QueryExpression(SdkMessageProcessingStep.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStep.Fields.Name,
                SdkMessageProcessingStep.Fields.Stage,
                SdkMessageProcessingStep.Fields.Mode,
                SdkMessageProcessingStep.Fields.Rank,
                SdkMessageProcessingStep.Fields.FilteringAttributes,
                SdkMessageProcessingStep.Fields.Configuration,
                SdkMessageProcessingStep.Fields.StateCode,
                SdkMessageProcessingStep.Fields.Description,
                SdkMessageProcessingStep.Fields.SupportedDeployment,
                SdkMessageProcessingStep.Fields.AsyncAutoDelete),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStep.Fields.Name, ConditionOperator.Equal, stepName)
                }
            },
            LinkEntities =
            {
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SdkMessage.EntityLogicalName, SdkMessageProcessingStep.Fields.SdkMessageId, SdkMessage.Fields.SdkMessageId, JoinOperator.Inner)
                {
                    Columns = new ColumnSet(SdkMessage.Fields.Name),
                    EntityAlias = "message"
                },
                new LinkEntity(SdkMessageProcessingStep.EntityLogicalName, SdkMessageFilter.EntityLogicalName, SdkMessageProcessingStep.Fields.SdkMessageFilterId, SdkMessageFilter.Fields.SdkMessageFilterId, JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet(SdkMessageFilter.Fields.PrimaryObjectTypeCode, SdkMessageFilter.Fields.SecondaryObjectTypeCode),
                    EntityAlias = "filter"
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault();

        if (entity == null)
            return null;

        return new PluginStepInfo
        {
            Id = entity.Id,
            Name = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Name) ?? string.Empty,
            Message = entity.GetAttributeValue<AliasedValue>($"message.{SdkMessage.Fields.Name}")?.Value?.ToString() ?? string.Empty,
            PrimaryEntity = entity.GetAttributeValue<AliasedValue>($"filter.{SdkMessageFilter.Fields.PrimaryObjectTypeCode}")?.Value?.ToString() ?? "none",
            SecondaryEntity = entity.GetAttributeValue<AliasedValue>($"filter.{SdkMessageFilter.Fields.SecondaryObjectTypeCode}")?.Value?.ToString(),
            Stage = MapStageFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Stage)?.Value ?? StagePostOperation),
            Mode = MapModeFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Mode)?.Value ?? 0),
            ExecutionOrder = entity.GetAttributeValue<int>(SdkMessageProcessingStep.Fields.Rank),
            FilteringAttributes = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.FilteringAttributes),
            Configuration = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Configuration),
            IsEnabled = entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.StateCode)?.Value == (int)sdkmessageprocessingstep_statecode.Enabled,
            Description = entity.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Description),
            Deployment = MapDeploymentFromValue(entity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.SupportedDeployment)?.Value ?? 0),
            AsyncAutoDelete = entity.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.AsyncAutoDelete) ?? false
        };
    }

    /// <summary>
    /// Creates or updates a plugin type.
    /// </summary>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="typeName">The type name.</param>
    /// <param name="solutionName">Optional solution name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid> UpsertPluginTypeAsync(
        Guid assemblyId,
        string typeName,
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        // Check if type exists
        var query = new QueryExpression(PluginType.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(PluginType.Fields.PluginTypeId),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginType.Fields.PluginAssemblyId, ConditionOperator.Equal, assemblyId),
                    new ConditionExpression(PluginType.Fields.TypeName, ConditionOperator.Equal, typeName)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var existing = results.Entities.FirstOrDefault();

        if (existing != null)
        {
            return existing.Id;
        }

        var entity = new PluginType
        {
            PluginAssemblyId = new EntityReference(PluginAssembly.EntityLogicalName, assemblyId),
            TypeName = typeName,
            FriendlyName = typeName,
            Name = typeName
        };

        return await CreateWithSolutionAsync(entity, solutionName, client, cancellationToken);
    }

    /// <summary>
    /// Creates or updates a processing step.
    /// </summary>
    /// <param name="pluginTypeId">The plugin type ID.</param>
    /// <param name="stepConfig">The step configuration.</param>
    /// <param name="messageId">The SDK message ID.</param>
    /// <param name="filterId">Optional SDK message filter ID.</param>
    /// <param name="solutionName">Optional solution name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<Guid> UpsertStepAsync(
        Guid pluginTypeId,
        PluginStepConfig stepConfig,
        Guid messageId,
        Guid? filterId,
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        // Check if step exists by name
        var query = new QueryExpression(SdkMessageProcessingStep.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStep.Fields.EventHandler, ConditionOperator.Equal, pluginTypeId),
                    new ConditionExpression(SdkMessageProcessingStep.Fields.Name, ConditionOperator.Equal, stepConfig.Name)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var existing = results.Entities.FirstOrDefault();

        var entity = new SdkMessageProcessingStep
        {
            Name = stepConfig.Name,
            EventHandler = new EntityReference(PluginType.EntityLogicalName, pluginTypeId),
            SdkMessageId = new EntityReference(SdkMessage.EntityLogicalName, messageId),
            Stage = (sdkmessageprocessingstep_stage)MapStageToValue(stepConfig.Stage),
            Mode = (sdkmessageprocessingstep_mode)MapModeToValue(stepConfig.Mode),
            Rank = stepConfig.ExecutionOrder,
            SupportedDeployment = (sdkmessageprocessingstep_supporteddeployment)MapDeploymentToValue(stepConfig.Deployment),
            InvocationSource = sdkmessageprocessingstep_invocationsource.Internal
        };

        if (filterId.HasValue)
        {
            entity.SdkMessageFilterId = new EntityReference(SdkMessageFilter.EntityLogicalName, filterId.Value);
        }

        if (!string.IsNullOrEmpty(stepConfig.FilteringAttributes))
        {
            entity.FilteringAttributes = stepConfig.FilteringAttributes;
        }

        if (!string.IsNullOrEmpty(stepConfig.UnsecureConfiguration))
        {
            entity.Configuration = stepConfig.UnsecureConfiguration;
        }

        if (!string.IsNullOrEmpty(stepConfig.Description))
        {
            entity.Description = stepConfig.Description;
        }

        // Handle impersonating user (Run in User's Context)
        if (!string.IsNullOrEmpty(stepConfig.RunAsUser) &&
            !stepConfig.RunAsUser.Equals("CallingUser", StringComparison.OrdinalIgnoreCase))
        {
            if (Guid.TryParse(stepConfig.RunAsUser, out var userId))
            {
                entity.ImpersonatingUserId = new EntityReference(SystemUser.EntityLogicalName, userId);
            }
        }

        // Async auto-delete (only applies to async steps)
        if (stepConfig.AsyncAutoDelete == true && stepConfig.Mode == "Asynchronous")
        {
            entity.AsyncAutoDelete = true;
        }

        if (existing != null)
        {
            entity.Id = existing.Id;
            await UpdateAsync(entity, client, cancellationToken);

            // Add to solution even on update (handles case where component exists but isn't in solution)
            if (!string.IsNullOrEmpty(solutionName))
            {
                await AddToSolutionAsync(existing.Id, ComponentTypeSdkMessageProcessingStep, solutionName, cancellationToken);
            }

            return existing.Id;
        }
        else
        {
            return await CreateWithSolutionAsync(entity, solutionName, client, cancellationToken);
        }
    }

    /// <summary>
    /// Creates or updates a step image.
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="imageConfig">The image configuration.</param>
    /// <param name="messageName">The SDK message name (e.g., "Create", "Update", "SetState").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PpdsException">Thrown when the message does not support images.</exception>
    public async Task<Guid> UpsertImageAsync(
        Guid stepId,
        PluginImageConfig imageConfig,
        string messageName,
        CancellationToken cancellationToken = default)
    {
        var messagePropertyName = GetDefaultImagePropertyName(messageName)
            ?? throw new PpdsException(
                ErrorCodes.Plugin.ImageNotSupported,
                $"Message '{messageName}' does not support plugin images.");

        // Check if image exists
        var query = new QueryExpression(SdkMessageProcessingStepImage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepImageId),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepId, ConditionOperator.Equal, stepId),
                    new ConditionExpression(SdkMessageProcessingStepImage.Fields.Name, ConditionOperator.Equal, imageConfig.Name)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var existing = results.Entities.FirstOrDefault();

        var entity = new SdkMessageProcessingStepImage
        {
            SdkMessageProcessingStepId = new EntityReference(SdkMessageProcessingStep.EntityLogicalName, stepId),
            Name = imageConfig.Name,
            EntityAlias = imageConfig.EntityAlias ?? imageConfig.Name,
            ImageType = (sdkmessageprocessingstepimage_imagetype)MapImageTypeToValue(imageConfig.ImageType),
            MessagePropertyName = messagePropertyName
        };

        if (!string.IsNullOrEmpty(imageConfig.Attributes))
        {
            entity.Attributes1 = imageConfig.Attributes;
        }

        if (existing != null)
        {
            entity.Id = existing.Id;
            await UpdateAsync(entity, client, cancellationToken);
            return existing.Id;
        }
        else
        {
            return await CreateAsync(entity, client, cancellationToken);
        }
    }

    #endregion

    #region Update Operations

    /// <summary>
    /// Updates a processing step with the specified changes.
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="request">The update request containing properties to change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the step is managed and not customizable.</exception>
    public async Task UpdateStepAsync(
        Guid stepId,
        StepUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Verify the step exists and check managed state
        var existingStep = await GetStepByIdWithManagedStateAsync(stepId, cancellationToken);
        if (existingStep == null)
        {
            throw new InvalidOperationException($"Step with ID '{stepId}' not found.");
        }

        // Check managed state
        var isManaged = existingStep.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.IsManaged) ?? false;
        var isCustomizable = GetBooleanManagedProperty(existingStep, SdkMessageProcessingStep.Fields.IsCustomizable);

        if (isManaged && !isCustomizable)
        {
            var stepName = existingStep.GetAttributeValue<string>(SdkMessageProcessingStep.Fields.Name);
            throw new InvalidOperationException($"Cannot update: {stepName} is managed. Managed components cannot be modified in this environment.");
        }

        // Build update entity with only changed properties
        var entity = new SdkMessageProcessingStep { Id = stepId };
        var hasChanges = false;

        if (request.Mode != null)
        {
            entity.Mode = (sdkmessageprocessingstep_mode)MapModeToValue(request.Mode);
            hasChanges = true;
        }

        if (request.Stage != null)
        {
            entity.Stage = (sdkmessageprocessingstep_stage)MapStageToValue(request.Stage);
            hasChanges = true;
        }

        if (request.Rank != null)
        {
            entity.Rank = request.Rank.Value;
            hasChanges = true;
        }

        if (request.FilteringAttributes != null)
        {
            entity.FilteringAttributes = request.FilteringAttributes;
            hasChanges = true;
        }

        if (request.Description != null)
        {
            entity.Description = request.Description;
            hasChanges = true;
        }

        if (!hasChanges)
        {
            return; // Nothing to update
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await UpdateAsync(entity, client, cancellationToken);
    }

    /// <summary>
    /// Updates a step image with the specified changes.
    /// </summary>
    /// <param name="imageId">The image ID.</param>
    /// <param name="request">The update request containing properties to change.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when the image is managed and not customizable.</exception>
    public async Task UpdateImageAsync(
        Guid imageId,
        ImageUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        // Verify the image exists and check managed state
        var existingImage = await GetImageByIdWithManagedStateAsync(imageId, cancellationToken);
        if (existingImage == null)
        {
            throw new InvalidOperationException($"Image with ID '{imageId}' not found.");
        }

        // Check managed state
        var isManaged = existingImage.GetAttributeValue<bool?>(SdkMessageProcessingStepImage.Fields.IsManaged) ?? false;
        var isCustomizable = GetBooleanManagedProperty(existingImage, SdkMessageProcessingStepImage.Fields.IsCustomizable);

        if (isManaged && !isCustomizable)
        {
            var imageName = existingImage.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Name);
            throw new InvalidOperationException($"Cannot update: {imageName} is managed. Managed components cannot be modified in this environment.");
        }

        // Build update entity with only changed properties
        var entity = new SdkMessageProcessingStepImage { Id = imageId };
        var hasChanges = false;

        if (request.Attributes != null)
        {
            entity.Attributes1 = request.Attributes;
            hasChanges = true;
        }

        if (request.Name != null)
        {
            entity.Name = request.Name;
            hasChanges = true;
        }

        if (!hasChanges)
        {
            return; // Nothing to update
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await UpdateAsync(entity, client, cancellationToken);
    }

    private async Task<Entity?> GetStepByIdWithManagedStateAsync(Guid stepId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression(SdkMessageProcessingStep.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStep.Fields.Name,
                SdkMessageProcessingStep.Fields.IsManaged,
                SdkMessageProcessingStep.Fields.IsCustomizable),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStep.Fields.SdkMessageProcessingStepId, ConditionOperator.Equal, stepId)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault();
    }

    private async Task<Entity?> GetImageByIdWithManagedStateAsync(Guid imageId, CancellationToken cancellationToken)
    {
        var query = new QueryExpression(SdkMessageProcessingStepImage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStepImage.Fields.Name,
                SdkMessageProcessingStepImage.Fields.IsManaged,
                SdkMessageProcessingStepImage.Fields.IsCustomizable),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepImageId, ConditionOperator.Equal, imageId)
                }
            }
        };

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        return results.Entities.FirstOrDefault();
    }

    private static bool GetBooleanManagedProperty(Entity entity, string attributeName)
    {
        var value = entity.GetAttributeValue<BooleanManagedProperty>(attributeName);
        return value?.Value ?? true; // Default to true (customizable) if not set
    }

    #endregion

    #region Delete Operations

    /// <summary>
    /// Deletes a step image.
    /// </summary>
    /// <param name="imageId">The image ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, imageId, client, cancellationToken);
    }

    /// <summary>
    /// Deletes a processing step (also deletes child images in parallel).
    /// </summary>
    /// <param name="stepId">The step ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeleteStepAsync(Guid stepId, CancellationToken cancellationToken = default)
    {
        // Delete images first - fetch list and delete in parallel
        var images = await ListImagesForStepAsync(stepId, cancellationToken);

        if (images.Count > 0)
        {
            var parallelism = _pool.GetTotalRecommendedParallelism();
            await Parallel.ForEachAsync(
                images,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (image, ct) =>
                {
                    await using var client = await _pool.GetClientAsync(cancellationToken: ct);
                    await DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, image.Id, client, ct);
                });
        }

        await using var stepClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(SdkMessageProcessingStep.EntityLogicalName, stepId, stepClient, cancellationToken);
    }

    /// <summary>
    /// Deletes a plugin type (only if it has no steps).
    /// </summary>
    /// <param name="pluginTypeId">The plugin type ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DeletePluginTypeAsync(Guid pluginTypeId, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(PluginType.EntityLogicalName, pluginTypeId, client, cancellationToken);
    }

    #endregion

    #region Download Operations

    /// <summary>
    /// Downloads the binary content of a plugin assembly.
    /// </summary>
    /// <param name="assemblyId">The assembly ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple containing the binary content and assembly name with .dll extension.</returns>
    /// <exception cref="PpdsException">Thrown when assembly has no content (e.g., source type is Disk or GAC).</exception>
    public async Task<(byte[] Content, string FileName)> DownloadAssemblyAsync(
        Guid assemblyId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var entity = await RetrieveAsync(
            PluginAssembly.EntityLogicalName,
            assemblyId,
            new ColumnSet(PluginAssembly.Fields.Name, PluginAssembly.Fields.Content),
            client,
            cancellationToken);

        var name = entity.GetAttributeValue<string>(PluginAssembly.Fields.Name) ?? "assembly";
        var content = entity.GetAttributeValue<string>(PluginAssembly.Fields.Content);

        if (string.IsNullOrEmpty(content))
        {
            throw new PpdsException(
                ErrorCodes.Plugin.NoContent,
                $"Assembly '{name}' has no content. The source type may be Disk or GAC.");
        }

        var bytes = Convert.FromBase64String(content);
        var fileName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? name
            : $"{name}.dll";

        return (bytes, fileName);
    }

    /// <summary>
    /// Downloads the binary content of a plugin package.
    /// </summary>
    /// <param name="packageId">The package ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Tuple containing the binary content and package name with .nupkg extension.</returns>
    /// <exception cref="PpdsException">Thrown when package has no content.</exception>
    public async Task<(byte[] Content, string FileName)> DownloadPackageAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var entity = await RetrieveAsync(
            PluginPackage.EntityLogicalName,
            packageId,
            new ColumnSet(PluginPackage.Fields.Name, PluginPackage.Fields.Version, PluginPackage.Fields.Content),
            client,
            cancellationToken);

        var name = entity.GetAttributeValue<string>(PluginPackage.Fields.Name) ?? "package";
        var version = entity.GetAttributeValue<string>(PluginPackage.Fields.Version);
        var content = entity.GetAttributeValue<string>(PluginPackage.Fields.Content);

        if (string.IsNullOrEmpty(content))
        {
            throw new PpdsException(
                ErrorCodes.Plugin.NoContent,
                $"Package '{name}' has no content.");
        }

        var bytes = Convert.FromBase64String(content);

        // Format: PackageName.Version.nupkg (matching NuGet convention)
        var fileName = !string.IsNullOrEmpty(version)
            ? $"{name}.{version}.nupkg"
            : $"{name}.nupkg";

        return (bytes, fileName);
    }

    #endregion

    #region Unregister Operations

    /// <summary>
    /// Unregisters a step image by ID.
    /// </summary>
    public async Task<UnregisterResult> UnregisterImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        // Get image info first
        var query = new QueryExpression(SdkMessageProcessingStepImage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStepImage.Fields.Name,
                SdkMessageProcessingStepImage.Fields.IsManaged)
        };
        query.Criteria.AddCondition(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepImageId, ConditionOperator.Equal, imageId);

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var results = await RetrieveMultipleAsync(query, client, cancellationToken);
        var entity = results.Entities.FirstOrDefault()
            ?? throw new UnregisterException(
                $"Image with ID {imageId} not found.",
                imageId.ToString(),
                "Image",
                ErrorCodes.Plugin.NotFound);

        var name = entity.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Name) ?? string.Empty;
        var isManaged = entity.GetAttributeValue<bool?>(SdkMessageProcessingStepImage.Fields.IsManaged) ?? false;

        if (isManaged)
        {
            throw new UnregisterException(
                $"Cannot unregister: {name} is managed. Managed components cannot be deleted in this environment.",
                name,
                "Image",
                ErrorCodes.Plugin.ManagedComponent);
        }

        await DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, imageId, client, cancellationToken);

        return new UnregisterResult
        {
            EntityName = name,
            EntityType = "Image",
            ImagesDeleted = 1
        };
    }

    /// <summary>
    /// Unregisters a processing step and optionally its images.
    /// </summary>
    public async Task<UnregisterResult> UnregisterStepAsync(Guid stepId, bool force = false, CancellationToken cancellationToken = default)
    {
        // Get step info
        var step = await GetStepByNameOrIdAsync(stepId.ToString(), cancellationToken)
            ?? throw new UnregisterException(
                $"Step with ID {stepId} not found.",
                stepId.ToString(),
                "Step",
                ErrorCodes.Plugin.NotFound);

        if (step.IsManaged)
        {
            throw new UnregisterException(
                $"Cannot unregister: {step.Name} is managed. Managed components cannot be deleted in this environment.",
                step.Name,
                "Step",
                ErrorCodes.Plugin.ManagedComponent);
        }

        // Check for images
        var images = await ListImagesForStepAsync(stepId, cancellationToken);

        if (images.Count > 0 && !force)
        {
            throw new UnregisterException(
                $"Cannot unregister step: {step.Name}. Step has {images.Count} image(s). Use --force to cascade delete all images.",
                step.Name,
                "Step",
                ErrorCodes.Plugin.HasChildren,
                imageCount: images.Count);
        }

        var result = new UnregisterResult
        {
            EntityName = step.Name,
            EntityType = "Step"
        };

        // Delete images in parallel if force
        if (images.Count > 0)
        {
            var parallelism = _pool.GetTotalRecommendedParallelism();
            await Parallel.ForEachAsync(
                images,
                new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
                async (image, ct) =>
                {
                    await using var imageClient = await _pool.GetClientAsync(cancellationToken: ct);
                    await DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, image.Id, imageClient, ct);
                });
            result.ImagesDeleted = images.Count;
        }

        // Delete step
        await using var stepClient = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(SdkMessageProcessingStep.EntityLogicalName, stepId, stepClient, cancellationToken);
        result.StepsDeleted = 1;

        return result;
    }

    /// <summary>
    /// Unregisters a plugin type and optionally its steps and images.
    /// </summary>
    public async Task<UnregisterResult> UnregisterPluginTypeAsync(Guid pluginTypeId, bool force = false, CancellationToken cancellationToken = default)
    {
        // Get type info
        var pluginType = await GetPluginTypeByNameOrIdAsync(pluginTypeId.ToString(), cancellationToken)
            ?? throw new UnregisterException(
                $"Plugin type with ID {pluginTypeId} not found.",
                pluginTypeId.ToString(),
                "Type",
                ErrorCodes.Plugin.NotFound);

        if (pluginType.IsManaged)
        {
            throw new UnregisterException(
                $"Cannot unregister: {pluginType.TypeName} is managed. Managed components cannot be deleted in this environment.",
                pluginType.TypeName,
                "Type",
                ErrorCodes.Plugin.ManagedComponent);
        }

        // Check for steps
        var steps = await ListStepsForTypeAsync(pluginTypeId, options: null, cancellationToken);

        if (steps.Count > 0 && !force)
        {
            throw new UnregisterException(
                $"Cannot unregister plugin type: {pluginType.TypeName}. Type has {steps.Count} active step(s). Use --force to cascade delete all steps and images.",
                pluginType.TypeName,
                "Type",
                ErrorCodes.Plugin.HasChildren,
                stepCount: steps.Count);
        }

        var result = new UnregisterResult
        {
            EntityName = pluginType.TypeName,
            EntityType = "Type"
        };

        // Delete steps (and their images) in sequence
        foreach (var step in steps)
        {
            var stepResult = await UnregisterStepAsync(step.Id, force: true, cancellationToken);
            result += stepResult;
        }

        // Delete type
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(PluginType.EntityLogicalName, pluginTypeId, client, cancellationToken);
        result.TypesDeleted = 1;

        return result;
    }

    /// <summary>
    /// Unregisters an assembly and optionally all its types, steps, and images.
    /// </summary>
    public async Task<UnregisterResult> UnregisterAssemblyAsync(Guid assemblyId, bool force = false, CancellationToken cancellationToken = default)
    {
        // Get assembly info
        var assembly = await GetAssemblyByIdAsync(assemblyId, cancellationToken)
            ?? throw new UnregisterException(
                $"Assembly with ID {assemblyId} not found.",
                assemblyId.ToString(),
                "Assembly",
                ErrorCodes.Plugin.NotFound);

        if (assembly.IsManaged)
        {
            throw new UnregisterException(
                $"Cannot unregister: {assembly.Name} is managed. Managed components cannot be deleted in this environment.",
                assembly.Name,
                "Assembly",
                ErrorCodes.Plugin.ManagedComponent);
        }

        // Get types and their steps
        var types = await ListTypesForAssemblyAsync(assemblyId, cancellationToken);
        var totalSteps = 0;

        foreach (var type in types)
        {
            var steps = await ListStepsForTypeAsync(type.Id, options: null, cancellationToken);
            totalSteps += steps.Count;
        }

        if (totalSteps > 0 && !force)
        {
            throw new UnregisterException(
                $"Cannot unregister assembly: {assembly.Name}. Assembly has {types.Count} plugin type(s) with {totalSteps} active step(s). Use --force to cascade delete all children.",
                assembly.Name,
                "Assembly",
                ErrorCodes.Plugin.HasChildren,
                typeCount: types.Count,
                stepCount: totalSteps);
        }

        var result = new UnregisterResult
        {
            EntityName = assembly.Name,
            EntityType = "Assembly"
        };

        // Delete types (and their steps/images) in sequence
        foreach (var type in types)
        {
            var typeResult = await UnregisterPluginTypeAsync(type.Id, force: true, cancellationToken);
            result += typeResult;
        }

        // Delete assembly
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(PluginAssembly.EntityLogicalName, assemblyId, client, cancellationToken);
        result.AssembliesDeleted = 1;

        return result;
    }

    /// <summary>
    /// Unregisters a plugin package and optionally all its assemblies, types, steps, and images.
    /// </summary>
    public async Task<UnregisterResult> UnregisterPackageAsync(Guid packageId, bool force = false, CancellationToken cancellationToken = default)
    {
        // Get package info
        var package = await GetPackageByIdAsync(packageId, cancellationToken)
            ?? throw new UnregisterException(
                $"Package with ID {packageId} not found.",
                packageId.ToString(),
                "Package",
                ErrorCodes.Plugin.NotFound);

        if (package.IsManaged)
        {
            throw new UnregisterException(
                $"Cannot unregister: {package.Name} is managed. Managed components cannot be deleted in this environment.",
                package.Name,
                "Package",
                ErrorCodes.Plugin.ManagedComponent);
        }

        // Get assemblies
        var assemblies = await ListAssembliesForPackageAsync(packageId, cancellationToken);

        if (assemblies.Count > 0 && !force)
        {
            throw new UnregisterException(
                $"Cannot unregister package: {package.Name}. Package has {assemblies.Count} assembly(ies). Use --force to cascade delete all children.",
                package.Name,
                "Package",
                ErrorCodes.Plugin.HasChildren,
                assemblyCount: assemblies.Count);
        }

        var result = new UnregisterResult
        {
            EntityName = package.Name,
            EntityType = "Package"
        };

        // Delete assemblies (and their types/steps/images) in sequence
        foreach (var assembly in assemblies)
        {
            var assemblyResult = await UnregisterAssemblyAsync(assembly.Id, force: true, cancellationToken);
            result += assemblyResult;
        }

        // Delete package
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        await DeleteAsync(PluginPackage.EntityLogicalName, packageId, client, cancellationToken);
        result.PackagesDeleted = 1;

        return result;
    }

    #endregion

    #region Solution Operations

    /// <summary>
    /// Adds a component to a solution.
    /// </summary>
    /// <param name="componentId">The component ID.</param>
    /// <param name="componentType">The component type code.</param>
    /// <param name="solutionName">The solution unique name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task AddToSolutionAsync(
        Guid componentId,
        int componentType,
        string solutionName,
        CancellationToken cancellationToken = default)
    {
        var request = new AddSolutionComponentRequest
        {
            ComponentId = componentId,
            ComponentType = componentType,
            SolutionUniqueName = solutionName,
            AddRequiredComponents = false
        };

        try
        {
            await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
            await ExecuteAsync(request, client, cancellationToken);
        }
        catch (FaultException<OrganizationServiceFault> ex) when (ex.Detail?.ErrorCode == -2147159998)
        {
            // Error code 0x80048542: Component already exists in the solution
            // This is expected when re-deploying - not an error
            _logger.LogDebug(
                "Component {ComponentId} already exists in solution {SolutionName}, skipping",
                componentId, solutionName);
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Gets the solution component type code for an entity.
    /// Uses well-known values for system entities, queries metadata for custom entities like pluginpackage.
    /// </summary>
    private async Task<int> GetComponentTypeAsync(
        string entityLogicalName,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        // Check well-known types first (no API call needed)
        if (WellKnownComponentTypes.TryGetValue(entityLogicalName, out var wellKnownType))
        {
            return wellKnownType;
        }

        // Check cache
        if (_entityTypeCodeCache.TryGetValue(entityLogicalName, out var cachedType))
        {
            return cachedType;
        }

        // Query entity metadata to get ObjectTypeCode
        var request = new RetrieveEntityRequest
        {
            LogicalName = entityLogicalName,
            EntityFilters = EntityFilters.Entity
        };

        try
        {
            var response = (RetrieveEntityResponse)await ExecuteAsync(request, client, cancellationToken);
            var objectTypeCode = response.EntityMetadata.ObjectTypeCode ?? 0;
            _entityTypeCodeCache[entityLogicalName] = objectTypeCode;
            return objectTypeCode;
        }
        catch (FaultException<OrganizationServiceFault> ex)
        {
            // Entity doesn't exist or user lacks metadata read permissions - skip solution addition
            _logger.LogDebug(
                "Could not retrieve component type for entity '{EntityLogicalName}': {ErrorMessage} (ErrorCode: {ErrorCode})",
                entityLogicalName,
                ex.Detail?.Message ?? ex.Message,
                ex.Detail?.ErrorCode);
            return 0;
        }
        catch (FaultException ex)
        {
            // Generic SOAP fault - entity may not exist in this environment
            _logger.LogDebug(
                "Could not retrieve component type for entity '{EntityLogicalName}': {ErrorMessage}",
                entityLogicalName,
                ex.Message);
            return 0;
        }
    }

    private async Task<Guid> CreateWithSolutionAsync(
        Entity entity,
        string? solutionName,
        IOrganizationService client,
        CancellationToken cancellationToken)
    {
        var id = await CreateAsync(entity, client, cancellationToken);

        if (!string.IsNullOrEmpty(solutionName))
        {
            var componentType = await GetComponentTypeAsync(entity.LogicalName, client, cancellationToken);

            if (componentType > 0)
            {
                await AddToSolutionAsync(id, componentType, solutionName, cancellationToken);
            }
        }

        return id;
    }

    /// <summary>
    /// Creates an entity with atomic solution association using CreateRequest.SolutionUniqueName.
    /// This is the SDK equivalent of the MSCRM.SolutionUniqueName HTTP header.
    /// </summary>
    private async Task<Guid> CreateWithSolutionHeaderAsync(
        Entity entity,
        string? solutionName,
        CancellationToken cancellationToken)
    {
        var request = new CreateRequest { Target = entity };

        if (!string.IsNullOrEmpty(solutionName))
        {
            request.Parameters["SolutionUniqueName"] = solutionName;
        }

        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);
        var response = (CreateResponse)await ExecuteAsync(request, client, cancellationToken);
        return response.id;
    }

    private static string MapStageFromValue(int value) => value switch
    {
        StagePreValidation => "PreValidation",
        StagePreOperation => "PreOperation",
        StageMainOperation => "MainOperation",
        StagePostOperation => "PostOperation",
        _ => value.ToString()
    };

    private static string MapModeFromValue(int value) => value switch
    {
        0 => "Synchronous",
        1 => "Asynchronous",
        _ => value.ToString()
    };

    private static string MapImageTypeFromValue(int value) => value switch
    {
        0 => "PreImage",
        1 => "PostImage",
        2 => "Both",
        _ => value.ToString()
    };

    private static int MapStageToValue(string stage) => stage switch
    {
        "PreValidation" => StagePreValidation,
        "PreOperation" => StagePreOperation,
        "MainOperation" => StageMainOperation,
        "PostOperation" => StagePostOperation,
        _ => int.TryParse(stage, out var v) ? v : StagePostOperation
    };

    private static int MapModeToValue(string mode) => mode switch
    {
        "Synchronous" => 0,
        "Asynchronous" => 1,
        _ => int.TryParse(mode, out var v) ? v : 0
    };

    private static int MapImageTypeToValue(string imageType) => imageType switch
    {
        "PreImage" => 0,
        "PostImage" => 1,
        "Both" => 2,
        _ => int.TryParse(imageType, out var v) ? v : 0
    };

    private static string MapDeploymentFromValue(int value) => value switch
    {
        0 => "ServerOnly",
        1 => "Offline",
        2 => "Both",
        _ => value.ToString()
    };

    private static int MapDeploymentToValue(string? deployment) => deployment switch
    {
        "ServerOnly" or null => 0,
        "Offline" => 1,
        "Both" => 2,
        _ => int.TryParse(deployment, out var v) ? v : 0
    };

    // Native async helpers - use async SDK when available, otherwise fall back to Task.Run
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

    private static async Task UpdateAsync(
        Entity entity,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            await asyncService.UpdateAsync(entity, cancellationToken);
        else
            await Task.Run(() => client.Update(entity), cancellationToken);
    }

    private static async Task DeleteAsync(
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

    private static async Task<OrganizationResponse> ExecuteAsync(
        OrganizationRequest request,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            return await asyncService.ExecuteAsync(request, cancellationToken);
        return await Task.Run(() => client.Execute(request), cancellationToken);
    }

    private static async Task<Entity> RetrieveAsync(
        string entityName,
        Guid id,
        ColumnSet columnSet,
        IOrganizationService client,
        CancellationToken cancellationToken = default)
    {
        if (client is IOrganizationServiceAsync2 asyncService)
            return await asyncService.RetrieveAsync(entityName, id, columnSet, cancellationToken);
        return await Task.Run(() => client.Retrieve(entityName, id, columnSet), cancellationToken);
    }

    #endregion
}

#region Info Models

/// <summary>
/// Information about a plugin assembly in Dataverse.
/// </summary>
public sealed class PluginAssemblyInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? PublicKeyToken { get; set; }
    public int IsolationMode { get; set; }
    public int SourceType { get; set; }
    public bool IsManaged { get; set; }
    public Guid? PackageId { get; set; }
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Information about a plugin package (NuGet) in Dataverse.
/// </summary>
public sealed class PluginPackageInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? UniqueName { get; set; }
    public string? Version { get; set; }
    public bool IsManaged { get; set; }
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Information about a plugin type in Dataverse.
/// </summary>
public sealed class PluginTypeInfo
{
    public Guid Id { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public Guid? AssemblyId { get; set; }
    public string? AssemblyName { get; set; }
    public bool IsManaged { get; set; }
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Information about a processing step in Dataverse.
/// </summary>
public sealed class PluginStepInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string PrimaryEntity { get; set; } = string.Empty;
    public string? SecondaryEntity { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty;
    public int ExecutionOrder { get; set; }
    public string? FilteringAttributes { get; set; }
    public string? Configuration { get; set; }
    public bool IsEnabled { get; set; }
    public string? Description { get; set; }
    public string Deployment { get; set; } = "ServerOnly";
    public Guid? ImpersonatingUserId { get; set; }
    public string? ImpersonatingUserName { get; set; }
    public bool AsyncAutoDelete { get; set; }
    public Guid? PluginTypeId { get; set; }
    public string? PluginTypeName { get; set; }
    public bool IsManaged { get; set; }
    public bool IsCustomizable { get; set; } = true;
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Information about a step image in Dataverse.
/// </summary>
public sealed class PluginImageInfo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? EntityAlias { get; set; }
    public string ImageType { get; set; } = string.Empty;
    public string? Attributes { get; set; }
    public string? MessagePropertyName { get; set; }
    public Guid? StepId { get; set; }
    public string? StepName { get; set; }
    public bool IsManaged { get; set; }
    public bool IsCustomizable { get; set; } = true;
    public DateTime? CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// Result of an unregister operation.
/// </summary>
public sealed class UnregisterResult
{
    /// <summary>
    /// Name of the deleted entity.
    /// </summary>
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Type of entity that was deleted (Package, Assembly, Type, Step, Image).
    /// </summary>
    public string EntityType { get; set; } = string.Empty;

    /// <summary>
    /// Number of packages deleted (for cascade operations).
    /// </summary>
    public int PackagesDeleted { get; set; }

    /// <summary>
    /// Number of assemblies deleted (for cascade operations).
    /// </summary>
    public int AssembliesDeleted { get; set; }

    /// <summary>
    /// Number of plugin types deleted (for cascade operations).
    /// </summary>
    public int TypesDeleted { get; set; }

    /// <summary>
    /// Number of steps deleted (for cascade operations).
    /// </summary>
    public int StepsDeleted { get; set; }

    /// <summary>
    /// Number of images deleted (for cascade operations).
    /// </summary>
    public int ImagesDeleted { get; set; }

    /// <summary>
    /// Gets the total number of entities deleted.
    /// </summary>
    public int TotalDeleted => PackagesDeleted + AssembliesDeleted + TypesDeleted + StepsDeleted + ImagesDeleted;

    /// <summary>
    /// Combines two unregister results.
    /// </summary>
    public static UnregisterResult operator +(UnregisterResult a, UnregisterResult b)
    {
        return new UnregisterResult
        {
            EntityName = a.EntityName,
            EntityType = a.EntityType,
            PackagesDeleted = a.PackagesDeleted + b.PackagesDeleted,
            AssembliesDeleted = a.AssembliesDeleted + b.AssembliesDeleted,
            TypesDeleted = a.TypesDeleted + b.TypesDeleted,
            StepsDeleted = a.StepsDeleted + b.StepsDeleted,
            ImagesDeleted = a.ImagesDeleted + b.ImagesDeleted
        };
    }
}

/// <summary>
/// Exception thrown when an unregister operation cannot proceed.
/// </summary>
public sealed class UnregisterException : PpdsException
{
    /// <summary>
    /// The name of the entity that could not be unregistered.
    /// </summary>
    public string EntityName { get; }

    /// <summary>
    /// The type of entity (Assembly, Package, Type, Step).
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// Number of child assemblies that exist (for package).
    /// </summary>
    public int AssemblyCount { get; }

    /// <summary>
    /// Number of child types that exist (for assembly/package).
    /// </summary>
    public int TypeCount { get; }

    /// <summary>
    /// Number of child steps that exist (for type/assembly/package).
    /// </summary>
    public int StepCount { get; }

    /// <summary>
    /// Number of child images that exist (for step).
    /// </summary>
    public int ImageCount { get; }

    public UnregisterException(
        string message,
        string entityName,
        string entityType,
        string errorCode,
        int assemblyCount = 0,
        int typeCount = 0,
        int stepCount = 0,
        int imageCount = 0)
        : base(errorCode, message)
    {
        EntityName = entityName;
        EntityType = entityType;
        AssemblyCount = assemblyCount;
        TypeCount = typeCount;
        StepCount = stepCount;
        ImageCount = imageCount;
    }
}

#endregion
