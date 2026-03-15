using System.Text.Json.Serialization;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Discovery;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Profile;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Security;
using PPDS.Dataverse.Services;
using PPDS.Dataverse.Sql.Intellisense;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;

namespace PPDS.Cli.Commands.Serve.Handlers;

/// <summary>
/// Handles JSON-RPC method calls for the serve daemon.
/// Method naming follows the CLI command structure: "group/subcommand".
/// </summary>
public class RpcMethodHandler : IDisposable
{
    private readonly IDaemonConnectionPoolManager _poolManager;
    private readonly IServiceProvider _authServices;
    private readonly ILogger<RpcMethodHandler> _logger;
    private readonly CancellationTokenSource _daemonCts = new();
    private JsonRpc? _rpc;

    // Discovery cache for env/list
    private List<EnvironmentInfo>? _discoveredEnvCache;
    private DateTime _discoveredEnvCacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan DiscoveryCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="RpcMethodHandler"/> class.
    /// </summary>
    /// <param name="poolManager">The connection pool manager for caching Dataverse pools.</param>
    /// <param name="authServices">Service provider for auth services (ProfileStore, ISecureCredentialStore).</param>
    /// <param name="logger">Optional logger. If null, a NullLogger is used.</param>
    public RpcMethodHandler(
        IDaemonConnectionPoolManager poolManager,
        IServiceProvider authServices,
        ILogger<RpcMethodHandler>? logger = null)
    {
        _poolManager = poolManager ?? throw new ArgumentNullException(nameof(poolManager));
        _authServices = authServices ?? throw new ArgumentNullException(nameof(authServices));
        _logger = logger ?? NullLogger<RpcMethodHandler>.Instance;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _daemonCts.Cancel();
        _daemonCts.Dispose();
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
        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profiles = new List<ProfileInfo>();
        foreach (var p in collection.All)
        {
            EnvironmentSummary? envSummary = null;
            if (p.Environment != null)
            {
                var label = await ResolveEnvironmentLabelAsync(
                    p.Environment.Url, p.Environment.DisplayName, cancellationToken);
                envSummary = new EnvironmentSummary
                {
                    Url = p.Environment.Url,
                    DisplayName = label,
                    EnvironmentId = p.Environment.EnvironmentId
                };
            }
            profiles.Add(new ProfileInfo
            {
                Index = p.Index,
                Name = p.Name,
                Identity = p.IdentityDisplay,
                AuthMethod = p.AuthMethod.ToString(),
                Cloud = p.Cloud.ToString(),
                Environment = envSummary,
                IsActive = collection.ActiveProfile?.Index == p.Index,
                CreatedAt = p.CreatedAt,
                LastUsedAt = p.LastUsedAt
            });
        }

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
        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        // Query MSAL for current token state (if environment is bound)
        CachedTokenInfo? tokenInfo = null;
        if (profile.Environment != null && !string.IsNullOrEmpty(profile.Environment.Url))
        {
            try
            {
                using var provider = CredentialProviderFactory.Create(profile);
                tokenInfo = await provider.GetCachedTokenInfoAsync(profile.Environment.Url, cancellationToken);
            }
            catch
            {
                // Ignore errors - token info will be null
            }
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
            TokenExpiresOn = tokenInfo?.ExpiresOn,
            TokenStatus = tokenInfo != null
                ? (tokenInfo.IsExpired ? "expired" : "valid")
                : null,
            Environment = profile.Environment != null ? new EnvironmentDetails
            {
                Url = profile.Environment.Url,
                DisplayName = await ResolveEnvironmentLabelAsync(
                    profile.Environment.Url, profile.Environment.DisplayName, cancellationToken),
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

        var store = _authServices.GetRequiredService<ProfileStore>();
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
            profile = collection.GetByNameOrIndex(name!);
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
    /// Lists available environments by merging discovered (Global Discovery API) and
    /// configured (environments.json) environments. Discovery results are cached for 5 minutes.
    /// Maps to: ppds env list --json
    /// </summary>
    [JsonRpcMethod("env/list")]
    public async Task<EnvListResponse> EnvListAsync(
        string? filter = null,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();

        // 1. Get discovered environments (cached, unless forceRefresh)
        List<EnvironmentInfo> discovered;
        if (!forceRefresh && _discoveredEnvCache != null && DateTime.UtcNow < _discoveredEnvCacheExpiry)
        {
            discovered = _discoveredEnvCache;
        }
        else
        {
            discovered = new List<EnvironmentInfo>();
            try
            {
                using var gds = GlobalDiscoveryService.FromProfile(profile);
                var environments = await gds.DiscoverEnvironmentsAsync(cancellationToken);
                discovered = environments.Select(e => new EnvironmentInfo
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
                        e.ApiUrl.TrimEnd('/').ToLowerInvariant() == selectedUrl,
                    Source = "discovered"
                }).ToList();
            }
            catch (Exception ex)
            {
                // Discovery may fail for SPNs or when offline — continue with configured only
                _logger.LogDebug(ex, "Environment discovery failed, using configured environments only");
            }
            _discoveredEnvCache = discovered;
            _discoveredEnvCacheExpiry = DateTime.UtcNow + DiscoveryCacheTtl;
        }

        // 2. Get configured environments from environments.json and tag discovered envs with profile
        var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
        var configCollection = await configStore.LoadAsync(cancellationToken);
        var profileName = profile.Name ?? profile.DisplayIdentifier;

        // Tag all discovered environments with this profile in environments.json
        foreach (var env in discovered)
        {
            await configStore.SaveConfigAsync(
                env.ApiUrl,
                discoveredType: env.Type,
                profileName: profileName,
                ct: cancellationToken);
        }

        // Reload config after tagging
        configStore.ClearCache();
        configCollection = await configStore.LoadAsync(cancellationToken);

        // 3. Merge: start with discovered, add configured that belong to this profile
        var merged = new List<EnvironmentInfo>(discovered);
        var discoveredUrls = new HashSet<string>(
            discovered.Select(e => EnvironmentConfig.NormalizeUrl(e.ApiUrl)));

        foreach (var config in configCollection.Environments)
        {
            var normalizedUrl = EnvironmentConfig.NormalizeUrl(config.Url);
            if (discoveredUrls.Contains(normalizedUrl))
            {
                // Already in discovered — update source to "both" and apply config metadata
                var existing = merged.First(e => EnvironmentConfig.NormalizeUrl(e.ApiUrl) == normalizedUrl);
                existing.Source = "both";
                if (config.Label != null) existing.FriendlyName = config.Label;
            }
            else if (config.Profiles.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                // Only in config but linked to this profile — add with source "configured"
                merged.Add(new EnvironmentInfo
                {
                    Id = Guid.Empty,
                    EnvironmentId = null,
                    FriendlyName = config.Label ?? config.Url,
                    UniqueName = "",
                    ApiUrl = config.Url,
                    Url = config.Url,
                    Type = config.Type?.ToString(),
                    State = "Unknown",
                    Region = null,
                    Version = null,
                    IsActive = selectedUrl != null && normalizedUrl == selectedUrl,
                    Source = "configured"
                });
            }
        }

        // 4. Apply filter if provided
        IEnumerable<EnvironmentInfo> result = merged;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            result = merged.Where(e =>
                e.FriendlyName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (e.UniqueName?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                e.ApiUrl.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                (e.EnvironmentId?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        return new EnvListResponse
        {
            Filter = filter,
            Environments = result.ToList()
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

        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile;
        if (profile == null)
        {
            throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        // Use multi-layer resolution
        var credentialStore = _authServices.GetRequiredService<ISecureCredentialStore>();
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

        _discoveredEnvCache = null; // Invalidate env list cache

        return new EnvSelectResponse
        {
            Url = resolved.Url,
            DisplayName = await ResolveEnvironmentLabelAsync(
                resolved.Url, resolved.DisplayName, cancellationToken),
            UniqueName = resolved.UniqueName,
            EnvironmentId = resolved.EnvironmentId,
            ResolutionMethod = result.Method.ToString()
        };
    }

    /// <summary>
    /// Gets WhoAmI information for the current environment.
    /// Maps to: ppds env who --json
    /// </summary>
    [JsonRpcMethod("env/who")]
    public async Task<EnvWhoResponse> EnvWhoAsync(CancellationToken cancellationToken = default)
    {
        return await WithActiveProfileAsync(async (sp, profile, env, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await pool.GetClientAsync(cancellationToken: ct);

            // WhoAmI verifies the connection and returns user/org IDs
            var whoAmI = (WhoAmIResponse)await client.ExecuteAsync(
                new WhoAmIRequest(), ct);

            // Org info is available directly on the client
            var orgName = client.ConnectedOrgFriendlyName;
            var orgUniqueName = client.ConnectedOrgUniqueName;
            var orgId = client.ConnectedOrgId ?? Guid.Empty;
            var orgVersion = client.ConnectedOrgVersion?.ToString();

            // Use the already-validated profile and environment passed by WithActiveProfileAsync —
            // no need to reload from ProfileStore, which avoids a null-forgiving operator race.
            return new EnvWhoResponse
            {
                OrganizationName = orgName ?? env.DisplayName,
                Url = env.Url,
                UniqueName = orgUniqueName ?? env.UniqueName ?? "",
                Version = orgVersion ?? "",
                OrganizationId = orgId != Guid.Empty ? orgId : Guid.TryParse(env.OrganizationId, out var parsedOrgId) ? parsedOrgId : Guid.Empty,
                UserId = whoAmI.UserId,
                BusinessUnitId = whoAmI.BusinessUnitId,
                ConnectedAs = profile.IdentityDisplay,
                EnvironmentType = env.Type
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets environment configuration (label, type, color).
    /// Maps to: ppds env config get --environment "url" --json
    /// </summary>
    [JsonRpcMethod("env/config/get")]
    public async Task<EnvConfigGetResponse> EnvConfigGetAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'environmentUrl' parameter is required");
        }
        if (!Uri.TryCreate(environmentUrl, UriKind.Absolute, out _))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"The 'environmentUrl' parameter must be a valid absolute URL");
        }

        var configService = _authServices.GetRequiredService<IEnvironmentConfigService>();
        var config = await configService.GetConfigAsync(environmentUrl, cancellationToken);
        var resolvedColor = await configService.ResolveColorAsync(environmentUrl, cancellationToken);
        var resolvedType = await configService.ResolveTypeAsync(environmentUrl, ct: cancellationToken);

        return new EnvConfigGetResponse
        {
            EnvironmentUrl = environmentUrl,
            Label = config?.Label,
            Type = config?.Type?.ToString(),
            Color = config?.Color?.ToString(),
            ResolvedType = resolvedType.ToString(),
            ResolvedColor = resolvedColor.ToString()
        };
    }

    /// <summary>
    /// Sets environment configuration (label, type, color).
    /// Maps to: ppds env config set --environment "url" --label "label" --type "type" --color "color"
    /// </summary>
    [JsonRpcMethod("env/config/set")]
    public async Task<EnvConfigSetResponse> EnvConfigSetAsync(
        string environmentUrl,
        string? label = null,
        string? type = null,
        string? color = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'environmentUrl' parameter is required");
        }
        if (!Uri.TryCreate(environmentUrl, UriKind.Absolute, out _))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"The 'environmentUrl' parameter must be a valid absolute URL");
        }

        var configService = _authServices.GetRequiredService<IEnvironmentConfigService>();

        EnvironmentType? parsedType = null;
        if (!string.IsNullOrWhiteSpace(type))
        {
            if (!Enum.TryParse<EnvironmentType>(type, ignoreCase: true, out var t))
            {
                throw new RpcException(
                    ErrorCodes.Validation.InvalidArguments,
                    $"Invalid environment type '{type}'. Valid values: {string.Join(", ", Enum.GetNames<EnvironmentType>())}");
            }
            parsedType = t;
        }

        EnvironmentColor? parsedColor = null;
        if (!string.IsNullOrWhiteSpace(color))
        {
            if (!Enum.TryParse<EnvironmentColor>(color, ignoreCase: true, out var c))
            {
                throw new RpcException(
                    ErrorCodes.Validation.InvalidArguments,
                    $"Invalid environment color '{color}'. Valid values: {string.Join(", ", Enum.GetNames<EnvironmentColor>())}");
            }
            parsedColor = c;
        }

        var saved = await configService.SaveConfigAsync(
            environmentUrl, label, parsedType, parsedColor, ct: cancellationToken);

        return new EnvConfigSetResponse
        {
            EnvironmentUrl = environmentUrl,
            Label = saved.Label,
            Type = saved.Type?.ToString(),
            Color = saved.Color?.ToString(),
            Saved = true
        };
    }

    /// <summary>
    /// Removes a configured environment from environments.json.
    /// Maps to: ppds env config remove --environment "url"
    /// </summary>
    [JsonRpcMethod("env/config/remove")]
    public async Task<EnvConfigRemoveResponse> EnvConfigRemoveAsync(
        string environmentUrl,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(environmentUrl))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'environmentUrl' parameter is required");
        }

        var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
        var removed = await configStore.RemoveConfigAsync(environmentUrl, cancellationToken);

        return new EnvConfigRemoveResponse { Removed = removed };
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
        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();

            // Create registration service with pool (use NullLogger for daemon context)
            var registrationService = new PluginRegistrationService(
                pool,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PluginRegistrationService>.Instance);

            var response = new PluginsListResponse();

            // Get assemblies (unless package filter is specified)
            if (string.IsNullOrEmpty(package))
            {
                var assemblies = await registrationService.ListAssembliesAsync(assembly, options: null, ct);

                foreach (var asm in assemblies)
                {
                    var assemblyOutput = new PluginAssemblyInfo
                    {
                        Name = asm.Name,
                        Version = asm.Version,
                        PublicKeyToken = asm.PublicKeyToken,
                        Types = []
                    };

                    var types = await registrationService.ListTypesForAssemblyAsync(asm.Id, ct);
                    await PopulatePluginTypesAsync(registrationService, types, assemblyOutput.Types, ct);

                    response.Assemblies.Add(assemblyOutput);
                }
            }

            // Get packages (unless assembly filter is specified)
            if (string.IsNullOrEmpty(assembly))
            {
                var packages = await registrationService.ListPackagesAsync(package, options: null, ct);

                foreach (var pkg in packages)
                {
                    var packageOutput = new PluginPackageInfo
                    {
                        Name = pkg.Name,
                        UniqueName = pkg.UniqueName,
                        Version = pkg.Version,
                        Assemblies = []
                    };

                    var pkgAssemblies = await registrationService.ListAssembliesForPackageAsync(pkg.Id, ct);
                    foreach (var asm in pkgAssemblies)
                    {
                        var assemblyOutput = new PluginAssemblyInfo
                        {
                            Name = asm.Name,
                            Version = asm.Version,
                            PublicKeyToken = asm.PublicKeyToken,
                            Types = []
                        };

                        var types = await registrationService.ListTypesForAssemblyAsync(asm.Id, ct);
                        await PopulatePluginTypesAsync(registrationService, types, assemblyOutput.Types, ct);

                        packageOutput.Assemblies.Add(assemblyOutput);
                    }

                    response.Packages.Add(packageOutput);
                }
            }

            return response;
        }, cancellationToken);
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
        var stepTasks = types.Select(t => registrationService.ListStepsForTypeAsync(t.Id, options: null, cancellationToken));
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
    /// Lists all entities in the environment with summary metadata.
    /// Used by VS Code extension for IntelliSense entity completion.
    /// </summary>
    [JsonRpcMethod("schema/entities")]
    public async Task<SchemaEntitiesResponse> SchemaEntitiesAsync(
        CancellationToken cancellationToken = default)
    {
        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var metadataProvider = sp.GetRequiredService<ICachedMetadataProvider>();
            var entities = await metadataProvider.GetEntitiesAsync(ct);

            return new SchemaEntitiesResponse
            {
                Entities = entities.Select(e => new EntitySummaryDto
                {
                    LogicalName = e.LogicalName,
                    DisplayName = e.DisplayName,
                    IsCustom = e.IsCustomEntity
                }).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Lists attributes for a specific entity.
    /// Used by VS Code extension for IntelliSense attribute completion.
    /// </summary>
    [JsonRpcMethod("schema/attributes")]
    public async Task<SchemaAttributesResponse> SchemaAttributesAsync(
        string entity,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'entity' parameter is required");
        }

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var metadataProvider = sp.GetRequiredService<ICachedMetadataProvider>();
            var attributes = await metadataProvider.GetAttributesAsync(entity, ct);

            return new SchemaAttributesResponse
            {
                EntityName = entity,
                Attributes = attributes.Select(a => new AttributeSummaryDto
                {
                    LogicalName = a.LogicalName,
                    DisplayName = a.DisplayName,
                    DataType = a.AttributeType,
                    IsCustom = a.IsCustomAttribute
                }).ToList()
            };
        }, cancellationToken);
    }

