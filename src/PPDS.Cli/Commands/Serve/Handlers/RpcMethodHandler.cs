using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

/// <summary>
/// Handles JSON-RPC method calls for the serve daemon.
/// Method naming follows the CLI command structure: "group/subcommand".
/// </summary>
public class RpcMethodHandler
{
    private readonly IDaemonConnectionPoolManager _poolManager;
    private JsonRpc? _rpc;

    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMethodHandler"/> class.
    /// </summary>
    /// <param name="poolManager">The connection pool manager for caching Dataverse pools.</param>
    public RpcMethodHandler(IDaemonConnectionPoolManager poolManager)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
    }

    /// <summary>
    /// Sets the JSON-RPC context for sending notifications (e.g., device code flow).
    /// Must be called exactly once after JsonRpc.Attach.
    /// </summary>
    /// <param name="rpc">The JSON-RPC connection.</param>
    /// <exception cref="InvalidOperationException">Thrown if called more than once.</exception>
    public void SetRpcContext(JsonRpc rpc)
    {
        ArgumentNullException.ThrowIfNull(rpc);
        if (Interlocked.CompareExchange(ref _rpc, rpc, null) != null)
        {
            throw new InvalidOperationException("RPC context has already been set.");
        }
    }

    #region Auth Methods

    /// <summary>
    /// Lists all authentication profiles.
    /// Maps to: ppds auth list --json
    /// </summary>
    [JsonRpcMethod("auth/list")]
    public async Task<AuthListResponse> AuthListAsync(CancellationToken cancellationToken)
    {
        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profiles = collection.All.Select(p => new ProfileInfo
        {
            Index = p.Index,
            Name = p.Name,
            Identity = p.IdentityDisplay,
            AuthMethod = p.AuthMethod.ToString(),
            Cloud = p.Cloud.ToString(),
            Environment = p.Environment != null ? new EnvironmentSummary
            {
                Url = p.Environment.Url,
                DisplayName = p.Environment.DisplayName
            } : null,
            IsActive = collection.ActiveProfile?.Index == p.Index,
            CreatedAt = p.CreatedAt,
            LastUsedAt = p.LastUsedAt
        }).ToList();

        return new AuthListResponse
        {
            ActiveProfile = collection.ActiveProfile?.Name,
            ActiveProfileIndex = collection.ActiveProfileIndex,
            Profiles = profiles
        };
    }

    /// <summary>
    /// Gets the current active profile.
    /// Maps to: ppds auth who --json
    /// </summary>
    [JsonRpcMethod("auth/who")]
    public async Task<AuthWhoResponse> AuthWhoAsync(CancellationToken cancellationToken)
    {
        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        return new AuthWhoResponse
        {
            Index = profile.Index,
            Name = profile.Name,
            AuthMethod = profile.AuthMethod.ToString(),
            Cloud = profile.Cloud.ToString(),
            TenantId = profile.TenantId,
            Username = profile.Username,
            ObjectId = profile.ObjectId,
            ApplicationId = profile.ApplicationId,
            TokenExpiresOn = profile.TokenExpiresOn,
            TokenStatus = profile.TokenExpiresOn.HasValue
                ? (profile.TokenExpiresOn.Value < DateTimeOffset.UtcNow ? "expired" : "valid")
                : null,
            Environment = profile.Environment != null ? new EnvironmentDetails
            {
                Url = profile.Environment.Url,
                DisplayName = profile.Environment.DisplayName,
                UniqueName = profile.Environment.UniqueName,
                EnvironmentId = profile.Environment.EnvironmentId,
                OrganizationId = profile.Environment.OrganizationId,
                Type = profile.Environment.Type,
                Region = profile.Environment.Region
            } : null,
            CreatedAt = profile.CreatedAt,
            LastUsedAt = profile.LastUsedAt
        };
    }

    /// <summary>
    /// Selects an authentication profile as active.
    /// Maps to: ppds auth select --index N or ppds auth select --name "name"
    /// </summary>
    [JsonRpcMethod("auth/select")]
    public async Task<AuthSelectResponse> AuthSelectAsync(
        int? index = null,
        string? name = null,
        CancellationToken cancellationToken = default)
    {
        if (index == null && string.IsNullOrWhiteSpace(name))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "Either 'index' or 'name' parameter is required");
        }

        if (index != null && !string.IsNullOrWhiteSpace(name))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                "Provide either 'index' or 'name', not both");
        }

        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        AuthProfile? profile;
        if (index != null)
        {
            profile = collection.GetByIndex(index.Value);
            if (profile == null)
            {
                throw new RpcException(
                    ErrorCodes.Auth.ProfileNotFound,
                    $"Profile with index {index} not found");
            }
        }
        else
        {
            profile = collection.GetByName(name!);
            if (profile == null)
            {
                throw new RpcException(
                    ErrorCodes.Auth.ProfileNotFound,
                    $"Profile '{name}' not found");
            }
        }

        collection.SetActiveByIndex(profile.Index);
        await store.SaveAsync(collection, cancellationToken);

        return new AuthSelectResponse
        {
            Index = profile.Index,
            Name = profile.Name,
            Identity = profile.IdentityDisplay,
            Environment = profile.Environment?.DisplayName
        };
    }

    #endregion

    #region Environment Methods

    /// <summary>
    /// Lists available environments.
    /// Maps to: ppds env list --json
    /// </summary>
    [JsonRpcMethod("env/list")]
    public async Task<EnvListResponse> EnvListAsync(
        string? filter = null,
        CancellationToken cancellationToken = default)
    {
        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        using var gds = GlobalDiscoveryService.FromProfile(profile);
        var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);

        // Apply filter if provided
        IReadOnlyList<DiscoveredEnvironment> filtered = environments;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            filtered = environments.Where(e =>
                e.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.UniqueName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                e.ApiUrl.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (e.EnvironmentId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
            ).ToList();
        }

        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();

        return new EnvListResponse
        {
            Filter = filter,
            Environments = filtered.Select(e => new EnvironmentInfo
            {
                Id = e.Id,
                EnvironmentId = e.EnvironmentId,
                FriendlyName = e.FriendlyName,
                UniqueName = e.UniqueName,
                ApiUrl = e.ApiUrl,
                Url = e.Url,
                Type = e.EnvironmentType,
                State = e.IsEnabled ? "Enabled" : "Disabled",
                Region = e.Region,
                Version = e.Version,
                IsActive = selectedUrl != null &&
                    e.ApiUrl.TrimEnd('/').ToLowerInvariant() == selectedUrl
            }).ToList()
        };
    }

    /// <summary>
    /// Selects an environment for the active profile.
    /// Maps to: ppds env select --environment "env"
    /// </summary>
    [JsonRpcMethod("env/select")]
    public async Task<EnvSelectResponse> EnvSelectAsync(
        string environment,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environment))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'environment' parameter is required");
        }

        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        // Use multi-layer resolution
        using var credentialStore = new SecureCredentialStore();
        using var resolver = new EnvironmentResolutionService(profile, credentialStore: credentialStore);
        var result = await resolver.ResolveAsync(environment, cancellationToken);

        if (!result.Success)
        {
            throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                result.ErrorMessage ?? $"Environment '{environment}' not found");
        }

        var resolved = result.Environment!;
        profile.Environment = resolved;
        await store.SaveAsync(collection, cancellationToken);

        return new EnvSelectResponse
        {
            Url = resolved.Url,
            DisplayName = resolved.DisplayName,
            UniqueName = resolved.UniqueName,
            EnvironmentId = resolved.EnvironmentId,
            ResolutionMethod = result.Method.ToString()
        };
    }

    #endregion

    #region Plugins Methods

    /// <summary>
    /// Lists registered plugins in the environment.
    /// Maps to: ppds plugins list --json
    /// </summary>
    [JsonRpcMethod("plugins/list")]
    public async Task<PluginsListResponse> PluginsListAsync(
        string? assembly = null,
        string? package = null,
        CancellationToken cancellationToken = default)
    {
        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        if (profile.Environment == null)
        {
            throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");
        }

        var pool = await _poolManager.GetOrCreatePoolAsync(
            new[] { profile.Name ?? profile.DisplayIdentifier },
            profile.Environment.Url,
            deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc),
            cancellationToken: cancellationToken);

        // Create registration service with pool (use NullLogger for daemon context)
        var registrationService = new PluginRegistrationService(
            pool,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginRegistrationService>.Instance);

        var response = new PluginsListResponse();

        // Get assemblies (unless package filter is specified)
        if (string.IsNullOrEmpty(package))
        {
            var assemblies = await registrationService.ListAssembliesAsync(assembly, cancellationToken);

            foreach (var asm in assemblies)
            {
                var assemblyOutput = new PluginAssemblyInfo
                {
                    Name = asm.Name,
                    Version = asm.Version,
                    PublicKeyToken = asm.PublicKeyToken,
                    Types = []
                };

                var types = await registrationService.ListTypesForAssemblyAsync(asm.Id, cancellationToken);
                await PopulatePluginTypesAsync(registrationService, types, assemblyOutput.Types, cancellationToken);

                response.Assemblies.Add(assemblyOutput);
            }
        }

        // Get packages (unless assembly filter is specified)
        if (string.IsNullOrEmpty(assembly))
        {
            var packages = await registrationService.ListPackagesAsync(package, cancellationToken);

            foreach (var pkg in packages)
            {
                var packageOutput = new PluginPackageInfo
                {
                    Name = pkg.Name,
                    UniqueName = pkg.UniqueName,
                    Version = pkg.Version,
                    Assemblies = []
                };

                var assemblies = await registrationService.ListAssembliesForPackageAsync(pkg.Id, cancellationToken);
                foreach (var asm in assemblies)
                {
                    var assemblyOutput = new PluginAssemblyInfo
                    {
                        Name = asm.Name,
                        Version = asm.Version,
                        PublicKeyToken = asm.PublicKeyToken,
                        Types = []
                    };

                    var types = await registrationService.ListTypesForAssemblyAsync(asm.Id, cancellationToken);
                    await PopulatePluginTypesAsync(registrationService, types, assemblyOutput.Types, cancellationToken);

                    packageOutput.Assemblies.Add(assemblyOutput);
                }

                response.Packages.Add(packageOutput);
            }
        }

        return response;
    }

    private static async Task PopulatePluginTypesAsync(
        PluginRegistrationService registrationService,
        List<PluginTypeInfoModel> types,
        List<PluginTypeInfoDto> typeOutputs,
        CancellationToken cancellationToken)
    {
        if (types.Count == 0)
            return;

        // Fetch all steps in parallel - each call gets its own client from the pool
        var stepTasks = types.Select(t => registrationService.ListStepsForTypeAsync(t.Id, cancellationToken));
        var stepsPerType = await Task.WhenAll(stepTasks);

        // Collect all steps for image fetching
        var allSteps = stepsPerType.SelectMany(s => s).ToList();

        // Fetch all images in parallel if there are steps
        Dictionary<Guid, List<PluginImageInfoModel>> imagesByStepId = [];
        if (allSteps.Count > 0)
        {
            var imageTasks = allSteps.Select(s => registrationService.ListImagesForStepAsync(s.Id, cancellationToken));
            var imagesPerStep = await Task.WhenAll(imageTasks);
            imagesByStepId = allSteps
                .Select((step, idx) => (step.Id, images: imagesPerStep[idx]))
                .ToDictionary(t => t.Id, t => t.images);
        }

        // Build DTOs
        for (var i = 0; i < types.Count; i++)
        {
            var type = types[i];
            var stepsForType = stepsPerType[i];

            var typeOutput = new PluginTypeInfoDto
            {
                TypeName = type.TypeName,
                Steps = stepsForType.Select(step => new PluginStepInfo
                {
                    Name = step.Name,
                    Message = step.Message,
                    Entity = step.PrimaryEntity,
                    Stage = step.Stage,
                    Mode = step.Mode,
                    ExecutionOrder = step.ExecutionOrder,
                    FilteringAttributes = step.FilteringAttributes,
                    IsEnabled = step.IsEnabled,
                    Description = step.Description,
                    Deployment = step.Deployment,
                    RunAsUser = step.ImpersonatingUserName,
                    AsyncAutoDelete = step.AsyncAutoDelete,
                    Images = imagesByStepId.TryGetValue(step.Id, out var images)
                        ? images.Select(img => new PluginImageInfo
                        {
                            Name = img.Name,
                            EntityAlias = img.EntityAlias ?? img.Name,
                            ImageType = img.ImageType,
                            Attributes = img.Attributes
                        }).ToList()
                        : []
                }).ToList()
            };

            typeOutputs.Add(typeOutput);
        }
    }

    #endregion

    #region Schema Methods

    /// <summary>
    /// Lists entity schema (fields/attributes).
    /// Maps to: ppds data schema --entity "entityname" --json
    /// </summary>
    [JsonRpcMethod("schema/list")]
    public async Task<SchemaListResponse> SchemaListAsync(
        string entity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'entity' parameter is required");
        }

        // TODO: Implement schema retrieval
        // This will be fully implemented when #51 (metadata commands) is merged
        // For now, return a placeholder indicating the method is recognized
        throw new RpcException(
            ErrorCodes.Operation.NotSupported,
            "Schema retrieval will be available after metadata commands (#51) are implemented");
    }

    #endregion

    #region Query Methods

    /// <summary>
    /// Executes a FetchXML query against Dataverse.
    /// Maps to: ppds query fetch --json
    /// </summary>
    [JsonRpcMethod("query/fetch")]
    public async Task<QueryResultResponse> QueryFetchAsync(
        string fetchXml,
        int? top = null,
        int? page = null,
        string? pagingCookie = null,
        bool count = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fetchXml))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'fetchXml' parameter is required");
        }

        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        if (profile.Environment == null)
        {
            throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");
        }

        // Inject top attribute if specified
        var query = fetchXml;
        if (top.HasValue)
        {
            query = InjectTopAttribute(query, top.Value);
        }

        await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
            profile.Name,
            profile.Environment.Url,
            deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
            cancellationToken: cancellationToken);

        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();
        var result = await queryExecutor.ExecuteFetchXmlAsync(
            query,
            page,
            pagingCookie,
            count,
            cancellationToken);

        return MapToResponse(result, query);
    }

    /// <summary>
    /// Executes a SQL query against Dataverse by transpiling to FetchXML.
    /// Maps to: ppds query sql --json
    /// </summary>
    [JsonRpcMethod("query/sql")]
    public async Task<QueryResultResponse> QuerySqlAsync(
        string sql,
        int? top = null,
        int? page = null,
        string? pagingCookie = null,
        bool count = false,
        bool showFetchXml = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        // Parse and transpile SQL to FetchXML
        SqlSelectStatement ast;
        try
        {
            var parser = new SqlParser(sql);
            ast = parser.Parse();
        }
        catch (SqlParseException ex)
        {
            throw new RpcException(ErrorCodes.Query.ParseError, ex);
        }

        // Override top if specified
        if (top.HasValue)
        {
            ast = ast.WithTop(top.Value);
        }

        var transpiler = new SqlToFetchXmlTranspiler();
        var fetchXml = transpiler.Transpile(ast);

        // If showFetchXml is true, just return the transpiled FetchXML
        if (showFetchXml)
        {
            return new QueryResultResponse
            {
                Success = true,
                ExecutedFetchXml = fetchXml
            };
        }

        using var store = new ProfileStore();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        if (profile.Environment == null)
        {
            throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");
        }

        await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
            profile.Name,
            profile.Environment.Url,
            deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
            cancellationToken: cancellationToken);

        var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();
        var result = await queryExecutor.ExecuteFetchXmlAsync(
            fetchXml,
            page,
            pagingCookie,
            count,
            cancellationToken);

        return MapToResponse(result, fetchXml);
    }

    private static string InjectTopAttribute(string fetchXml, int top)
    {
        var fetchIndex = fetchXml.IndexOf("<fetch", StringComparison.OrdinalIgnoreCase);
        if (fetchIndex < 0) return fetchXml;

        var endOfFetch = fetchXml.IndexOf('>', fetchIndex);
        if (endOfFetch < 0) return fetchXml;

        var fetchElement = fetchXml.Substring(fetchIndex, endOfFetch - fetchIndex);

        if (fetchElement.Contains("top=", StringComparison.OrdinalIgnoreCase))
        {
            return fetchXml; // Already has top, don't override
        }

        var insertPoint = fetchIndex + "<fetch".Length;
        return fetchXml.Substring(0, insertPoint) + $" top=\"{top}\"" + fetchXml.Substring(insertPoint);
    }

    private static QueryResultResponse MapToResponse(QueryResult result, string fetchXml)
    {
        return new QueryResultResponse
        {
            Success = true,
            EntityName = result.EntityLogicalName,
            Columns = result.Columns.Select(c => new QueryColumnInfo
            {
                LogicalName = c.LogicalName,
                Alias = c.Alias,
                DisplayName = c.DisplayName,
                DataType = c.DataType.ToString(),
                LinkedEntityAlias = c.LinkedEntityAlias
            }).ToList(),
            Records = result.Records.Select(r =>
                r.ToDictionary(
                    kvp => kvp.Key,
                    kvp => MapQueryValue(kvp.Value))).ToList(),
            Count = result.Count,
            TotalCount = result.TotalCount,
            MoreRecords = result.MoreRecords,
            PagingCookie = result.PagingCookie,
            PageNumber = result.PageNumber,
            IsAggregate = result.IsAggregate,
            ExecutedFetchXml = fetchXml,
            ExecutionTimeMs = result.ExecutionTimeMs
        };
    }

    private static object? MapQueryValue(QueryValue? value)
    {
        if (value == null) return null;

        // For lookups, return structured object
        if (value.LookupEntityId.HasValue)
        {
            return new Dictionary<string, object?>
            {
                ["value"] = value.Value,
                ["formatted"] = value.FormattedValue,
                ["entityType"] = value.LookupEntityType,
                ["entityId"] = value.LookupEntityId
            };
        }

        // For values with formatting, return structured object
        if (value.FormattedValue != null)
        {
            return new Dictionary<string, object?>
            {
                ["value"] = value.Value,
                ["formatted"] = value.FormattedValue
            };
        }

        // Simple value
        return value.Value;
    }

    #endregion

    #region Profile Invalidation

    /// <summary>
    /// Invalidates cached pools that use the specified profile.
    /// Called by VS Code extension after auth profile changes.
    /// </summary>
    [JsonRpcMethod("profiles/invalidate")]
    public ProfilesInvalidateResponse ProfilesInvalidate(string profileName)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'profileName' parameter is required");
        }

        _poolManager.InvalidateProfile(profileName);

        return new ProfilesInvalidateResponse
        {
            ProfileName = profileName,
            Invalidated = true
        };
    }

    #endregion
}

