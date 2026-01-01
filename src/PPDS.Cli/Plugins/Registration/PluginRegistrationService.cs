using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Plugins.Registration;

/// <summary>
/// Service for managing plugin registrations in Dataverse.
/// </summary>
public sealed class PluginRegistrationService
{
    private readonly IOrganizationService _service;
    private readonly IOrganizationServiceAsync2? _asyncService;

    // Cache for entity type codes (ETCs) - some like pluginpackage vary by environment
    private readonly Dictionary<string, int> _entityTypeCodeCache = new();

    // Well-known component type codes that are consistent across all environments
    private static readonly Dictionary<string, int> WellKnownComponentTypes = new()
    {
        ["pluginassembly"] = 91,
        ["sdkmessageprocessingstep"] = 92
    };

    public PluginRegistrationService(IOrganizationService service)
    {
        _service = service;
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
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("name", "version", "publickeytoken", "culture", "isolationmode", "sourcetype"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    // Exclude system assemblies
                    new ConditionExpression("ishidden", ConditionOperator.Equal, false)
                }
            },
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        if (!string.IsNullOrEmpty(assemblyNameFilter))
        {
            query.Criteria.AddCondition("name", ConditionOperator.Equal, assemblyNameFilter);
        }

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginAssemblyInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>("name") ?? string.Empty,
            Version = e.GetAttributeValue<string>("version"),
            PublicKeyToken = e.GetAttributeValue<string>("publickeytoken"),
            IsolationMode = e.GetAttributeValue<OptionSetValue>("isolationmode")?.Value ?? 2
        }).ToList();
    }

    /// <summary>
    /// Lists all plugin packages in the environment.
    /// </summary>
    /// <param name="packageNameFilter">Optional filter by package name or unique name.</param>
    public async Task<List<PluginPackageInfo>> ListPackagesAsync(string? packageNameFilter = null)
    {
        var query = new QueryExpression("pluginpackage")
        {
            ColumnSet = new ColumnSet("name", "uniquename", "version"),
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        if (!string.IsNullOrEmpty(packageNameFilter))
        {
            // Filter by name or uniquename
            query.Criteria = new FilterExpression(LogicalOperator.Or)
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, packageNameFilter),
                    new ConditionExpression("uniquename", ConditionOperator.Equal, packageNameFilter)
                }
            };
        }

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginPackageInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>("name") ?? string.Empty,
            UniqueName = e.GetAttributeValue<string>("uniquename"),
            Version = e.GetAttributeValue<string>("version")
        }).ToList();
    }

    /// <summary>
    /// Lists all assemblies contained in a plugin package.
    /// </summary>
    public async Task<List<PluginAssemblyInfo>> ListAssembliesForPackageAsync(Guid packageId)
    {
        var query = new QueryExpression("pluginassembly")
        {
            ColumnSet = new ColumnSet("name", "version", "publickeytoken", "culture", "isolationmode", "sourcetype"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("packageid", ConditionOperator.Equal, packageId)
                }
            },
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginAssemblyInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>("name") ?? string.Empty,
            Version = e.GetAttributeValue<string>("version"),
            PublicKeyToken = e.GetAttributeValue<string>("publickeytoken"),
            IsolationMode = e.GetAttributeValue<OptionSetValue>("isolationmode")?.Value ?? 2
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
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("typename", "friendlyname", "name"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId)
                }
            },
            Orders = { new OrderExpression("typename", OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginTypeInfo
        {
            Id = e.Id,
            TypeName = e.GetAttributeValue<string>("typename") ?? string.Empty,
            FriendlyName = e.GetAttributeValue<string>("friendlyname")
        }).ToList();
    }

    /// <summary>
    /// Lists all processing steps for a plugin type.
    /// </summary>
    public async Task<List<PluginStepInfo>> ListStepsForTypeAsync(Guid pluginTypeId)
    {
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet(
                "name", "stage", "mode", "rank", "filteringattributes",
                "configuration", "statecode", "description", "supporteddeployment",
                "impersonatinguserid", "asyncautodelete"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("plugintypeid", ConditionOperator.Equal, pluginTypeId)
                }
            },
            LinkEntities =
            {
                new LinkEntity("sdkmessageprocessingstep", "sdkmessage", "sdkmessageid", "sdkmessageid", JoinOperator.Inner)
                {
                    Columns = new ColumnSet("name"),
                    EntityAlias = "message"
                },
                new LinkEntity("sdkmessageprocessingstep", "sdkmessagefilter", "sdkmessagefilterid", "sdkmessagefilterid", JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet("primaryobjecttypecode", "secondaryobjecttypecode"),
                    EntityAlias = "filter"
                },
                new LinkEntity("sdkmessageprocessingstep", "systemuser", "impersonatinguserid", "systemuserid", JoinOperator.LeftOuter)
                {
                    Columns = new ColumnSet("fullname", "domainname"),
                    EntityAlias = "impersonatinguser"
                }
            },
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e =>
        {
            var impersonatingUserRef = e.GetAttributeValue<EntityReference>("impersonatinguserid");
            var impersonatingUserName = e.GetAttributeValue<AliasedValue>("impersonatinguser.fullname")?.Value?.ToString()
                ?? e.GetAttributeValue<AliasedValue>("impersonatinguser.domainname")?.Value?.ToString();

            return new PluginStepInfo
            {
                Id = e.Id,
                Name = e.GetAttributeValue<string>("name") ?? string.Empty,
                Message = e.GetAttributeValue<AliasedValue>("message.name")?.Value?.ToString() ?? string.Empty,
                PrimaryEntity = e.GetAttributeValue<AliasedValue>("filter.primaryobjecttypecode")?.Value?.ToString() ?? "none",
                SecondaryEntity = e.GetAttributeValue<AliasedValue>("filter.secondaryobjecttypecode")?.Value?.ToString(),
                Stage = MapStageFromValue(e.GetAttributeValue<OptionSetValue>("stage")?.Value ?? 40),
                Mode = MapModeFromValue(e.GetAttributeValue<OptionSetValue>("mode")?.Value ?? 0),
                ExecutionOrder = e.GetAttributeValue<int>("rank"),
                FilteringAttributes = e.GetAttributeValue<string>("filteringattributes"),
                Configuration = e.GetAttributeValue<string>("configuration"),
                IsEnabled = e.GetAttributeValue<OptionSetValue>("statecode")?.Value == 0,
                Description = e.GetAttributeValue<string>("description"),
                Deployment = MapDeploymentFromValue(e.GetAttributeValue<OptionSetValue>("supporteddeployment")?.Value ?? 0),
                ImpersonatingUserId = impersonatingUserRef?.Id,
                ImpersonatingUserName = impersonatingUserName,
                AsyncAutoDelete = e.GetAttributeValue<bool?>("asyncautodelete") ?? false
            };
        }).ToList();
    }

    /// <summary>
    /// Lists all images for a processing step.
    /// </summary>
    public async Task<List<PluginImageInfo>> ListImagesForStepAsync(Guid stepId)
    {
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("name", "entityalias", "imagetype", "attributes"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId)
                }
            },
            Orders = { new OrderExpression("name", OrderType.Ascending) }
        };

        var results = await RetrieveMultipleAsync(query);

        return results.Entities.Select(e => new PluginImageInfo
        {
            Id = e.Id,
            Name = e.GetAttributeValue<string>("name") ?? string.Empty,
            EntityAlias = e.GetAttributeValue<string>("entityalias"),
            ImageType = MapImageTypeFromValue(e.GetAttributeValue<OptionSetValue>("imagetype")?.Value ?? 0),
            Attributes = e.GetAttributeValue<string>("attributes")
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
        var query = new QueryExpression("sdkmessage")
        {
            ColumnSet = new ColumnSet("sdkmessageid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("name", ConditionOperator.Equal, messageName)
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
        var query = new QueryExpression("sdkmessagefilter")
        {
            ColumnSet = new ColumnSet("sdkmessagefilterid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageid", ConditionOperator.Equal, messageId),
                    new ConditionExpression("primaryobjecttypecode", ConditionOperator.Equal, primaryEntity)
                }
            }
        };

        if (!string.IsNullOrEmpty(secondaryEntity))
        {
            query.Criteria.AddCondition("secondaryobjecttypecode", ConditionOperator.Equal, secondaryEntity);
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

        var entity = new Entity("pluginassembly")
        {
            ["name"] = name,
            ["content"] = Convert.ToBase64String(content),
            ["isolationmode"] = new OptionSetValue(2), // Sandbox
            ["sourcetype"] = new OptionSetValue(0) // Database
        };

        if (existing != null)
        {
            entity.Id = existing.Id;
            await UpdateAsync(entity);

            // Add to solution even on update (handles case where component exists but isn't in solution)
            if (!string.IsNullOrEmpty(solutionName))
            {
                await AddToSolutionAsync(existing.Id, 91, solutionName); // 91 = Plugin Assembly
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
            var updateEntity = new Entity("pluginpackage", existing.Id)
            {
                ["content"] = Convert.ToBase64String(nupkgContent)
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
        var entity = new Entity("pluginpackage")
        {
            ["name"] = packageName,
            ["content"] = Convert.ToBase64String(nupkgContent)
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
        var query = new QueryExpression("plugintype")
        {
            ColumnSet = new ColumnSet("plugintypeid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("pluginassemblyid", ConditionOperator.Equal, assemblyId),
                    new ConditionExpression("typename", ConditionOperator.Equal, typeName)
                }
            }
        };

        var results = await RetrieveMultipleAsync(query);
        var existing = results.Entities.FirstOrDefault();

        if (existing != null)
        {
            return existing.Id;
        }

        var entity = new Entity("plugintype")
        {
            ["pluginassemblyid"] = new EntityReference("pluginassembly", assemblyId),
            ["typename"] = typeName,
            ["friendlyname"] = typeName,
            ["name"] = typeName
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
        var query = new QueryExpression("sdkmessageprocessingstep")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("plugintypeid", ConditionOperator.Equal, pluginTypeId),
                    new ConditionExpression("name", ConditionOperator.Equal, stepConfig.Name)
                }
            }
        };

        var results = await RetrieveMultipleAsync(query);
        var existing = results.Entities.FirstOrDefault();

        var entity = new Entity("sdkmessageprocessingstep")
        {
            ["name"] = stepConfig.Name,
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId),
            ["stage"] = new OptionSetValue(MapStageToValue(stepConfig.Stage)),
            ["mode"] = new OptionSetValue(MapModeToValue(stepConfig.Mode)),
            ["rank"] = stepConfig.ExecutionOrder,
            ["supporteddeployment"] = new OptionSetValue(MapDeploymentToValue(stepConfig.Deployment)),
            ["invocationsource"] = new OptionSetValue(0) // Internal (legacy, but required)
        };

        if (filterId.HasValue)
        {
            entity["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId.Value);
        }

        if (!string.IsNullOrEmpty(stepConfig.FilteringAttributes))
        {
            entity["filteringattributes"] = stepConfig.FilteringAttributes;
        }

        if (!string.IsNullOrEmpty(stepConfig.UnsecureConfiguration))
        {
            entity["configuration"] = stepConfig.UnsecureConfiguration;
        }

        if (!string.IsNullOrEmpty(stepConfig.Description))
        {
            entity["description"] = stepConfig.Description;
        }

        // Handle impersonating user (Run in User's Context)
        if (!string.IsNullOrEmpty(stepConfig.RunAsUser) &&
            !stepConfig.RunAsUser.Equals("CallingUser", StringComparison.OrdinalIgnoreCase))
        {
            if (Guid.TryParse(stepConfig.RunAsUser, out var userId))
            {
                entity["impersonatinguserid"] = new EntityReference("systemuser", userId);
            }
        }

        // Async auto-delete (only applies to async steps)
        if (stepConfig.AsyncAutoDelete == true && stepConfig.Mode == "Asynchronous")
        {
            entity["asyncautodelete"] = true;
        }

        if (existing != null)
        {
            entity.Id = existing.Id;
            await UpdateAsync(entity);

            // Add to solution even on update (handles case where component exists but isn't in solution)
            if (!string.IsNullOrEmpty(solutionName))
            {
                await AddToSolutionAsync(existing.Id, 92, solutionName); // 92 = SDK Message Processing Step
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
        var query = new QueryExpression("sdkmessageprocessingstepimage")
        {
            ColumnSet = new ColumnSet("sdkmessageprocessingstepimageid"),
            Criteria = new FilterExpression
            {
                Conditions =
                {
                    new ConditionExpression("sdkmessageprocessingstepid", ConditionOperator.Equal, stepId),
                    new ConditionExpression("name", ConditionOperator.Equal, imageConfig.Name)
                }
            }
        };

        var results = await RetrieveMultipleAsync(query);
        var existing = results.Entities.FirstOrDefault();

        var entity = new Entity("sdkmessageprocessingstepimage")
        {
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["name"] = imageConfig.Name,
            ["entityalias"] = imageConfig.EntityAlias ?? imageConfig.Name,
            ["imagetype"] = new OptionSetValue(MapImageTypeToValue(imageConfig.ImageType)),
            ["messagepropertyname"] = "Target"
        };

        if (!string.IsNullOrEmpty(imageConfig.Attributes))
        {
            entity["attributes"] = imageConfig.Attributes;
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
        await DeleteAsync("sdkmessageprocessingstepimage", imageId);
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

        await DeleteAsync("sdkmessageprocessingstep", stepId);
    }

    /// <summary>
    /// Deletes a plugin type (only if it has no steps).
    /// </summary>
    public async Task DeletePluginTypeAsync(Guid pluginTypeId)
    {
        await DeleteAsync("plugintype", pluginTypeId);
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
        catch
        {
            // Entity doesn't exist or can't be queried - return 0 to skip solution addition
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
        10 => "PreValidation",
        20 => "PreOperation",
        30 => "MainOperation",
        40 => "PostOperation",
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
        "PreValidation" => 10,
        "PreOperation" => 20,
        "MainOperation" => 30,
        "PostOperation" => 40,
        _ => int.TryParse(stage, out var v) ? v : 40
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
