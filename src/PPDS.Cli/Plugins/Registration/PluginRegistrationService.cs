using System.Collections.Concurrent;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Plugins.Models;
using PPDS.Dataverse.Generated;

namespace PPDS.Cli.Plugins.Registration;

/// <summary>
/// Service for managing plugin registrations in Dataverse.
/// </summary>
public sealed class PluginRegistrationService
{
    private readonly IOrganizationService _service;
    private readonly IOrganizationServiceAsync2? _asyncService;
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

    #endregion

    /// <summary>
    /// Creates a new instance of the plugin registration service.
    /// </summary>
    /// <param name="service">The Dataverse organization service.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public PluginRegistrationService(IOrganizationService service, ILogger<PluginRegistrationService> logger)
    {
        _service = service;
        _logger = logger;
        // Use native async when available (ServiceClient implements IOrganizationServiceAsync2)
        _asyncService = service as IOrganizationServiceAsync2;
    }

    #region Query Operations

    /// <summary>
    /// Lists all plugin assemblies in the environment.
    /// </summary>
    /// <param name="assemblyNameFilter">Optional filter by assembly name.</param>
    public async Task<List<PluginAssemblyInfo>> ListAssembliesAsync(string? assemblyNameFilter = null)
    {
        var query = new QueryExpression(PluginAssembly.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginAssembly.Fields.Name,
                PluginAssembly.Fields.Version,
                PluginAssembly.Fields.PublicKeyToken,
                PluginAssembly.Fields.Culture,
                PluginAssembly.Fields.IsolationMode,
                PluginAssembly.Fields.SourceType),
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

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginAssemblyInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(PluginAssembly.Fields.Name) ?? string.Empty,
            Version = e.GetAttributeValue<string>(PluginAssembly.Fields.Version),
            PublicKeyToken = e.GetAttributeValue<string>(PluginAssembly.Fields.PublicKeyToken),
            IsolationMode = e.GetAttributeValue<OptionSetValue>(PluginAssembly.Fields.IsolationMode)?.Value ?? (int)pluginassembly_isolationmode.Sandbox
        }).ToList();
    }

    /// <summary>
    /// Lists all plugin packages in the environment.
    /// </summary>
    /// <param name="packageNameFilter">Optional filter by package name or unique name.</param>
    public async Task<List<PluginPackageInfo>> ListPackagesAsync(string? packageNameFilter = null)
    {
        var query = new QueryExpression(PluginPackage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginPackage.Fields.Name,
                PluginPackage.Fields.UniqueName,
                PluginPackage.Fields.Version),
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

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginPackageInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(PluginPackage.Fields.Name) ?? string.Empty,
            UniqueName = e.GetAttributeValue<string>(PluginPackage.Fields.UniqueName),
            Version = e.GetAttributeValue<string>(PluginPackage.Fields.Version)
        }).ToList();
    }

    /// <summary>
    /// Lists all assemblies contained in a plugin package.
    /// </summary>
    public async Task<List<PluginAssemblyInfo>> ListAssembliesForPackageAsync(Guid packageId)
    {
        var query = new QueryExpression(PluginAssembly.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginAssembly.Fields.Name,
                PluginAssembly.Fields.Version,
                PluginAssembly.Fields.PublicKeyToken,
                PluginAssembly.Fields.Culture,
                PluginAssembly.Fields.IsolationMode,
                PluginAssembly.Fields.SourceType),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginAssembly.Fields.PackageId, ConditionOperator.Equal, packageId)
                }
            },
            Orders = { new OrderExpression(PluginAssembly.Fields.Name, OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginAssemblyInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(PluginAssembly.Fields.Name) ?? string.Empty,
            Version = e.GetAttributeValue<string>(PluginAssembly.Fields.Version),
            PublicKeyToken = e.GetAttributeValue<string>(PluginAssembly.Fields.PublicKeyToken),
            IsolationMode = e.GetAttributeValue<OptionSetValue>(PluginAssembly.Fields.IsolationMode)?.Value ?? (int)pluginassembly_isolationmode.Sandbox
        }).ToList();
    }

    /// <summary>
    /// Lists all plugin types for a package by querying through the package's assemblies.
    /// </summary>
    public async Task<List<PluginTypeInfo>> ListTypesForPackageAsync(Guid packageId)
    {
        var assemblies = await ListAssembliesForPackageAsync(packageId);

        if (assemblies.Count == 0)
            return [];

        var allTypes = new List<PluginTypeInfo>();
        foreach (var assembly in assemblies)
        {
            var types = await ListTypesForAssemblyAsync(assembly.Id);
            allTypes.AddRange(types);
        }

        return allTypes;
    }

    /// <summary>
    /// Lists all plugin types for an assembly.
    /// </summary>
    public async Task<List<PluginTypeInfo>> ListTypesForAssemblyAsync(Guid assemblyId)
    {
        var query = new QueryExpression(PluginType.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                PluginType.Fields.TypeName,
                PluginType.Fields.FriendlyName,
                PluginType.Fields.Name),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(PluginType.Fields.PluginAssemblyId, ConditionOperator.Equal, assemblyId)
                }
            },
            Orders = { new OrderExpression(PluginType.Fields.TypeName, OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginTypeInfo
        {
            Id = e.Id,
            TypeName = e.GetAttributeValue<string>(PluginType.Fields.TypeName) ?? string.Empty,
            FriendlyName = e.GetAttributeValue<string>(PluginType.Fields.FriendlyName)
        }).ToList();
    }

    /// <summary>
    /// Lists all processing steps for a plugin type.
    /// </summary>
    public async Task<List<PluginStepInfo>> ListStepsForTypeAsync(Guid pluginTypeId)
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
                SdkMessageProcessingStep.Fields.AsyncAutoDelete),
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

        var results = await RetrieveMultipleAsync(query);

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
                AsyncAutoDelete = e.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.AsyncAutoDelete) ?? false
            };
        }).ToList();
    }

    /// <summary>
    /// Lists all images for a processing step.
    /// </summary>
    public async Task<List<PluginImageInfo>> ListImagesForStepAsync(Guid stepId)
    {
        var query = new QueryExpression(SdkMessageProcessingStepImage.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                SdkMessageProcessingStepImage.Fields.Name,
                SdkMessageProcessingStepImage.Fields.EntityAlias,
                SdkMessageProcessingStepImage.Fields.ImageType,
                SdkMessageProcessingStepImage.Fields.Attributes1),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression(SdkMessageProcessingStepImage.Fields.SdkMessageProcessingStepId, ConditionOperator.Equal, stepId)
                }
            },
            Orders = { new OrderExpression(SdkMessageProcessingStepImage.Fields.Name, OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginImageInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Name) ?? string.Empty,
            EntityAlias = e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.EntityAlias),
            ImageType = MapImageTypeFromValue(e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStepImage.Fields.ImageType)?.Value ?? 0),
            Attributes = e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Attributes1)
        }).ToList();
    }

    #endregion

    #region Lookup Operations

    /// <summary>
    /// Gets an assembly by name.
    /// </summary>
    public async Task<PluginAssemblyInfo?> GetAssemblyByNameAsync(string name)
    {
        var assemblies = await ListAssembliesAsync(name);
        return assemblies.FirstOrDefault();
    }

    /// <summary>
    /// Gets a plugin package by name or unique name.
    /// </summary>
    public async Task<PluginPackageInfo?> GetPackageByNameAsync(string name)
    {
        var packages = await ListPackagesAsync(name);
        return packages.FirstOrDefault();
    }

    /// <summary>
    /// Gets the SDK message ID for a message name.
    /// </summary>
    public async Task<Guid?> GetSdkMessageIdAsync(string messageName)
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

        var results = await RetrieveMultipleAsync(query);
        return results.Entities.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Gets the SDK message filter ID for a message and entity combination.
    /// </summary>
    public async Task<Guid?> GetSdkMessageFilterIdAsync(Guid messageId, string primaryEntity, string? secondaryEntity = null)
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

        var results = await RetrieveMultipleAsync(query);
        return results.Entities.FirstOrDefault()?.Id;
    }

    #endregion

    #region Create Operations

    /// <summary>
    /// Creates or updates a plugin assembly (for classic DLL assemblies only).
    /// For NuGet packages, use <see cref="UpsertPackageAsync"/> instead.
    /// </summary>
    public async Task<Guid> UpsertAssemblyAsync(string name, byte[] content, string? solutionName = null)
    {
        var existing = await GetAssemblyByNameAsync(name);

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
            await UpdateAsync(entity);

            // Add to solution even on update (handles case where component exists but isn't in solution)
            if (!string.IsNullOrEmpty(solutionName))
            {
                await AddToSolutionAsync(existing.Id, ComponentTypePluginAssembly, solutionName);
            }

            return existing.Id;
        }
        else
        {
            return await CreateWithSolutionAsync(entity, solutionName);
        }
    }

    /// <summary>
    /// Creates or updates a plugin package (for NuGet packages).
    /// </summary>
    /// <param name="packageName">The package name from .nuspec (e.g., "ppds_MyPlugin"). This is what Dataverse uses as uniquename.</param>
    /// <param name="nupkgContent">The raw .nupkg file content.</param>
    /// <param name="solutionName">Solution to add the package to.</param>
    /// <returns>The package ID.</returns>
    public async Task<Guid> UpsertPackageAsync(string packageName, byte[] nupkgContent, string? solutionName = null)
    {
        // packageName comes from .nuspec <id> (parsed from .nuspec)
        // Dataverse extracts uniquename from the nupkg content, so we use packageName for lookup
        var existing = await GetPackageByNameAsync(packageName);

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
            await ExecuteAsync(request);

            return existing.Id;
        }

        // CREATE: Set name and content only - Dataverse extracts uniquename from .nuspec <id> inside nupkg
        var entity = new PluginPackage
        {
            Name = packageName,
            Content = Convert.ToBase64String(nupkgContent)
        };

        return await CreateWithSolutionHeaderAsync(entity, solutionName);
    }

    /// <summary>
    /// Gets the assembly ID for an assembly that is part of a plugin package.
    /// </summary>
    public async Task<Guid?> GetAssemblyIdForPackageAsync(Guid packageId, string assemblyName)
    {
        var assemblies = await ListAssembliesForPackageAsync(packageId);
        return assemblies.FirstOrDefault(a => a.Name == assemblyName)?.Id;
    }

    /// <summary>
    /// Creates or updates a plugin type.
    /// </summary>
    public async Task<Guid> UpsertPluginTypeAsync(Guid assemblyId, string typeName, string? solutionName = null)
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

        var results = await RetrieveMultipleAsync(query);
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

        return await CreateWithSolutionAsync(entity, solutionName);
    }

    /// <summary>
    /// Creates or updates a processing step.
    /// </summary>
    public async Task<Guid> UpsertStepAsync(
        Guid pluginTypeId,
        PluginStepConfig stepConfig,
        Guid messageId,
        Guid? filterId,
        string? solutionName = null)
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

        var results = await RetrieveMultipleAsync(query);
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
            await UpdateAsync(entity);

            // Add to solution even on update (handles case where component exists but isn't in solution)
            if (!string.IsNullOrEmpty(solutionName))
            {
                await AddToSolutionAsync(existing.Id, ComponentTypeSdkMessageProcessingStep, solutionName);
            }

            return existing.Id;
        }
        else
        {
            return await CreateWithSolutionAsync(entity, solutionName);
        }
    }

    /// <summary>
    /// Creates or updates a step image.
    /// </summary>
    public async Task<Guid> UpsertImageAsync(Guid stepId, PluginImageConfig imageConfig)
    {
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

        var results = await RetrieveMultipleAsync(query);
        var existing = results.Entities.FirstOrDefault();

        var entity = new SdkMessageProcessingStepImage
        {
            SdkMessageProcessingStepId = new EntityReference(SdkMessageProcessingStep.EntityLogicalName, stepId),
            Name = imageConfig.Name,
            EntityAlias = imageConfig.EntityAlias ?? imageConfig.Name,
            ImageType = (sdkmessageprocessingstepimage_imagetype)MapImageTypeToValue(imageConfig.ImageType),
            MessagePropertyName = "Target"
        };

        if (!string.IsNullOrEmpty(imageConfig.Attributes))
        {
            entity.Attributes1 = imageConfig.Attributes;
        }

        if (existing != null)
        {
            entity.Id = existing.Id;
            await UpdateAsync(entity);
            return existing.Id;
        }
        else
        {
            return await CreateAsync(entity);
        }
    }

    #endregion

    #region Delete Operations

    /// <summary>
    /// Deletes a step image.
    /// </summary>
    public async Task DeleteImageAsync(Guid imageId)
    {
        await DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, imageId);
    }

    /// <summary>
    /// Deletes a processing step (also deletes child images).
    /// </summary>
    public async Task DeleteStepAsync(Guid stepId)
    {
        // Delete images first
        var images = await ListImagesForStepAsync(stepId);
        foreach (var image in images)
        {
            await DeleteImageAsync(image.Id);
        }

        await DeleteAsync(SdkMessageProcessingStep.EntityLogicalName, stepId);
    }

    /// <summary>
    /// Deletes a plugin type (only if it has no steps).
    /// </summary>
    public async Task DeletePluginTypeAsync(Guid pluginTypeId)
    {
        await DeleteAsync(PluginType.EntityLogicalName, pluginTypeId);
    }

    #endregion

    #region Solution Operations

    /// <summary>
    /// Adds a component to a solution.
    /// </summary>
    public async Task AddToSolutionAsync(Guid componentId, int componentType, string solutionName)
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
            await ExecuteAsync(request);
        }
        catch (Exception ex) when (ex.Message.Contains("already exists"))
        {
            // Component already in solution, ignore
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Gets the solution component type code for an entity.
    /// Uses well-known values for system entities, queries metadata for custom entities like pluginpackage.
    /// </summary>
    private async Task<int> GetComponentTypeAsync(string entityLogicalName)
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
            var response = (RetrieveEntityResponse)await ExecuteAsync(request);
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

    private async Task<Guid> CreateWithSolutionAsync(Entity entity, string? solutionName)
    {
        var id = await CreateAsync(entity);

        if (!string.IsNullOrEmpty(solutionName))
        {
            var componentType = await GetComponentTypeAsync(entity.LogicalName);

            if (componentType > 0)
            {
                await AddToSolutionAsync(id, componentType, solutionName);
            }
        }

        return id;
    }

    /// <summary>
    /// Creates an entity with atomic solution association using CreateRequest.SolutionUniqueName.
    /// This is the SDK equivalent of the MSCRM.SolutionUniqueName HTTP header.
    /// </summary>
    private async Task<Guid> CreateWithSolutionHeaderAsync(Entity entity, string? solutionName)
    {
        var request = new CreateRequest { Target = entity };

        if (!string.IsNullOrEmpty(solutionName))
        {
            request.Parameters["SolutionUniqueName"] = solutionName;
        }

        var response = (CreateResponse)await ExecuteAsync(request);
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
    private async Task<EntityCollection> RetrieveMultipleAsync(QueryExpression query)
    {
        if (_asyncService != null)
            return await _asyncService.RetrieveMultipleAsync(query);
        return await Task.Run(() => _service.RetrieveMultiple(query));
    }

    private async Task<Guid> CreateAsync(Entity entity)
    {
        if (_asyncService != null)
            return await _asyncService.CreateAsync(entity);
        return await Task.Run(() => _service.Create(entity));
    }

    private async Task UpdateAsync(Entity entity)
    {
        if (_asyncService != null)
            await _asyncService.UpdateAsync(entity);
        else
            await Task.Run(() => _service.Update(entity));
    }

    private async Task DeleteAsync(string entityName, Guid id)
    {
        if (_asyncService != null)
            await _asyncService.DeleteAsync(entityName, id);
        else
            await Task.Run(() => _service.Delete(entityName, id));
    }

    private async Task<OrganizationResponse> ExecuteAsync(OrganizationRequest request)
    {
        if (_asyncService != null)
            return await _asyncService.ExecuteAsync(request);
        return await Task.Run(() => _service.Execute(request));
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
}

/// <summary>
/// Information about a plugin type in Dataverse.
/// </summary>
public sealed class PluginTypeInfo
{
    public Guid Id { get; set; }
    public string TypeName { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
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
}

#endregion