#region Response DTOs

/// <summary>
/// Response for auth/list method.
/// </summary>
public class AuthListResponse
{
    [JsonPropertyName("activeProfile")]
    public string? ActiveProfile { get; set; }

    [JsonPropertyName("activeProfileIndex")]
    public int? ActiveProfileIndex { get; set; }

    [JsonPropertyName("profiles")]
    public List<ProfileInfo> Profiles { get; set; } = [];
}

/// <summary>
/// Profile information summary.
/// </summary>
public class ProfileInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("identity")]
    public string Identity { get; set; } = "";

    [JsonPropertyName("authMethod")]
    public string AuthMethod { get; set; } = "";

    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = "";

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentSummary? Environment { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Brief environment summary for profile listings.
/// </summary>
public class EnvironmentSummary
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";
}

/// <summary>
/// Response for auth/who method.
/// </summary>
public class AuthWhoResponse
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("authMethod")]
    public string AuthMethod { get; set; } = "";

    [JsonPropertyName("cloud")]
    public string Cloud { get; set; } = "";

    [JsonPropertyName("tenantId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TenantId { get; set; }

    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    [JsonPropertyName("objectId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObjectId { get; set; }

    [JsonPropertyName("applicationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApplicationId { get; set; }

    [JsonPropertyName("tokenExpiresOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? TokenExpiresOn { get; set; }

    [JsonPropertyName("tokenStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenStatus { get; set; }

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EnvironmentDetails? Environment { get; set; }

    [JsonPropertyName("createdAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("lastUsedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>
/// Detailed environment information.
/// </summary>
public class EnvironmentDetails
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("organizationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationId { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }
}

/// <summary>
/// Response for auth/select method.
/// </summary>
public class AuthSelectResponse
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("identity")]
    public string Identity { get; set; } = "";

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Environment { get; set; }
}

/// <summary>
/// Response for env/list method.
/// </summary>
public class EnvListResponse
{
    [JsonPropertyName("filter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filter { get; set; }

    [JsonPropertyName("environments")]
    public List<EnvironmentInfo> Environments { get; set; } = [];
}

/// <summary>
/// Environment information from discovery.
/// </summary>
public class EnvironmentInfo
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("apiUrl")]
    public string ApiUrl { get; set; } = "";

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("region")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Region { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}

/// <summary>
/// Response for env/select method.
/// </summary>
public class EnvSelectResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }

    [JsonPropertyName("resolutionMethod")]
    public string ResolutionMethod { get; set; } = "";
}

/// <summary>
/// Response for schema/list method.
/// </summary>
public class SchemaListResponse
{
    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "";

    [JsonPropertyName("attributes")]
    public List<AttributeInfo> Attributes { get; set; } = [];
}

/// <summary>
/// Attribute/field information.
/// </summary>
public class AttributeInfo
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("attributeType")]
    public string AttributeType { get; set; } = "";

    [JsonPropertyName("isCustomAttribute")]
    public bool IsCustomAttribute { get; set; }

    [JsonPropertyName("isPrimaryId")]
    public bool IsPrimaryId { get; set; }

    [JsonPropertyName("isPrimaryName")]
    public bool IsPrimaryName { get; set; }
}

/// <summary>
/// Response for plugins/list method.
/// </summary>
public class PluginsListResponse
{
    [JsonPropertyName("assemblies")]
    public List<PluginAssemblyInfo> Assemblies { get; set; } = [];

    [JsonPropertyName("packages")]
    public List<PluginPackageInfo> Packages { get; set; } = [];
}

/// <summary>
/// Plugin package information.
/// </summary>
public class PluginPackageInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UniqueName { get; set; }

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("assemblies")]
    public List<PluginAssemblyInfo> Assemblies { get; set; } = [];
}

/// <summary>
/// Plugin assembly information.
/// </summary>
public class PluginAssemblyInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    [JsonPropertyName("publicKeyToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublicKeyToken { get; set; }

    [JsonPropertyName("types")]
    public List<PluginTypeInfoDto> Types { get; set; } = [];
}

/// <summary>
/// Plugin type information.
/// </summary>
public class PluginTypeInfoDto
{
    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("steps")]
    public List<PluginStepInfo> Steps { get; set; } = [];
}

/// <summary>
/// Plugin step information.
/// </summary>
public class PluginStepInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("entity")]
    public string Entity { get; set; } = "";

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = "";

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("executionOrder")]
    public int ExecutionOrder { get; set; }

    [JsonPropertyName("filteringAttributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilteringAttributes { get; set; }

    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("deployment")]
    public string Deployment { get; set; } = "ServerOnly";

    [JsonPropertyName("runAsUser")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunAsUser { get; set; }

    [JsonPropertyName("asyncAutoDelete")]
    public bool AsyncAutoDelete { get; set; }

    [JsonPropertyName("images")]
    public List<PluginImageInfo> Images { get; set; } = [];
}

/// <summary>
/// Plugin image information.
/// </summary>
public class PluginImageInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("entityAlias")]
    public string EntityAlias { get; set; } = "";

    [JsonPropertyName("imageType")]
    public string ImageType { get; set; } = "";

    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attributes { get; set; }
}

/// <summary>
/// Response for query/fetch and query/sql methods.
/// </summary>
public class QueryResultResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("entityName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityName { get; set; }

    [JsonPropertyName("columns")]
    public List<QueryColumnInfo> Columns { get; set; } = [];

    [JsonPropertyName("records")]
    public List<Dictionary<string, object?>> Records { get; set; } = [];

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("totalCount")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TotalCount { get; set; }

    [JsonPropertyName("moreRecords")]
    public bool MoreRecords { get; set; }

    [JsonPropertyName("pagingCookie")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PagingCookie { get; set; }

    [JsonPropertyName("pageNumber")]
    public int PageNumber { get; set; }

    [JsonPropertyName("isAggregate")]
    public bool IsAggregate { get; set; }

    [JsonPropertyName("executedFetchXml")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutedFetchXml { get; set; }

    [JsonPropertyName("executionTimeMs")]
    public long ExecutionTimeMs { get; set; }
}

/// <summary>
/// Column information in query results.
/// </summary>
public class QueryColumnInfo
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("alias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Alias { get; set; }

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("dataType")]
    public string DataType { get; set; } = "";

    [JsonPropertyName("linkedEntityAlias")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LinkedEntityAlias { get; set; }
}

/// <summary>
/// Response for profiles/invalidate method.
/// </summary>
public class ProfilesInvalidateResponse
{
    /// <summary>
    /// Gets or sets the profile name that was invalidated.
    /// </summary>
    [JsonPropertyName("profileName")]
    public string ProfileName { get; set; } = "";

    /// <summary>
    /// Gets or sets a value indicating whether invalidation was successful.
    /// </summary>
    [JsonPropertyName("invalidated")]
    public bool Invalidated { get; set; }
}

#endregion