    #endregion

    #region Query Methods

    /// <summary>
    /// Gets completion items at a given cursor position for SQL or FetchXML.
    /// Used by VS Code extension for IntelliSense in the query editor.
    /// </summary>
    [JsonRpcMethod("query/complete")]
    public async Task<QueryCompleteResponse> QueryCompleteAsync(
        QueryCompleteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }
        if (request.CursorOffset < 0 || request.CursorOffset > request.Sql.Length)
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"cursorOffset must be between 0 and {request.Sql.Length}");
        }

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var metadataProvider = sp.GetRequiredService<ICachedMetadataProvider>();

            IReadOnlyList<PPDS.Dataverse.Sql.Intellisense.SqlCompletion> completions;

            if (string.Equals(request.Language, "fetchxml", StringComparison.OrdinalIgnoreCase))
            {
                var engine = new FetchXmlCompletionEngine(metadataProvider);
                completions = await engine.GetCompletionsAsync(request.Sql, request.CursorOffset, ct);
            }
            else
            {
                var engine = new SqlCompletionEngine(metadataProvider);
                completions = await engine.GetCompletionsAsync(request.Sql, request.CursorOffset, ct);
            }

            return new QueryCompleteResponse
            {
                Items = completions.Select(c => new CompletionItemDto
                {
                    Label = c.Label,
                    InsertText = c.InsertText,
                    Kind = c.Kind.ToString().ToLowerInvariant(),
                    Detail = c.Detail,
                    Description = c.Description,
                    SortOrder = c.SortOrder
                }).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Executes a FetchXML query against Dataverse.
    /// Maps to: ppds query fetch --json
    /// </summary>
    [JsonRpcMethod("query/fetch")]
    public async Task<QueryResultResponse> QueryFetchAsync(
        QueryFetchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FetchXml))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'fetchXml' parameter is required");
        }

        // Inject top attribute if specified
        var query = request.FetchXml;
        if (request.Top.HasValue)
        {
            query = InjectTopAttribute(query, request.Top.Value);
        }

        var response = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
        {
            var queryExecutor = sp.GetRequiredService<IQueryExecutor>();
            var result = await queryExecutor.ExecuteFetchXmlAsync(
                query,
                request.Page,
                request.PagingCookie,
                request.Count,
                ct);

            return MapToResponse(result, query);
        }, cancellationToken);

        // Auto-save to history (fire-and-forget)
        FireAndForgetHistorySave(request.FetchXml, response);

        return response;
    }

    /// <summary>
    /// Executes a SQL query against Dataverse by transpiling to FetchXML.
    /// Maps to: ppds query sql --json
    /// </summary>
    [JsonRpcMethod("query/sql")]
    public async Task<QueryResultResponse> QuerySqlAsync(
        QuerySqlRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        if (request.UseTds)
        {
            var response = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, profile, env, ct) =>
            {
                using var credentialProvider = CredentialProviderFactory.Create(
                    profile,
                    DaemonDeviceCodeHandler.CreateCallback(_rpc));

                var tdsExecutor = new TdsQueryExecutor(
                    env.Url,
                    async token =>
                    {
                        // Create a ServiceClient to trigger MSAL token acquisition,
                        // then grab the cached access token from the credential provider.
                        var client = await credentialProvider.CreateServiceClientAsync(env.Url, token)
                            .ConfigureAwait(false);
                        client.Dispose();
                        return credentialProvider.AccessToken
                            ?? throw new InvalidOperationException("Failed to acquire access token for TDS endpoint");
                    },
                    sp.GetService<ILogger<TdsQueryExecutor>>());

                var result = await tdsExecutor.ExecuteSqlAsync(request.Sql, request.Top, ct);
                return MapToResponse(result, null);
            }, cancellationToken);

            // Auto-save to history (fire-and-forget)
            FireAndForgetHistorySave(request.Sql, response);

            return response;
        }

        var fetchXml = TranspileSqlToFetchXml(request.Sql, request.Top);

        // If showFetchXml is true, just return the transpiled FetchXML
        if (request.ShowFetchXml)
        {
            return new QueryResultResponse
            {
                Success = true,
                ExecutedFetchXml = fetchXml
            };
        }

        var fetchResponse = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
        {
            var queryExecutor = sp.GetRequiredService<IQueryExecutor>();
            var result = await queryExecutor.ExecuteFetchXmlAsync(
                fetchXml,
                request.Page,
                request.PagingCookie,
                request.Count,
                ct);

            return MapToResponse(result, fetchXml);
        }, cancellationToken);

        // Auto-save to history (fire-and-forget)
        FireAndForgetHistorySave(request.Sql, fetchResponse);

        return fetchResponse;
    }

    /// <summary>
    /// Lists query history entries for the active environment.
    /// Maps to: ppds query history list --json
    /// </summary>
    [JsonRpcMethod("query/history/list")]
    public async Task<QueryHistoryListResponse> QueryHistoryListAsync(
        QueryHistoryListRequest request,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Min(request.Limit, 1000);
        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

        var environment = profile.Environment
            ?? throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");

        var historyService = _authServices.GetRequiredService<IQueryHistoryService>();

        IReadOnlyList<QueryHistoryEntry> entries;
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            entries = await historyService.SearchHistoryAsync(environment.Url, request.Search, limit, cancellationToken);
        }
        else
        {
            entries = await historyService.GetHistoryAsync(environment.Url, limit, cancellationToken);
        }

        return new QueryHistoryListResponse
        {
            Entries = entries.Select(e => new QueryHistoryEntryDto
            {
                Id = e.Id,
                Sql = e.Sql,
                RowCount = e.RowCount,
                ExecutionTimeMs = e.ExecutionTimeMs,
                EnvironmentUrl = environment.Url,
                ExecutedAt = e.ExecutedAt
            }).ToList()
        };
    }

    /// <summary>
    /// Deletes a query history entry by ID.
    /// Maps to: ppds query history delete --json
    /// </summary>
    [JsonRpcMethod("query/history/delete")]
    public async Task<QueryHistoryDeleteResponse> QueryHistoryDeleteAsync(
        QueryHistoryDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter is required");
        }

        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

        var environment = profile.Environment
            ?? throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");

        var historyService = _authServices.GetRequiredService<IQueryHistoryService>();
        var deleted = await historyService.DeleteEntryAsync(environment.Url, request.Id, cancellationToken);

        return new QueryHistoryDeleteResponse
        {
            Deleted = deleted
        };
    }

    /// <summary>
    /// Exports query results in the specified format (CSV, TSV, or JSON).
    /// Reuses the same SQL-to-FetchXML transpilation and execution pipeline as QuerySqlAsync.
    /// </summary>
    [JsonRpcMethod("query/export")]
    public async Task<QueryExportResponse> QueryExportAsync(
        QueryExportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        var format = request.Format.ToLowerInvariant();
        if (format is not ("csv" or "tsv" or "json"))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"Invalid format '{format}'. Valid values: csv, tsv, json");
        }

        var fetchXml = TranspileSqlToFetchXml(request.Sql, request.Top);

        // Execute the query
        const int MaxExportRecords = 100_000;

        var queryResponse = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
        {
            var queryExecutor = sp.GetRequiredService<IQueryExecutor>();

            // Fetch all pages to export complete results
            var allRecords = new List<Dictionary<string, object?>>();
            List<QueryColumnInfo>? columns = null;
            string? pagingCookie = null;
            int? currentPage = null;
            bool moreRecords;

            do
            {
                var result = await queryExecutor.ExecuteFetchXmlAsync(
                    fetchXml,
                    currentPage,
                    pagingCookie,
                    false,
                    ct);

                var mapped = MapToResponse(result, fetchXml);
                columns ??= mapped.Columns;

                allRecords.AddRange(mapped.Records);
                pagingCookie = mapped.PagingCookie;
                moreRecords = mapped.MoreRecords;
                currentPage = (currentPage ?? 1) + 1;

                if (allRecords.Count >= MaxExportRecords)
                {
                    break; // Safety cap to prevent OOM on very large exports
                }
            } while (moreRecords);

            return (columns: columns ?? [], records: allRecords);
        }, cancellationToken);

        // Format the results
        var content = FormatExportContent(
            queryResponse.columns,
            queryResponse.records,
            format,
            request.IncludeHeaders);

        return new QueryExportResponse
        {
            Content = content,
            Format = format,
            RowCount = queryResponse.records.Count
        };
    }

    /// <summary>
    /// Returns the execution plan for a SQL query by transpiling it to FetchXML.
    /// Since Dataverse SQL is always transpiled to FetchXML for execution,
    /// the FetchXML output serves as the execution plan.
    /// </summary>
    [JsonRpcMethod("query/explain")]
    public Task<QueryExplainResponse> QueryExplainAsync(
        QueryExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        var fetchXml = TranspileSqlToFetchXml(request.Sql);

        return Task.FromResult(new QueryExplainResponse
        {
            Plan = fetchXml,
            Format = "fetchxml"
        });
    }

    /// <summary>
    /// Saves a query to history in a fire-and-forget fashion so callers are not
    /// blocked. History saves are best-effort: cancellation (daemon shutdown) is silently
    /// ignored, and all other exceptions are logged at Debug level to aid diagnostics
    /// without surfacing failures to the caller.
    /// </summary>
    private void FireAndForgetHistorySave(string queryText, QueryResultResponse response)
    {
        var daemonToken = _daemonCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                daemonToken.ThrowIfCancellationRequested();

                var historyService = _authServices.GetService<IQueryHistoryService>();
                if (historyService != null)
                {
                    var store = _authServices.GetRequiredService<ProfileStore>();
                    var collection = await store.LoadAsync(daemonToken);
                    var envUrl = collection.ActiveProfile?.Environment?.Url;
                    if (envUrl != null)
                    {
                        await historyService.AddQueryAsync(
                            envUrl, queryText,
                            rowCount: response.Count,
                            executionTimeMs: response.ExecutionTimeMs,
                            cancellationToken: daemonToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Daemon is shutting down — silently discard
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to save query history entry");
            }
        }, daemonToken);
    }

    /// <summary>
    /// Parses a SQL statement and transpiles it to FetchXML.
    /// Optionally injects a TOP clause for row-limiting.
    /// </summary>
    private static string TranspileSqlToFetchXml(string sql, int? top = null)
    {
        TSqlStatement stmt;
        try
        {
            var parser = new QueryParser();
            stmt = parser.ParseStatement(sql);
        }
        catch (QueryParseException ex)
        {
            throw new RpcException(ErrorCodes.Query.ParseError, ex);
        }

        if (top.HasValue && stmt is SelectStatement selectStmt
            && selectStmt.QueryExpression is QuerySpecification querySpec)
        {
            querySpec.TopRowFilter = new TopRowFilter
            {
                Expression = new IntegerLiteral { Value = top.Value.ToString() }
            };
        }

        try
        {
            return new FetchXmlGenerator().Generate(stmt);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            throw new RpcException(ErrorCodes.Query.ParseError, ex.Message);
        }
    }

    private static string FormatExportContent(
        List<QueryColumnInfo> columns,
        List<Dictionary<string, object?>> records,
        string format,
        bool includeHeaders)
    {
        if (format == "json")
        {
            // JSON array of objects
            var jsonArray = records.Select(record =>
            {
                var obj = new Dictionary<string, object?>();
                foreach (var col in columns)
                {
                    var key = col.Alias ?? col.LogicalName;
                    record.TryGetValue(key, out var val);
                    obj[key] = ExtractDisplayValue(val);
                }
                return obj;
            }).ToList();

            return System.Text.Json.JsonSerializer.Serialize(jsonArray,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }

        // CSV or TSV
        var separator = format == "tsv" ? '\t' : ',';
        var sb = new System.Text.StringBuilder();

        if (includeHeaders)
        {
            var headers = columns.Select(c => c.Alias ?? c.LogicalName);
            if (format == "csv")
            {
                sb.AppendLine(string.Join(separator, headers.Select(h => CsvEscape(h, separator))));
            }
            else
            {
                sb.AppendLine(string.Join(separator, headers));
            }
        }

        foreach (var record in records)
        {
            var values = columns.Select(col =>
            {
                var key = col.Alias ?? col.LogicalName;
                record.TryGetValue(key, out var val);
                var display = ExtractDisplayValue(val)?.ToString() ?? "";
                return format == "csv" ? CsvEscape(display, separator) : display.Replace("\t", " ").Replace("\n", " ").Replace("\r", "");
            });
            sb.AppendLine(string.Join(separator, values));
        }

        return sb.ToString();
    }

    private static object? ExtractDisplayValue(object? val)
    {
        if (val is Dictionary<string, object?> dict)
        {
            // Return formatted value if available, otherwise raw value
            if (dict.TryGetValue("formatted", out var formatted) && formatted != null)
                return formatted;
            if (dict.TryGetValue("value", out var value))
                return value;
        }
        return val;
    }

    private static string CsvEscape(string value, char separator)
    {
        if (value.Contains(separator) || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }

    /// <summary>
    /// Injects a top="N" attribute into generated FetchXML.
    /// Uses string manipulation (not XML parsing) because the FetchXML
    /// comes from our generator and always has a predictable format.
    /// </summary>
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

    private static QueryResultResponse MapToResponse(QueryResult result, string? fetchXml)
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

    /// <summary>
    /// Executes an action with the validated active profile, its environment, and a cached service provider.
    /// Profile and environment are loaded once, validated, then passed directly into the lambda —
    /// eliminating the need for callers to reload the store or use null-forgiving operators.
    /// The service provider is long-lived (cached by the pool manager) — do NOT dispose it inside the action.
    /// </summary>
    /// <typeparam name="T">The return type of the action.</typeparam>
    /// <param name="action">The action to execute with the service provider, profile, and environment.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The result of the action.</returns>
    /// <exception cref="RpcException">Thrown when no active profile or environment is configured.</exception>
    private async Task<T> WithActiveProfileAsync<T>(
        Func<IServiceProvider, AuthProfile, PPDS.Auth.Profiles.EnvironmentInfo, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

        var environment = profile.Environment
            ?? throw new RpcException(
                ErrorCodes.Connection.EnvironmentNotFound,
                "No environment selected. Use env/select first.");

        // Use the pool manager to get a cached service provider. This reuses the existing
        // connection pool instead of creating a new ServiceClient on every RPC call.
        var serviceProvider = await _poolManager.GetOrCreateServiceProviderAsync(
            new[] { profile.Name ?? profile.DisplayIdentifier },
            environment.Url,
            deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc),
            cancellationToken: cancellationToken);

        return await action(serviceProvider, profile, environment, cancellationToken);
    }

    /// <summary>
    /// Convenience overload for actions that only need the service provider and cancellation token.
    /// Wraps the full overload, discarding the profile and environment parameters.
    /// </summary>
    private Task<T> WithActiveProfileAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
        => WithActiveProfileAsync<T>(
            (sp, _, _, ct) => action(sp, ct),
            cancellationToken);

    /// <summary>
    /// Resolves the display name for an environment URL, preferring the user's configured label
    /// from environments.json over the raw discovery/profile display name.
    /// </summary>
    private async Task<string> ResolveEnvironmentLabelAsync(string url, string fallbackDisplayName, CancellationToken ct)
    {
        try
        {
            var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
            var config = await configStore.GetConfigAsync(url, ct);
            if (config?.Label != null) return config.Label;
        }
        catch { /* config lookup is best-effort */ }
        return fallbackDisplayName;
    }

    /// <summary>
    /// Executes an action with the active profile's credentials against a specific environment.
    /// If environmentUrl is provided, uses it; otherwise falls back to the active profile's saved environment.
    /// </summary>
    private async Task<T> WithProfileAndEnvironmentAsync<T>(
        string? environmentUrl,
        Func<IServiceProvider, AuthProfile, PPDS.Auth.Profiles.EnvironmentInfo, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        var profile = collection.ActiveProfile
            ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");

        // Resolve environment: explicit URL wins, else profile's saved environment
        string resolvedUrl;
        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            resolvedUrl = environmentUrl;
        }
        else
        {
            var env = profile.Environment
                ?? throw new RpcException(
                    ErrorCodes.Connection.EnvironmentNotFound,
                    "No environment selected. Use env/select first.");
            resolvedUrl = env.Url;
        }

        // Build an EnvironmentInfo for the resolved URL
        var resolvedEnvironment = profile.Environment?.Url?.Equals(resolvedUrl, StringComparison.OrdinalIgnoreCase) == true
            ? profile.Environment
            : new PPDS.Auth.Profiles.EnvironmentInfo { Url = resolvedUrl, DisplayName = resolvedUrl };

        var serviceProvider = await _poolManager.GetOrCreateServiceProviderAsync(
            new[] { profile.Name ?? profile.DisplayIdentifier },
            resolvedUrl,
            deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc),
            cancellationToken: cancellationToken);

        return await action(serviceProvider, profile, resolvedEnvironment, cancellationToken);
    }

    /// <summary>
    /// Convenience overload for actions that only need the service provider and cancellation token.
    /// </summary>
    private Task<T> WithProfileAndEnvironmentAsync<T>(
        string? environmentUrl,
        Func<IServiceProvider, CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
        => WithProfileAndEnvironmentAsync<T>(
            environmentUrl,
            (sp, _, _, ct) => action(sp, ct),
            cancellationToken);

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

    #region Profile CRUD

    /// <summary>
    /// Creates a new authentication profile.
    /// Maps to: ppds auth create --json
    /// </summary>
    /// <remarks>
    /// SECURITY: <paramref name="clientSecret"/>, <paramref name="password"/>, and
    /// <paramref name="certificatePassword"/> are transmitted as plain-text JSON-RPC parameters
    /// over the local stdio pipe. This is acceptable for local-only IPC (the pipe is not
    /// network-accessible) but these values MUST NOT be logged anywhere in the call stack.
    /// StreamJsonRpc trace logging is deliberately not configured in ServeCommand to avoid
    /// capturing sensitive parameters. The extension should prefer device code or browser-based
    /// auth flows (Interactive, DeviceCode) over secret-bearing flows when possible.
    /// </remarks>
    [JsonRpcMethod("profiles/create")]
    public async Task<ProfileCreateResponse> ProfilesCreateAsync(
        string authMethod,
        string? name = null,
        string? applicationId = null,
        [SensitiveData("Client secret transmitted as plain-text over local stdio pipe")]
        string? clientSecret = null,
        string? tenantId = null,
        string? environmentUrl = null,
        string? certificatePath = null,
        [SensitiveData("Certificate password transmitted as plain-text over local stdio pipe")]
        string? certificatePassword = null,
        string? certificateThumbprint = null,
        string? username = null,
        [SensitiveData("Password transmitted as plain-text over local stdio pipe")]
        string? password = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authMethod))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'authMethod' parameter is required");
        }

        if (!Enum.TryParse<AuthMethod>(authMethod, ignoreCase: true, out var parsedMethod))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"Invalid auth method '{authMethod}'. Valid values: {string.Join(", ", Enum.GetNames<AuthMethod>())}");
        }

        var request = new ProfileCreateRequest
        {
            Name = name,
            AuthMethod = parsedMethod,
            ApplicationId = applicationId,
            ClientSecret = clientSecret,
            TenantId = tenantId,
            Environment = environmentUrl,
            CertificatePath = certificatePath,
            CertificatePassword = certificatePassword,
            CertificateThumbprint = certificateThumbprint,
            Username = username,
            Password = password,
        };

        var profileService = _authServices.GetRequiredService<IProfileService>();
        var result = await profileService.CreateProfileAsync(
            request,
            deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc),
            beforeInteractiveAuth: null,
            cancellationToken: cancellationToken);

        return new ProfileCreateResponse
        {
            Index = result.Index,
            Name = result.Name,
            Identity = result.Identity,
            AuthMethod = result.AuthMethod.ToString(),
            Environment = result.EnvironmentUrl,
        };
    }

    /// <summary>
    /// Deletes an authentication profile by index or name.
    /// Maps to: ppds auth delete --json
    /// </summary>
    [JsonRpcMethod("profiles/delete")]
    public async Task<ProfileDeleteResponse> ProfilesDeleteAsync(
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

        var nameOrIndex = index != null ? index.Value.ToString() : name!;
        var profileService = _authServices.GetRequiredService<IProfileService>();
        var deleted = await profileService.DeleteProfileAsync(nameOrIndex, cancellationToken);

        return new ProfileDeleteResponse
        {
            Deleted = deleted,
            ProfileName = nameOrIndex,
        };
    }

    /// <summary>
    /// Renames an authentication profile.
    /// Maps to: ppds auth rename --json
    /// </summary>
    [JsonRpcMethod("profiles/rename")]
    public async Task<ProfileRenameResponse> ProfilesRenameAsync(
        string currentName,
        string newName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'currentName' parameter is required");
        }

        if (string.IsNullOrWhiteSpace(newName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'newName' parameter is required");
        }

        var profileService = _authServices.GetRequiredService<IProfileService>();
        var result = await profileService.UpdateProfileAsync(
            currentName,
            newName: newName,
            cancellationToken: cancellationToken);

        return new ProfileRenameResponse
        {
            Index = result.Index,
            PreviousName = currentName,
            NewName = result.Name ?? newName,
        };
    }

    #endregion

    #region Solutions Methods

    /// <summary>
    /// Lists solutions in the environment.
    /// Maps to: ppds solutions list --json
    /// </summary>
    /// <param name="filter">Optional filter by solution unique name or friendly name.</param>
    /// <param name="includeManaged">Include managed solutions in the list (default: false).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of solutions matching the filter criteria.</returns>
    [JsonRpcMethod("solutions/list")]
    public async Task<SolutionsListResponse> SolutionsListAsync(
        string? filter = null,
        bool includeManaged = false,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var solutionService = sp.GetRequiredService<ISolutionService>();
            var solutions = await solutionService.ListAsync(filter, includeManaged, ct);

            return new SolutionsListResponse
            {
                Solutions = solutions.Select(s => new SolutionInfoDto
                {
                    Id = s.Id,
                    UniqueName = s.UniqueName,
                    FriendlyName = s.FriendlyName,
                    Version = s.Version,
                    IsManaged = s.IsManaged,
                    PublisherName = s.PublisherName,
                    Description = s.Description,
                    CreatedOn = s.CreatedOn,
                    ModifiedOn = s.ModifiedOn,
                    InstalledOn = s.InstalledOn
                }).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets components for a solution.
    /// Maps to: ppds solutions components --json
    /// </summary>
    /// <param name="uniqueName">The solution unique name.</param>
    /// <param name="componentType">Optional filter by component type (e.g., 61 for WebResource, 69 for PluginAssembly).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Solution components grouped by type.</returns>
    [JsonRpcMethod("solutions/components")]
    public async Task<SolutionComponentsResponse> SolutionsComponentsAsync(
        string uniqueName,
        int? componentType = null,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'uniqueName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var solutionService = sp.GetRequiredService<ISolutionService>();

            // First get the solution to find its ID
            var solution = await solutionService.GetAsync(uniqueName, ct);
            if (solution == null)
            {
                throw new RpcException(
                    ErrorCodes.Solution.NotFound,
                    $"Solution '{uniqueName}' not found");
            }

            var components = await solutionService.GetComponentsAsync(solution.Id, componentType, ct);

            return new SolutionComponentsResponse
            {
                SolutionId = solution.Id,
                UniqueName = solution.UniqueName,
                Components = components.Select(c => new SolutionComponentInfoDto
                {
                    Id = c.Id,
                    ObjectId = c.ObjectId,
                    ComponentType = c.ComponentType,
                    ComponentTypeName = c.ComponentTypeName,
                    RootComponentBehavior = c.RootComponentBehavior,
                    IsMetadata = c.IsMetadata,
                    DisplayName = c.DisplayName,
                    LogicalName = c.LogicalName,
                    SchemaName = c.SchemaName
                }).ToList()
            };
        }, cancellationToken);
    }

    #endregion
}

#region Response DTOs

// TODO: Extract DTOs to a separate RpcMethodDtos.cs file for better maintainability

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

    [JsonPropertyName("environmentId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentId { get; set; }
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

    [JsonPropertyName("source")]
    public string Source { get; set; } = "discovered";
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
/// Response for env/who method. Returns WhoAmI and environment details.
/// </summary>
public class EnvWhoResponse
{
    [JsonPropertyName("organizationName")]
    public string OrganizationName { get; set; } = "";

    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("organizationId")]
    public Guid OrganizationId { get; set; }

    [JsonPropertyName("userId")]
    public Guid UserId { get; set; }

    [JsonPropertyName("businessUnitId")]
    public Guid BusinessUnitId { get; set; }

    [JsonPropertyName("connectedAs")]
    public string ConnectedAs { get; set; } = "";

    [JsonPropertyName("environmentType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EnvironmentType { get; set; }
}

public class EnvConfigGetResponse
{
    [JsonPropertyName("environmentUrl")] public string EnvironmentUrl { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("resolvedType")] public string ResolvedType { get; set; } = "";
    [JsonPropertyName("resolvedColor")] public string ResolvedColor { get; set; } = "";
}

public class EnvConfigSetResponse
{
    [JsonPropertyName("environmentUrl")] public string EnvironmentUrl { get; set; } = "";
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("saved")] public bool Saved { get; set; }
}

/// <summary>
/// Response for env/config/remove method.
/// </summary>
public class EnvConfigRemoveResponse
{
    [JsonPropertyName("removed")]
    public bool Removed { get; set; }
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

/// <summary>
/// Response for profiles/create method.
/// </summary>
public class ProfileCreateResponse
{
    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    [JsonPropertyName("identity")] public string Identity { get; set; } = "";

    [JsonPropertyName("authMethod")] public string AuthMethod { get; set; } = "";

    [JsonPropertyName("environment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Environment { get; set; }
}

/// <summary>
/// Response for profiles/delete method.
/// </summary>
public class ProfileDeleteResponse
{
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }

    [JsonPropertyName("profileName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProfileName { get; set; }
}

/// <summary>
/// Response for profiles/rename method.
/// </summary>
public class ProfileRenameResponse
{
    [JsonPropertyName("index")] public int Index { get; set; }

    [JsonPropertyName("previousName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PreviousName { get; set; }

    [JsonPropertyName("newName")] public string NewName { get; set; } = "";
}

/// <summary>
/// Response for solutions/list method.
/// </summary>
public class SolutionsListResponse
{
    /// <summary>
    /// Gets or sets the list of solutions.
    /// </summary>
    [JsonPropertyName("solutions")]
    public List<SolutionInfoDto> Solutions { get; set; } = [];
}

/// <summary>
/// Solution information for RPC responses.
/// </summary>
public class SolutionInfoDto
{
    /// <summary>
    /// Gets or sets the solution ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the solution unique name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// Gets or sets the solution friendly name.
    /// </summary>
    [JsonPropertyName("friendlyName")]
    public string FriendlyName { get; set; } = "";

    /// <summary>
    /// Gets or sets the solution version.
    /// </summary>
    [JsonPropertyName("version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Version { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the solution is managed.
    /// </summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>
    /// Gets or sets the publisher name.
    /// </summary>
    [JsonPropertyName("publisherName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PublisherName { get; set; }

    /// <summary>
    /// Gets or sets the solution description.
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the creation date.
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? CreatedOn { get; set; }

    /// <summary>
    /// Gets or sets the last modification date.
    /// </summary>
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ModifiedOn { get; set; }

    /// <summary>
    /// Gets or sets the installation date.
    /// </summary>
    [JsonPropertyName("installedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? InstalledOn { get; set; }
}

/// <summary>
/// Response for solutions/components method.
/// </summary>
public class SolutionComponentsResponse
{
    /// <summary>
    /// Gets or sets the solution ID.
    /// </summary>
    [JsonPropertyName("solutionId")]
    public Guid SolutionId { get; set; }

    /// <summary>
    /// Gets or sets the solution unique name.
    /// </summary>
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    /// <summary>
    /// Gets or sets the list of solution components.
    /// </summary>
    [JsonPropertyName("components")]
    public List<SolutionComponentInfoDto> Components { get; set; } = [];
}

/// <summary>
/// Solution component information for RPC responses.
/// </summary>
public class SolutionComponentInfoDto
{
    /// <summary>
    /// Gets or sets the component ID.
    /// </summary>
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the object ID of the component.
    /// </summary>
    [JsonPropertyName("objectId")]
    public Guid ObjectId { get; set; }

    /// <summary>
    /// Gets or sets the component type code.
    /// </summary>
    [JsonPropertyName("componentType")]
    public int ComponentType { get; set; }

    /// <summary>
    /// Gets or sets the component type name.
    /// </summary>
    [JsonPropertyName("componentTypeName")]
    public string ComponentTypeName { get; set; } = "";

    /// <summary>
    /// Gets or sets the root component behavior.
    /// </summary>
    [JsonPropertyName("rootComponentBehavior")]
    public int RootComponentBehavior { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is a metadata component.
    /// </summary>
    [JsonPropertyName("isMetadata")]
    public bool IsMetadata { get; set; }

    /// <summary>
    /// Gets or sets the component display name.
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Gets or sets the component logical name.
    /// </summary>
    [JsonPropertyName("logicalName")]
    public string? LogicalName { get; set; }

    /// <summary>
    /// Gets or sets the component schema name.
    /// </summary>
    [JsonPropertyName("schemaName")]
    public string? SchemaName { get; set; }
}

/// <summary>
/// Response for schema/entities method.
/// </summary>
public class SchemaEntitiesResponse
{
    [JsonPropertyName("entities")] public List<EntitySummaryDto> Entities { get; set; } = [];
}

/// <summary>
/// Entity summary for schema/entities response.
/// </summary>
public class EntitySummaryDto
{
    [JsonPropertyName("logicalName")] public string LogicalName { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("isCustom")] public bool IsCustom { get; set; }
}

/// <summary>
/// Response for schema/attributes method.
/// </summary>
public class SchemaAttributesResponse
{
    [JsonPropertyName("entityName")] public string EntityName { get; set; } = "";
    [JsonPropertyName("attributes")] public List<AttributeSummaryDto> Attributes { get; set; } = [];
}

/// <summary>
/// Attribute summary for schema/attributes response.
/// </summary>
public class AttributeSummaryDto
{
    [JsonPropertyName("logicalName")] public string LogicalName { get; set; } = "";
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("dataType")] public string DataType { get; set; } = "";
    [JsonPropertyName("isCustom")] public bool IsCustom { get; set; }
}

/// <summary>
/// Response for query/complete method.
/// </summary>
public class QueryCompleteResponse
{
    [JsonPropertyName("items")] public List<CompletionItemDto> Items { get; set; } = [];
}

/// <summary>
/// Completion item for query/complete response.
/// </summary>
public class CompletionItemDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("insertText")] public string InsertText { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; set; }
}

/// <summary>
/// Response for query/history/list method.
/// </summary>
public class QueryHistoryListResponse
{
    [JsonPropertyName("entries")] public List<QueryHistoryEntryDto> Entries { get; set; } = [];
}

/// <summary>
/// A single query history entry DTO for RPC responses.
/// </summary>
public class QueryHistoryEntryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("rowCount")] public int? RowCount { get; set; }
    [JsonPropertyName("executionTimeMs")] public long? ExecutionTimeMs { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
    [JsonPropertyName("executedAt")] public DateTimeOffset ExecutedAt { get; set; }
}

/// <summary>
/// Response for query/history/delete method.
/// </summary>
public class QueryHistoryDeleteResponse
{
    [JsonPropertyName("deleted")] public bool Deleted { get; set; }
}

/// <summary>
/// Response for query/export method.
/// </summary>
public class QueryExportResponse
{
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "";
    [JsonPropertyName("rowCount")] public int RowCount { get; set; }
}

/// <summary>
/// Response for query/explain method.
/// </summary>
public class QueryExplainResponse
{
    [JsonPropertyName("plan")] public string Plan { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "fetchxml";
}

// ── Query Request DTOs ──────────────────────────────────────────────────────
// These DTOs accept named JSON-RPC parameters from the TypeScript client
// (e.g. { sql: "...", top: 100 }) instead of positional parameters.

/// <summary>
/// Request DTO for query/sql method.
/// </summary>
public class QuerySqlRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("top")] public int? Top { get; set; }
    [JsonPropertyName("page")] public int? Page { get; set; }
    [JsonPropertyName("pagingCookie")] public string? PagingCookie { get; set; }
    [JsonPropertyName("count")] public bool Count { get; set; }
    [JsonPropertyName("showFetchXml")] public bool ShowFetchXml { get; set; }
    [JsonPropertyName("useTds")] public bool UseTds { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
}

/// <summary>
/// Request DTO for query/fetch method.
/// </summary>
public class QueryFetchRequest
{
    [JsonPropertyName("fetchXml")] public string FetchXml { get; set; } = "";
    [JsonPropertyName("top")] public int? Top { get; set; }
    [JsonPropertyName("page")] public int? Page { get; set; }
    [JsonPropertyName("pagingCookie")] public string? PagingCookie { get; set; }
    [JsonPropertyName("count")] public bool Count { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
}

/// <summary>
/// Request DTO for query/complete method.
/// </summary>
public class QueryCompleteRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("cursorOffset")] public int CursorOffset { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}

/// <summary>
/// Request DTO for query/export method.
/// </summary>
public class QueryExportRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("format")] public string Format { get; set; } = "csv";
    [JsonPropertyName("includeHeaders")] public bool IncludeHeaders { get; set; } = true;
    [JsonPropertyName("top")] public int? Top { get; set; }
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
}

/// <summary>
/// Request DTO for query/explain method.
/// </summary>
public class QueryExplainRequest
{
    [JsonPropertyName("sql")] public string Sql { get; set; } = "";
    [JsonPropertyName("environmentUrl")] public string? EnvironmentUrl { get; set; }
}

/// <summary>
/// Request DTO for query/history/list method.
/// </summary>
public class QueryHistoryListRequest
{
    [JsonPropertyName("search")] public string? Search { get; set; }
    [JsonPropertyName("limit")] public int Limit { get; set; } = 50;
}

/// <summary>
/// Request DTO for query/history/delete method.
/// </summary>
public class QueryHistoryDeleteRequest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
}

#endregion
