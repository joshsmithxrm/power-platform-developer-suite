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
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Security;
using PPDS.Dataverse.Services;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using System.Threading;
using StreamJsonRpc;

// Aliases to disambiguate from local DTOs
using PluginTypeInfoModel = PPDS.Cli.Plugins.Registration.PluginTypeInfo;
using PluginImageInfoModel = PPDS.Cli.Plugins.Registration.PluginImageInfo;
using PluginAssemblyInfoModel = PPDS.Cli.Plugins.Registration.PluginAssemblyInfo;
using PluginPackageInfoModel = PPDS.Cli.Plugins.Registration.PluginPackageInfo;
using PluginStepInfoModel = PPDS.Cli.Plugins.Registration.PluginStepInfo;
using ConnRefRelationshipType = PPDS.Dataverse.Services.RelationshipType;
using WebResourceInfoModel = PPDS.Dataverse.Services.WebResourceInfo;

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

    // Discovery cache for env/list — uses Volatile.Read/Write for lock-free thread safety.
    // The list is written BEFORE the expiry so a concurrent reader never sees a new expiry with
    // a stale/null list.
    private List<EnvironmentInfo>? _discoveredEnvCache;
    private long _discoveredEnvCacheExpiry = DateTime.MinValue.Ticks;
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
        // Read expiry first, then list — mirrors the write order (list before expiry)
        // so if we see a non-expired expiry the list is guaranteed to be the matching value.
        var cachedExpiry = Volatile.Read(ref _discoveredEnvCacheExpiry);
        var cachedList = Volatile.Read(ref _discoveredEnvCache);
        if (!forceRefresh && cachedList != null && DateTime.UtcNow.Ticks < cachedExpiry)
        {
            discovered = cachedList;
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
            // Write list BEFORE expiry — a concurrent reader checks expiry first,
            // so it will never see a fresh expiry paired with a stale/null list.
            Volatile.Write(ref _discoveredEnvCache, discovered);
            Volatile.Write(ref _discoveredEnvCacheExpiry, (DateTime.UtcNow + DiscoveryCacheTtl).Ticks);
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

        Volatile.Write(ref _discoveredEnvCache, (List<EnvironmentInfo>?)null); // Invalidate env list cache

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

    #region Plugin Registration Mutations

    /// <summary>
    /// Gets detailed information about a single plugin entity by type and ID.
    /// Supports: assembly, package, type, step, image.
    /// </summary>
    [JsonRpcMethod("plugins/get")]
    public async Task<PluginsGetResponse> PluginsGetAsync(
        string type,
        string id,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var entityId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();

            var query = new QueryExpression(SdkMessage.EntityLogicalName)
            {
                ColumnSet = new ColumnSet(SdkMessage.Fields.Name),
                PageInfo = new PagingInfo { Count = 500, PageNumber = 1 }
            };

            if (!string.IsNullOrWhiteSpace(filter))
            {
                query.Criteria = new FilterExpression
                {
                    Conditions =
                    {
                        new ConditionExpression(SdkMessage.Fields.Name, ConditionOperator.Like,
                            $"%{filter}%")
                    }
                };
            }

            await using var client = await pool.GetClientAsync(cancellationToken: ct);
            var results = await client.RetrieveMultipleAsync(query, ct);

            var messages = results.Entities
                .Select(e => e.GetAttributeValue<string>(SdkMessage.Fields.Name) ?? "")
                .Where(n => !string.IsNullOrEmpty(n))
                .OrderBy(n => n)
                .ToList();

            return new PluginsMessagesResponse { Messages = messages };
        }, cancellationToken);
    }

    /// <summary>
    /// Returns attribute metadata for an entity.
    /// </summary>
    [JsonRpcMethod("plugins/entityAttributes")]
    public async Task<PluginsEntityAttributesResponse> PluginsEntityAttributesAsync(
        string entityLogicalName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entityLogicalName))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entityLogicalName' parameter is required");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await pool.GetClientAsync(cancellationToken: ct);

            var request = new RetrieveEntityRequest
            {
                LogicalName = entityLogicalName.ToLowerInvariant(),
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveEntityResponse)await client.ExecuteAsync(request);

            var attributes = response.EntityMetadata.Attributes
                .Select(a => new AttributeInfoDto
                {
                    LogicalName = a.LogicalName,
                    DisplayName = a.DisplayName?.UserLocalizedLabel?.Label ?? a.LogicalName,
                    AttributeType = a.AttributeType?.ToString() ?? "Unknown"
                })
                .OrderBy(a => a.LogicalName)
                .ToList();

            return new PluginsEntityAttributesResponse { Attributes = attributes };
        }, cancellationToken);
    }

    /// <summary>
    /// Enables or disables a plugin processing step.
    /// </summary>
    [JsonRpcMethod("plugins/toggleStep")]
    public async Task<PluginsToggleStepResponse> PluginsToggleStepAsync(
        string id,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var stepId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(content))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'content' parameter is required");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(content))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'content' parameter is required");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        string pluginTypeId,
        string message,
        string entity,
        string stage,
        string mode = "Synchronous",
        int executionOrder = 1,
        string? filteringAttributes = null,
        string? description = null,
        string? unsecureConfiguration = null,
        string? deployment = null,
        string? runAsUser = null,
        bool? canBeBypassed = null,
        bool? canUseReadOnlyConnection = null,
        string? invocationSource = null,
        bool asyncAutoDelete = false,
        string? secondaryEntity = null,
        string? solutionName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pluginTypeId) || !Guid.TryParse(pluginTypeId, out var typeId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'pluginTypeId' parameter must be a valid GUID");
        if (string.IsNullOrWhiteSpace(message))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'message' parameter is required");
        if (string.IsNullOrWhiteSpace(entity))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'entity' parameter is required");
        if (string.IsNullOrWhiteSpace(stage))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'stage' parameter is required");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

            var messageId = await registrationService.GetSdkMessageIdAsync(message, ct)
                ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"SDK message '{message}' not found");

            var filterId = await registrationService.GetSdkMessageFilterIdAsync(messageId, entity, secondaryEntity, ct);

            var stepConfig = new PluginStepConfig
            {
                Message = message,
                Entity = entity,
                Stage = stage,
                Mode = mode,
                ExecutionOrder = executionOrder,
                FilteringAttributes = filteringAttributes,
                Description = description,
                UnsecureConfiguration = unsecureConfiguration,
                Deployment = deployment,
                RunAsUser = runAsUser,
                CanBeBypassed = canBeBypassed,
                CanUseReadOnlyConnection = canUseReadOnlyConnection,
                InvocationSource = invocationSource,
                AsyncAutoDelete = asyncAutoDelete,
                SecondaryEntity = secondaryEntity
            };

            var stepId = await registrationService.UpsertStepAsync(typeId, stepConfig, messageId, filterId, solutionName, ct);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(stepId) || !Guid.TryParse(stepId, out var parsedStepId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'stepId' parameter must be a valid GUID");
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(imageType))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'imageType' parameter is required");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var stepId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var imageId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var entityId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'type' parameter is required");
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var entityId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithActiveProfileAsync(async (sp, ct) =>
        {
            var pool = sp.GetRequiredService<IDataverseConnectionPool>();
            var registrationService = new PluginRegistrationService(
                pool,
                NullLogger<PluginRegistrationService>.Instance);

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

    #region Metadata Methods

    /// <summary>
    /// Lists all entities in the environment with summary metadata.
    /// Used by the Metadata Browser panel in VS Code.
    /// </summary>
    [JsonRpcMethod("metadata/entities")]
    public async Task<MetadataEntitiesResponse> MetadataEntitiesAsync(
        string? environmentUrl = null,
        bool includeIntersect = false,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataService>();

            // Fetch entities with requested includeIntersect setting
            var entities = await metadataService.GetEntitiesAsync(includeIntersect: includeIntersect, cancellationToken: ct).ConfigureAwait(false);
            var intersectHiddenCount = 0;

            if (!includeIntersect)
            {
                // Fetch total count (with intersect) to compute how many were hidden
                var allEntities = await metadataService.GetEntitiesAsync(includeIntersect: true, cancellationToken: ct).ConfigureAwait(false);
                intersectHiddenCount = allEntities.Count - entities.Count;
            }

            return new MetadataEntitiesResponse
            {
                Entities = entities.Select(MapEntitySummaryToRpc).ToList(),
                IntersectHiddenCount = intersectHiddenCount,
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all global option sets in the environment.
    /// Used by the Metadata Browser panel CHOICES section in VS Code.
    /// </summary>
    [JsonRpcMethod("metadata/globalOptionSets")]
    public async Task<MetadataGlobalOptionSetsResponse> MetadataGlobalOptionSetsAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataService>();
            var optionSets = await metadataService.GetGlobalOptionSetsAsync(cancellationToken: ct).ConfigureAwait(false);

            return new MetadataGlobalOptionSetsResponse
            {
                OptionSets = optionSets.Select(MapOptionSetSummaryToRpc).ToList(),
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the full details of a specific global option set including all values.
    /// Used when a global choice is selected in the Metadata Browser CHOICES tree.
    /// </summary>
    [JsonRpcMethod("metadata/globalOptionSet")]
    public async Task<MetadataGlobalOptionSetDetailResponse> MetadataGlobalOptionSetAsync(
        string name,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'name' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataService>();
            var optionSet = await metadataService.GetOptionSetAsync(name, ct).ConfigureAwait(false);

            return new MetadataGlobalOptionSetDetailResponse
            {
                OptionSet = MapOptionSetToRpc(optionSet),
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets full metadata for a specific entity including attributes, relationships,
    /// keys, privileges, and optionally global option set values.
    /// Used by the Metadata Browser panel in VS Code.
    /// </summary>
    [JsonRpcMethod("metadata/entity")]
    public async Task<MetadataEntityResponse> MetadataEntityAsync(
        string logicalName,
        bool includeGlobalOptionSets = false,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'logicalName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var metadataService = sp.GetRequiredService<IMetadataService>();
            var (entity, globalOptionSets) = await metadataService.GetEntityWithGlobalOptionSetsAsync(
                logicalName,
                includeGlobalOptionSets,
                ct);

            return new MetadataEntityResponse
            {
                Entity = MapEntityDetailToRpc(entity, globalOptionSets)
            };
        }, cancellationToken);
    }

    private static MetadataEntitySummaryDto MapEntitySummaryToRpc(EntitySummary e)
    {
        return new MetadataEntitySummaryDto
        {
            LogicalName = e.LogicalName,
            SchemaName = e.SchemaName,
            DisplayName = e.DisplayName,
            IsCustomEntity = e.IsCustomEntity,
            IsManaged = e.IsManaged,
            OwnershipType = e.OwnershipType,
            ObjectTypeCode = e.ObjectTypeCode,
            Description = e.Description
        };
    }

    private static MetadataEntityDetailDto MapEntityDetailToRpc(
        EntityMetadataDto entity,
        IReadOnlyList<OptionSetMetadataDto> globalOptionSets)
    {
        return new MetadataEntityDetailDto
        {
            LogicalName = entity.LogicalName,
            SchemaName = entity.SchemaName,
            DisplayName = entity.DisplayName,
            IsCustomEntity = entity.IsCustomEntity,
            IsManaged = entity.IsManaged,
            OwnershipType = entity.OwnershipType,
            ObjectTypeCode = entity.ObjectTypeCode,
            Description = entity.Description,
            PrimaryIdAttribute = entity.PrimaryIdAttribute,
            PrimaryNameAttribute = entity.PrimaryNameAttribute,
            EntitySetName = entity.EntitySetName,
            IsActivity = entity.IsActivity,
            Attributes = entity.Attributes.Select(MapAttributeToRpc).ToList(),
            OneToManyRelationships = entity.OneToManyRelationships.Select(MapRelationshipToRpc).ToList(),
            ManyToOneRelationships = entity.ManyToOneRelationships.Select(MapRelationshipToRpc).ToList(),
            ManyToManyRelationships = entity.ManyToManyRelationships.Select(MapManyToManyToRpc).ToList(),
            Keys = entity.Keys.Select(MapKeyToRpc).ToList(),
            Privileges = entity.Privileges.Select(MapPrivilegeToRpc).ToList(),
            GlobalOptionSets = globalOptionSets.Select(MapOptionSetToRpc).ToList()
        };
    }

    private static MetadataAttributeDto MapAttributeToRpc(AttributeMetadataDto a)
    {
        return new MetadataAttributeDto
        {
            LogicalName = a.LogicalName,
            DisplayName = a.DisplayName,
            SchemaName = a.SchemaName,
            AttributeType = a.AttributeType,
            AttributeTypeName = a.AttributeTypeName,
            IsPrimaryId = a.IsPrimaryId,
            IsPrimaryName = a.IsPrimaryName,
            IsCustomAttribute = a.IsCustomAttribute,
            RequiredLevel = a.RequiredLevel,
            MaxLength = a.MaxLength,
            MinValue = a.MinValue,
            MaxValue = a.MaxValue,
            Precision = a.Precision,
            Targets = a.Targets,
            OptionSetName = a.OptionSetName,
            IsGlobalOptionSet = a.IsGlobalOptionSet,
            Options = a.Options?.Select(MapOptionValueToRpc).ToList(),
            Format = a.Format,
            DateTimeBehavior = a.DateTimeBehavior,
            SourceType = a.SourceType,
            IsSecured = a.IsSecured,
            Description = a.Description,
            AutoNumberFormat = a.AutoNumberFormat
        };
    }

    private static MetadataRelationshipDto MapRelationshipToRpc(RelationshipMetadataDto r)
    {
        return new MetadataRelationshipDto
        {
            SchemaName = r.SchemaName,
            RelationshipType = r.RelationshipType,
            ReferencedEntity = r.ReferencedEntity,
            ReferencedAttribute = r.ReferencedAttribute,
            ReferencingEntity = r.ReferencingEntity,
            ReferencingAttribute = r.ReferencingAttribute,
            CascadeAssign = r.CascadeAssign,
            CascadeDelete = r.CascadeDelete,
            CascadeMerge = r.CascadeMerge,
            CascadeReparent = r.CascadeReparent,
            CascadeShare = r.CascadeShare,
            CascadeUnshare = r.CascadeUnshare,
            IsHierarchical = r.IsHierarchical
        };
    }

    private static MetadataManyToManyDto MapManyToManyToRpc(ManyToManyRelationshipDto r)
    {
        return new MetadataManyToManyDto
        {
            SchemaName = r.SchemaName,
            Entity1LogicalName = r.Entity1LogicalName,
            Entity1IntersectAttribute = r.Entity1IntersectAttribute,
            Entity2LogicalName = r.Entity2LogicalName,
            Entity2IntersectAttribute = r.Entity2IntersectAttribute,
            IntersectEntityName = r.IntersectEntityName
        };
    }

    private static MetadataKeyDto MapKeyToRpc(EntityKeyDto k)
    {
        return new MetadataKeyDto
        {
            SchemaName = k.SchemaName,
            LogicalName = k.LogicalName,
            DisplayName = k.DisplayName,
            KeyAttributes = k.KeyAttributes,
            EntityKeyIndexStatus = k.EntityKeyIndexStatus,
            IsManaged = k.IsManaged
        };
    }

    private static MetadataPrivilegeDto MapPrivilegeToRpc(PrivilegeDto p)
    {
        return new MetadataPrivilegeDto
        {
            PrivilegeId = p.PrivilegeId,
            Name = p.Name,
            PrivilegeType = p.PrivilegeType,
            CanBeLocal = p.CanBeLocal,
            CanBeDeep = p.CanBeDeep,
            CanBeGlobal = p.CanBeGlobal,
            CanBeBasic = p.CanBeBasic
        };
    }

    private static MetadataOptionSetDto MapOptionSetToRpc(OptionSetMetadataDto os)
    {
        return new MetadataOptionSetDto
        {
            Name = os.Name,
            DisplayName = os.DisplayName,
            OptionSetType = os.OptionSetType,
            IsGlobal = os.IsGlobal,
            Options = os.Options.Select(MapOptionValueToRpc).ToList()
        };
    }

    private static MetadataGlobalChoiceSummaryDto MapOptionSetSummaryToRpc(OptionSetSummary os)
    {
        return new MetadataGlobalChoiceSummaryDto
        {
            Name = os.Name,
            DisplayName = os.DisplayName,
            OptionSetType = os.OptionSetType,
            IsCustomOptionSet = os.IsCustomOptionSet,
            IsManaged = os.IsManaged,
            OptionCount = os.OptionCount,
            Description = os.Description,
        };
    }

    private static MetadataOptionValueDto MapOptionValueToRpc(OptionValueDto o)
    {
        return new MetadataOptionValueDto
        {
            Value = o.Value,
            Label = o.Label,
            Color = o.Color,
            Description = o.Description
        };
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

            var mapped = MapToResponse(result, query);
            mapped.QueryMode = "dataverse";
            return mapped;
        }, cancellationToken);

        // Auto-save to history (fire-and-forget)
        FireAndForgetHistorySave(request.FetchXml, response);

        return response;
    }

    /// <summary>
    /// Executes a SQL query against Dataverse by transpiling to FetchXML.
    /// Delegates to <see cref="SqlQueryService"/> for the shared transpile-and-execute pipeline.
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

        // Execute (or transpile-only) via SqlQueryService from the environment's service provider
        var response = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, profile, env, ct) =>
        {
            var service = sp.GetRequiredService<ISqlQueryService>();

            // ShowFetchXml mode: transpile only, no execution needed
            if (request.ShowFetchXml)
            {
                try
                {
                    var fetchXml = service.TranspileSql(request.Sql, request.Top);
                    return new QueryResultResponse
                    {
                        Success = true,
                        ExecutedFetchXml = fetchXml,
                        QueryMode = "dataverse"
                    };
                }
                catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
                {
                    throw new RpcException(ErrorCodes.Query.ParseError, ex.Message);
                }
            }

            // Wire cross-environment support and DML safety (mirrors InteractiveSession pattern)
            if (service is SqlQueryService concrete)
            {
                var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
                configStore.ClearCache();
                var configCollection = await configStore.LoadAsync(ct);

                // ProfileResolutionService rejects duplicate labels — gracefully degrade
                // to no cross-env support if the user's config has duplicates
                try
                {
                    var profileResolution = new ProfileResolutionService(configCollection.Environments);

                    concrete.RemoteExecutorFactory = label =>
                    {
                        var config = profileResolution.ResolveByLabel(label);
                        if (config?.Url == null) return null;
#pragma warning disable PPDS012
                        // Planner calls this factory synchronously; on cache hit the task is
                        // already completed so .GetResult() is free.  On first cache miss the
                        // pool creation performs async I/O (auth / device-code).  Wrapping in
                        // Task.Run avoids blocking the RPC handler's async context thread,
                        // which would be a thread-pool starvation risk under concurrent requests.
                        var remoteProvider = Task.Run(() => _poolManager.GetOrCreateServiceProviderAsync(
                            new[] { profile.Name ?? profile.DisplayIdentifier },
                            config.Url,
                            deviceCodeCallback: DaemonDeviceCodeHandler.CreateCallback(_rpc),
                            cancellationToken: ct)).GetAwaiter().GetResult();
#pragma warning restore PPDS012
                        return remoteProvider.GetRequiredService<IQueryExecutor>();
                    };

                    concrete.ProfileResolver = profileResolution;
                }
                catch (ArgumentException)
                {
                    // Duplicate labels or reserved label — cross-env queries won't work
                    // but single-env queries proceed normally
                    _logger.LogWarning("Environment config has duplicate or reserved labels — cross-environment queries disabled");
                }

                var envConfig = await configStore.GetConfigAsync(env.Url, ct);
                if (envConfig != null)
                {
                    concrete.EnvironmentSafetySettings = envConfig.SafetySettings;
                    var envType = envConfig.Type ?? PPDS.Auth.Profiles.EnvironmentType.Unknown;
                    concrete.EnvironmentProtectionLevel = envConfig.Protection
                        ?? DmlSafetyGuard.DetectProtectionLevel(envType);
                }
            }

            // Build the shared request
            var sqlRequest = new SqlQueryRequest
            {
                Sql = request.Sql,
                TopOverride = request.Top,
                PageNumber = request.Page,
                PagingCookie = request.PagingCookie,
                IncludeCount = request.Count,
                UseTdsEndpoint = request.UseTds,
                DmlSafety = request.DmlSafety != null
                    ? new DmlSafetyOptions
                    {
                        IsConfirmed = request.DmlSafety.IsConfirmed,
                        IsDryRun = request.DmlSafety.IsDryRun,
                        NoLimit = request.DmlSafety.NoLimit,
                        RowCap = request.DmlSafety.RowCap,
                    }
                    : null,
            };

            try
            {
                var result = await service.ExecuteAsync(sqlRequest, ct);
                var mapped = MapToResponse(result.Result, result.TranspiledFetchXml);
                mapped.QueryMode = result.ExecutionMode switch
                {
                    QueryExecutionMode.Tds => "tds",
                    _ => "dataverse"
                };

                if (result.DataSources is { Count: > 1 })
                {
                    mapped.DataSources = result.DataSources
                        .Select(ds => new QueryDataSourceDto { Label = ds.Label, IsRemote = ds.IsRemote })
                        .ToList();
                }

                if (result.AppliedHints is { Count: > 0 })
                {
                    mapped.AppliedHints = result.AppliedHints.ToList();
                }

                return mapped;
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
            {
                throw new RpcException(ErrorCodes.Query.ParseError, ex.Message);
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.DmlConfirmationRequired)
            {
                throw new RpcException(
                    ErrorCodes.Query.DmlConfirmationRequired,
                    ex.Message,
                    new DmlSafetyErrorData
                    {
                        Code = ErrorCodes.Query.DmlConfirmationRequired,
                        Message = ex.Message,
                        DmlConfirmationRequired = true,
                    });
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.DmlBlocked)
            {
                throw new RpcException(
                    ErrorCodes.Query.DmlBlocked,
                    ex.Message,
                    new DmlSafetyErrorData
                    {
                        Code = ErrorCodes.Query.DmlBlocked,
                        Message = ex.Message,
                        DmlBlocked = true,
                    });
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.TdsIncompatible)
            {
                throw new RpcException(ErrorCodes.Query.TdsIncompatible, ex.Message);
            }
            catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.TdsConnectionFailed)
            {
                throw new RpcException(ErrorCodes.Query.TdsConnectionFailed, ex.Message);
            }
            catch (PpdsException ex)
            {
                throw new RpcException(ErrorCodes.Query.ExecutionFailed, ex.Message);
            }
        }, cancellationToken);

        // Auto-save to history (fire-and-forget)
        FireAndForgetHistorySave(request.Sql, response);

        return response;
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
        if (string.IsNullOrWhiteSpace(request.Sql) && string.IsNullOrWhiteSpace(request.FetchXml))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "Either 'sql' or 'fetchXml' parameter is required");
        }

        var format = request.Format.ToLowerInvariant();
        if (format is not ("csv" or "tsv" or "json"))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidArguments,
                $"Invalid format '{format}'. Valid values: csv, tsv, json");
        }

        // Execute the query
        const int MaxExportRecords = 100_000;

        var queryResponse = await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
        {
            // Use FetchXML directly if provided, otherwise transpile SQL via SqlQueryService
            string fetchXml;
            if (!string.IsNullOrWhiteSpace(request.FetchXml))
            {
                fetchXml = request.FetchXml;
            }
            else
            {
                var sqlService = sp.GetRequiredService<ISqlQueryService>();
                try
                {
                    fetchXml = sqlService.TranspileSql(request.Sql, request.Top);
                }
                catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
                {
                    throw new RpcException(ErrorCodes.Query.ParseError, ex.Message);
                }
            }

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
    /// Returns the execution plan for a SQL query.
    /// Builds a full plan tree showing node types, descriptions, and estimated row counts.
    /// Falls back to transpiled FetchXML if plan building fails.
    /// </summary>
    [JsonRpcMethod("query/explain")]
    public async Task<QueryExplainResponse> QueryExplainAsync(
        QueryExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Sql))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'sql' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(request.EnvironmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<ISqlQueryService>();

            try
            {
                var description = await service.ExplainAsync(request.Sql, ct);
                var formatted = Tui.Components.QueryPlanView.FormatPlanTree(description);
                var fetchXml = service.TranspileSql(request.Sql);

                return new QueryExplainResponse
                {
                    Plan = formatted + "\n\n--- FetchXML ---\n" + fetchXml,
                    Format = "text",
                    FetchXml = fetchXml
                };
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Fall back to just the transpiled FetchXML if plan building fails
                try
                {
                    var fetchXml = service.TranspileSql(request.Sql);
                    return new QueryExplainResponse
                    {
                        Plan = fetchXml,
                        Format = "fetchxml",
                        FetchXml = fetchXml
                    };
                }
                catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.ParseError)
                {
                    throw new RpcException(ErrorCodes.Query.ParseError, ex.Message);
                }
            }
        }, cancellationToken);
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
            var result = await solutionService.ListAsync(filter, includeManaged, cancellationToken: ct);

            return new SolutionsListResponse
            {
                TotalCount = result.TotalCount,
                FiltersApplied = result.FiltersApplied.ToList(),
                Solutions = result.Items.Select(s => new SolutionInfoDto
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
                    InstalledOn = s.InstalledOn,
                    IsVisible = s.IsVisible,
                    IsApiManaged = s.IsApiManaged
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

    #region Import Jobs

    /// <summary>
    /// Lists import jobs for an environment.
    /// Maps to: ppds importjobs list --json
    /// </summary>
    [JsonRpcMethod("importJobs/list")]
    public async Task<ImportJobsListResponse> ImportJobsListAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var importJobService = sp.GetRequiredService<IImportJobService>();
            var result = await importJobService.ListAsync(cancellationToken: ct);

            return new ImportJobsListResponse
            {
                TotalCount = result.TotalCount,
                Jobs = result.Items.Select(MapImportJobToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single import job with full detail including XML data.
    /// Maps to: ppds importjobs get + ppds importjobs data
    /// </summary>
    [JsonRpcMethod("importJobs/get")]
    public async Task<ImportJobsGetResponse> ImportJobsGetAsync(
        string id,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var importJobId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var importJobService = sp.GetRequiredService<IImportJobService>();

            var job = await importJobService.GetAsync(importJobId, ct)
                ?? throw new RpcException(
                    ErrorCodes.Operation.NotFound,
                    $"Import job '{id}' not found");

            var data = await importJobService.GetDataAsync(importJobId, ct);

            return new ImportJobsGetResponse
            {
                Job = MapImportJobToDetailDto(job, data)
            };
        }, cancellationToken);
    }

    private static ImportJobInfoDto MapImportJobToDto(ImportJobInfo job)
    {
        return new ImportJobInfoDto
        {
            Id = job.Id.ToString(),
            SolutionName = job.SolutionName,
            Status = job.Status,
            Progress = job.Progress,
            CreatedBy = job.CreatedByName,
            CreatedOn = job.CreatedOn?.ToString("o"),
            StartedOn = job.StartedOn?.ToString("o"),
            CompletedOn = job.CompletedOn?.ToString("o"),
            Duration = job.FormattedDuration,
            OperationContext = job.OperationContext
        };
    }

    private static ImportJobDetailDto MapImportJobToDetailDto(ImportJobInfo job, string? data)
    {
        return new ImportJobDetailDto
        {
            Id = job.Id.ToString(),
            SolutionName = job.SolutionName,
            Status = job.Status,
            Progress = job.Progress,
            CreatedBy = job.CreatedByName,
            CreatedOn = job.CreatedOn?.ToString("o"),
            StartedOn = job.StartedOn?.ToString("o"),
            CompletedOn = job.CompletedOn?.ToString("o"),
            Duration = job.FormattedDuration,
            OperationContext = job.OperationContext,
            Data = data
        };
    }

    #endregion

    #region Plugin Traces

    /// <summary>
    /// Lists plugin trace logs with optional filtering.
    /// Maps to: ppds plugintraces list --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/list")]
    public async Task<PluginTracesListResponse> PluginTracesListAsync(
        TraceFilterDto? filter = null,
        int top = 100,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var serviceFilter = MapTraceFilterFromDto(filter);
            var result = await traceService.ListAsync(serviceFilter, top, ct);

            return new PluginTracesListResponse
            {
                TotalCount = result.TotalCount,
                Traces = result.Items.Select(MapTraceInfoToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single plugin trace with full details.
    /// Maps to: ppds plugintraces get --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/get")]
    public async Task<PluginTracesGetResponse> PluginTracesGetAsync(
        string id,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var traceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var trace = await traceService.GetAsync(traceId, ct)
                ?? throw new RpcException(
                    ErrorCodes.Operation.NotFound,
                    $"Plugin trace '{id}' not found");

            return new PluginTracesGetResponse
            {
                Trace = MapTraceDetailToDto(trace)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Builds a timeline hierarchy from traces with the given correlation ID.
    /// Maps to: ppds plugintraces timeline --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/timeline")]
    public async Task<PluginTracesTimelineResponse> PluginTracesTimelineAsync(
        string correlationId,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || !Guid.TryParse(correlationId, out var corrId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'correlationId' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var nodes = await traceService.BuildTimelineAsync(corrId, ct);

            return new PluginTracesTimelineResponse
            {
                Nodes = nodes.Select(MapTimelineNodeToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes plugin traces by IDs, by age, or by filter.
    /// Maps to: ppds plugintraces delete --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/delete")]
    public async Task<PluginTracesDeleteResponse> PluginTracesDeleteAsync(
        string[]? ids = null,
        int? olderThanDays = null,
        TraceFilterDto? filter = null,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        int modeCount = (ids != null && ids.Length > 0 ? 1 : 0)
            + (olderThanDays.HasValue ? 1 : 0)
            + (filter != null ? 1 : 0);
        if (modeCount == 0)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "One of 'ids', 'olderThanDays', or 'filter' must be provided");
        if (modeCount > 1)
            throw new RpcException(ErrorCodes.Validation.RequiredField, "Only one of 'ids', 'olderThanDays', or 'filter' may be provided per call");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            int deletedCount;

            if (ids != null && ids.Length > 0)
            {
                var guids = new List<Guid>(ids.Length);
                foreach (var idStr in ids)
                {
                    if (!Guid.TryParse(idStr, out var guid))
                    {
                        throw new RpcException(
                            ErrorCodes.Validation.InvalidValue,
                            $"The ID '{idStr}' is not a valid GUID");
                    }
                    guids.Add(guid);
                }

                deletedCount = await traceService.DeleteByIdsAsync(guids, cancellationToken: ct);
            }
            else if (olderThanDays != null)
            {
                var olderThan = TimeSpan.FromDays(olderThanDays.Value);
                deletedCount = await traceService.DeleteOlderThanAsync(olderThan, cancellationToken: ct);
            }
            else
            {
                var domainFilter = MapTraceFilterFromDto(filter) ?? new PluginTraceFilter();
                deletedCount = await traceService.DeleteByFilterAsync(domainFilter, cancellationToken: ct);
            }

            return new PluginTracesDeleteResponse
            {
                DeletedCount = deletedCount
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the current plugin trace logging level.
    /// Maps to: ppds plugintraces tracelevel --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/traceLevel")]
    public async Task<PluginTracesTraceLevelResponse> PluginTracesTraceLevelAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            var settings = await traceService.GetSettingsAsync(ct);

            return new PluginTracesTraceLevelResponse
            {
                Level = settings.SettingName,
                LevelValue = (int)settings.Setting
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Sets the plugin trace logging level.
    /// Maps to: ppds plugintraces settracelevel --json
    /// </summary>
    [JsonRpcMethod("pluginTraces/setTraceLevel")]
    public async Task<PluginTracesSetTraceLevelResponse> PluginTracesSetTraceLevelAsync(
        string level,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'level' parameter is required");
        }

        if (!Enum.TryParse<PluginTraceLogSetting>(level, ignoreCase: true, out var setting))
        {
            throw new RpcException(
                ErrorCodes.Validation.InvalidValue,
                $"Invalid trace level '{level}'. Valid values are: Off, Exception, All");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var traceService = sp.GetRequiredService<IPluginTraceService>();
            await traceService.SetSettingsAsync(setting, ct);

            return new PluginTracesSetTraceLevelResponse
            {
                Success = true
            };
        }, cancellationToken);
    }

    // ── Plugin Traces mapper helpers ────────────────────────────────────────

    private static PluginTraceFilter? MapTraceFilterFromDto(TraceFilterDto? dto)
    {
        if (dto == null) return null;

        PluginTraceMode? mode = null;
        if (dto.Mode != null && Enum.TryParse<PluginTraceMode>(dto.Mode, ignoreCase: true, out var parsedMode))
        {
            mode = parsedMode;
        }

        return new PluginTraceFilter
        {
            TypeName = dto.TypeName,
            MessageName = dto.MessageName,
            PrimaryEntity = dto.PrimaryEntity,
            Mode = mode,
            HasException = dto.HasException,
            CorrelationId = dto.CorrelationId != null && Guid.TryParse(dto.CorrelationId, out var corrId) ? corrId : null,
            MinDurationMs = dto.MinDurationMs,
            CreatedAfter = dto.StartDate != null && DateTime.TryParse(dto.StartDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var startDate) ? startDate : null,
            CreatedBefore = dto.EndDate != null && DateTime.TryParse(dto.EndDate, null, System.Globalization.DateTimeStyles.RoundtripKind, out var endDate) ? endDate : null
        };
    }

    private static PluginTraceInfoDto MapTraceInfoToDto(PluginTraceInfo trace)
    {
        return new PluginTraceInfoDto
        {
            Id = trace.Id.ToString(),
            TypeName = trace.TypeName,
            MessageName = trace.MessageName,
            PrimaryEntity = trace.PrimaryEntity,
            Mode = trace.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
            OperationType = trace.OperationType.ToString(),
            Depth = trace.Depth,
            CreatedOn = trace.CreatedOn.ToString("o"),
            DurationMs = trace.DurationMs,
            HasException = trace.HasException,
            CorrelationId = trace.CorrelationId?.ToString()
        };
    }

    private static PluginTraceDetailDto MapTraceDetailToDto(PluginTraceDetail detail)
    {
        return new PluginTraceDetailDto
        {
            Id = detail.Id.ToString(),
            TypeName = detail.TypeName,
            MessageName = detail.MessageName,
            PrimaryEntity = detail.PrimaryEntity,
            Mode = detail.Mode == PluginTraceMode.Synchronous ? "Sync" : "Async",
            OperationType = detail.OperationType.ToString(),
            Depth = detail.Depth,
            CreatedOn = detail.CreatedOn.ToString("o"),
            DurationMs = detail.DurationMs,
            HasException = detail.HasException,
            CorrelationId = detail.CorrelationId?.ToString(),
            ConstructorDurationMs = detail.ConstructorDurationMs,
            ExecutionStartTime = detail.ExecutionStartTime?.ToString("o"),
            ExceptionDetails = detail.ExceptionDetails,
            MessageBlock = detail.MessageBlock,
            Configuration = detail.Configuration,
            SecureConfiguration = detail.SecureConfiguration,
            RequestId = detail.RequestId?.ToString(),
            // Additional fields (PT-01 through PT-09)
            Stage = detail.OperationType.ToString(),
            ConstructorStartTime = detail.ConstructorStartTime?.ToString("o"),
            IsSystemCreated = detail.IsSystemCreated,
            CreatedById = detail.CreatedById?.ToString(),
            CreatedOnBehalfById = detail.CreatedOnBehalfById?.ToString(),
            PluginStepId = detail.PluginStepId?.ToString(),
            PersistenceKey = detail.PersistenceKey?.ToString(),
            OrganizationId = detail.OrganizationId?.ToString(),
            Profile = detail.Profile
        };
    }

    private static TimelineNodeDto MapTimelineNodeToDto(TimelineNode node)
    {
        return new TimelineNodeDto
        {
            TraceId = node.Trace.Id.ToString(),
            TypeName = node.Trace.TypeName,
            MessageName = node.Trace.MessageName,
            Depth = node.Trace.Depth,
            DurationMs = node.Trace.DurationMs,
            HasException = node.Trace.HasException,
            OffsetPercent = node.OffsetPercent,
            WidthPercent = node.WidthPercent,
            HierarchyDepth = node.HierarchyDepth,
            Children = node.Children.Select(MapTimelineNodeToDto).ToList()
        };
    }

    #endregion

    #region Connection References

    /// <summary>
    /// Lists connection references, optionally filtered by solution.
    /// Enriches with connection status from Power Platform API (graceful degradation for SPN).
    /// Maps to: ppds connref list --json
    /// </summary>
    [JsonRpcMethod("connectionReferences/list")]
    public async Task<ConnectionReferencesListResponse> ConnectionReferencesListAsync(
        string? solutionId = null,
        string? environmentUrl = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();
            var result = await connRefService.ListAsync(solutionName: solutionId, includeInactive: includeInactive, cancellationToken: ct);

            // Try to enrich with connection status from Power Platform API
            Dictionary<string, ConnectionInfo>? connectionMap = null;
            try
            {
                var connectionService = sp.GetRequiredService<IConnectionService>();
                var connections = await connectionService.ListAsync(cancellationToken: ct);
                connectionMap = connections.ToDictionary(c => c.ConnectionId, c => c);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to retrieve connection status (SPN may not have access to Connections API). Continuing without connection enrichment.");
            }

            return new ConnectionReferencesListResponse
            {
                TotalCount = result.TotalCount,
                FiltersApplied = result.FiltersApplied.ToList(),
                References = result.Items.Select(r => MapConnectionReferenceToDto(r, connectionMap)).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single connection reference with dependent flows and connection details.
    /// Maps to: ppds connref get --json
    /// </summary>
    [JsonRpcMethod("connectionReferences/get")]
    public async Task<ConnectionReferencesGetResponse> ConnectionReferencesGetAsync(
        string logicalName,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(logicalName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'logicalName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();

            var reference = await connRefService.GetAsync(logicalName, ct)
                ?? throw new RpcException(
                    ErrorCodes.Operation.NotFound,
                    $"Connection reference '{logicalName}' not found");

            var flows = await connRefService.GetFlowsUsingAsync(logicalName, ct);

            // Try to get connection details (graceful degradation for SPN)
            ConnectionInfo? connectionInfo = null;
            if (!string.IsNullOrEmpty(reference.ConnectionId))
            {
                try
                {
                    var connectionService = sp.GetRequiredService<IConnectionService>();
                    connectionInfo = await connectionService.GetAsync(reference.ConnectionId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unable to retrieve connection details for '{ConnectionId}' (SPN may not have access). Continuing without connection enrichment.", reference.ConnectionId);
                }
            }

            return new ConnectionReferencesGetResponse
            {
                Reference = MapConnectionReferenceToDetailDto(reference, flows, connectionInfo)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Analyzes connection references for orphaned references and flows.
    /// Maps to: ppds connref analyze --json
    /// </summary>
    [JsonRpcMethod("connectionReferences/analyze")]
    public async Task<ConnectionReferencesAnalyzeResponse> ConnectionReferencesAnalyzeAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var connRefService = sp.GetRequiredService<IConnectionReferenceService>();
            var analysis = await connRefService.AnalyzeAsync(cancellationToken: ct);

            var orphanedReferences = analysis.Relationships
                .Where(r => r.Type == ConnRefRelationshipType.OrphanedConnectionReference)
                .Select(r => new OrphanedReferenceDto
                {
                    LogicalName = r.ConnectionReferenceLogicalName ?? "",
                    DisplayName = r.ConnectionReferenceDisplayName,
                    ConnectorId = r.ConnectorId
                })
                .ToList();

            var orphanedFlows = analysis.Relationships
                .Where(r => r.Type == ConnRefRelationshipType.OrphanedFlow)
                .Select(r => new OrphanedFlowDto
                {
                    UniqueName = r.FlowUniqueName ?? "",
                    DisplayName = r.FlowDisplayName,
                    MissingReference = r.ConnectionReferenceLogicalName
                })
                .ToList();

            return new ConnectionReferencesAnalyzeResponse
            {
                OrphanedReferences = orphanedReferences,
                OrphanedFlows = orphanedFlows,
                TotalReferences = analysis.Relationships.Count(r =>
                    r.Type == ConnRefRelationshipType.OrphanedConnectionReference ||
                    r.Type == ConnRefRelationshipType.FlowToConnectionReference),
                TotalFlows = analysis.Relationships.Count(r =>
                    r.Type == ConnRefRelationshipType.OrphanedFlow ||
                    r.Type == ConnRefRelationshipType.FlowToConnectionReference)
            };
        }, cancellationToken);
    }

    private static ConnectionReferenceInfoDto MapConnectionReferenceToDto(
        ConnectionReferenceInfo r,
        Dictionary<string, ConnectionInfo>? connectionMap)
    {
        ConnectionInfo? connInfo = null;
        if (!string.IsNullOrEmpty(r.ConnectionId) && connectionMap != null)
        {
            connectionMap.TryGetValue(r.ConnectionId, out connInfo);
        }

        return new ConnectionReferenceInfoDto
        {
            LogicalName = r.LogicalName,
            DisplayName = r.DisplayName,
            ConnectorId = r.ConnectorId,
            ConnectionId = r.ConnectionId,
            IsManaged = r.IsManaged,
            ModifiedOn = r.ModifiedOn?.ToString("o"),
            ConnectionStatus = connInfo?.Status.ToString() ?? "N/A",
            ConnectorDisplayName = connInfo?.ConnectorDisplayName
        };
    }

    private static ConnectionReferenceDetailDto MapConnectionReferenceToDetailDto(
        ConnectionReferenceInfo r,
        List<FlowInfo> flows,
        ConnectionInfo? connectionInfo)
    {
        return new ConnectionReferenceDetailDto
        {
            LogicalName = r.LogicalName,
            DisplayName = r.DisplayName,
            ConnectorId = r.ConnectorId,
            ConnectionId = r.ConnectionId,
            IsManaged = r.IsManaged,
            ModifiedOn = r.ModifiedOn?.ToString("o"),
            ConnectionStatus = connectionInfo?.Status.ToString() ?? "N/A",
            ConnectorDisplayName = connectionInfo?.ConnectorDisplayName,
            Description = r.Description,
            IsBound = r.IsBound,
            CreatedOn = r.CreatedOn?.ToString("o"),
            Flows = flows.Select(f => new FlowReferenceDto
            {
                FlowId = f.Id.ToString(),
                UniqueName = f.UniqueName,
                DisplayName = f.DisplayName,
                State = f.State.ToString()
            }).ToList(),
            ConnectionOwner = connectionInfo?.CreatedBy,
            ConnectionIsShared = connectionInfo?.IsShared
        };
    }

    #endregion

    #region Environment Variables

    /// <summary>
    /// Lists environment variable definitions with current values.
    /// Maps to: ppds envvar list --json
    /// </summary>
    [JsonRpcMethod("environmentVariables/list")]
    public async Task<EnvironmentVariablesListResponse> EnvironmentVariablesListAsync(
        string? solutionId = null,
        string? environmentUrl = null,
        bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var envVarService = sp.GetRequiredService<IEnvironmentVariableService>();
            var result = await envVarService.ListAsync(solutionName: solutionId, includeInactive: includeInactive, cancellationToken: ct);

            return new EnvironmentVariablesListResponse
            {
                TotalCount = result.TotalCount,
                FiltersApplied = result.FiltersApplied.ToList(),
                Variables = result.Items.Select(MapEnvironmentVariableToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single environment variable detail.
    /// Maps to: ppds envvar get --json
    /// </summary>
    [JsonRpcMethod("environmentVariables/get")]
    public async Task<EnvironmentVariablesGetResponse> EnvironmentVariablesGetAsync(
        string schemaName,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'schemaName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var envVarService = sp.GetRequiredService<IEnvironmentVariableService>();

            var variable = await envVarService.GetAsync(schemaName, ct)
                ?? throw new RpcException(
                    ErrorCodes.Operation.NotFound,
                    $"Environment variable '{schemaName}' not found");

            return new EnvironmentVariablesGetResponse
            {
                Variable = MapEnvironmentVariableToDetailDto(variable)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Sets an environment variable value.
    /// Maps to: ppds envvar set --json
    /// </summary>
    [JsonRpcMethod("environmentVariables/set")]
    public async Task<EnvironmentVariablesSetResponse> EnvironmentVariablesSetAsync(
        string schemaName,
        string value,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'schemaName' parameter is required");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'value' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var envVarService = sp.GetRequiredService<IEnvironmentVariableService>();
            var success = await envVarService.SetValueAsync(schemaName, value, ct);

            return new EnvironmentVariablesSetResponse
            {
                Success = success
            };
        }, cancellationToken);
    }

    private static readonly System.Text.Json.JsonSerializerOptions DeploymentSettingsReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly System.Text.Json.JsonSerializerOptions DeploymentSettingsWriteOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Syncs a deployment settings file with the current solution state.
    /// Reads an existing file if present, merges with current environment, and writes the result.
    /// </summary>
    [JsonRpcMethod("environmentVariables/syncDeploymentSettings")]
    public async Task<EnvironmentVariablesSyncDeploymentSettingsResponse> EnvironmentVariablesSyncDeploymentSettingsAsync(
        string solutionId,
        string filePath,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(solutionId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'solutionId' parameter is required");
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'filePath' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var settingsService = sp.GetRequiredService<IDeploymentSettingsService>();

            // Load existing settings if file already exists
            DeploymentSettingsFile? existingSettings = null;
            var fullPath = System.IO.Path.GetFullPath(filePath);

            if (System.IO.File.Exists(fullPath))
            {
                var existingJson = await System.IO.File.ReadAllTextAsync(fullPath, ct);
                existingSettings = System.Text.Json.JsonSerializer.Deserialize<DeploymentSettingsFile>(
                    existingJson, DeploymentSettingsReadOptions);
            }

            var result = await settingsService.SyncAsync(solutionId, existingSettings, ct);

            // Write the synced file
            var json = System.Text.Json.JsonSerializer.Serialize(result.Settings, DeploymentSettingsWriteOptions);
            var directory = System.IO.Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }
            await System.IO.File.WriteAllTextAsync(fullPath, json, ct);

            return new EnvironmentVariablesSyncDeploymentSettingsResponse
            {
                FilePath = fullPath,
                EnvironmentVariables = new SyncStatisticsDto
                {
                    Added = result.EnvironmentVariables.Added,
                    Removed = result.EnvironmentVariables.Removed,
                    Preserved = result.EnvironmentVariables.Preserved
                },
                ConnectionReferences = new SyncStatisticsDto
                {
                    Added = result.ConnectionReferences.Added,
                    Removed = result.ConnectionReferences.Removed,
                    Preserved = result.ConnectionReferences.Preserved
                }
            };
        }, cancellationToken);
    }

    private static EnvironmentVariableInfoDto MapEnvironmentVariableToDto(EnvironmentVariableInfo v)
    {
        return new EnvironmentVariableInfoDto
        {
            SchemaName = v.SchemaName,
            DisplayName = v.DisplayName,
            Type = v.Type,
            DefaultValue = v.DefaultValue,
            CurrentValue = v.CurrentValue,
            IsManaged = v.IsManaged,
            IsRequired = v.IsRequired,
            ModifiedOn = v.ModifiedOn?.ToString("o"),
            HasOverride = v.CurrentValueId.HasValue,
            IsMissing = v.IsRequired && string.IsNullOrEmpty(v.CurrentValue) && string.IsNullOrEmpty(v.DefaultValue)
        };
    }

    private static EnvironmentVariableDetailDto MapEnvironmentVariableToDetailDto(EnvironmentVariableInfo v)
    {
        return new EnvironmentVariableDetailDto
        {
            SchemaName = v.SchemaName,
            DisplayName = v.DisplayName,
            Type = v.Type,
            DefaultValue = v.DefaultValue,
            CurrentValue = v.CurrentValue,
            IsManaged = v.IsManaged,
            IsRequired = v.IsRequired,
            ModifiedOn = v.ModifiedOn?.ToString("o"),
            HasOverride = v.CurrentValueId.HasValue,
            IsMissing = v.IsRequired && string.IsNullOrEmpty(v.CurrentValue) && string.IsNullOrEmpty(v.DefaultValue),
            Description = v.Description,
            CreatedOn = v.CreatedOn?.ToString("o")
        };
    }

    #endregion

    #region Web Resources

    /// <summary>
    /// Lists web resources for an environment, optionally filtered by solution.
    /// Maps to: ppds webresources list --json
    /// </summary>
    [JsonRpcMethod("webResources/list")]
    public async Task<WebResourcesListResponse> WebResourcesListAsync(
        string? solutionId = null,
        bool textOnly = true,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        Guid? parsedSolutionId = null;
        if (!string.IsNullOrWhiteSpace(solutionId))
        {
            if (!Guid.TryParse(solutionId, out var sid))
            {
                throw new RpcException(
                    ErrorCodes.Validation.InvalidValue,
                    "The 'solutionId' parameter must be a valid GUID");
            }
            parsedSolutionId = sid;
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var webResourceService = sp.GetRequiredService<IWebResourceService>();
            var result = await webResourceService.ListAsync(parsedSolutionId, textOnly, ct);

            return new WebResourcesListResponse
            {
                TotalCount = result.TotalCount,
                FiltersApplied = result.FiltersApplied.ToList(),
                Resources = result.Items.Select(MapWebResourceToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single web resource with content.
    /// Maps to: ppds webresources get --json
    /// </summary>
    [JsonRpcMethod("webResources/get")]
    public async Task<WebResourcesGetResponse> WebResourcesGetAsync(
        string id,
        bool published = false,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var webResourceService = sp.GetRequiredService<IWebResourceService>();
            var content = await webResourceService.GetContentAsync(resourceId, published, ct);

            return new WebResourcesGetResponse
            {
                Resource = content != null
                    ? new WebResourceDetailDto
                    {
                        Id = content.Id.ToString(),
                        Name = content.Name,
                        WebResourceType = content.WebResourceType,
                        Content = content.Content,
                        ModifiedOn = content.ModifiedOn?.ToString("o")
                    }
                    : null
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets the modifiedOn timestamp for a web resource (lightweight conflict detection).
    /// Maps to: ppds webresources get-modified-on
    /// </summary>
    [JsonRpcMethod("webResources/getModifiedOn")]
    public async Task<WebResourcesGetModifiedOnResponse> WebResourcesGetModifiedOnAsync(
        string id,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            var webResourceService = sp.GetRequiredService<IWebResourceService>();
            var modifiedOn = await webResourceService.GetModifiedOnAsync(resourceId, ct);

            return new WebResourcesGetModifiedOnResponse
            {
                ModifiedOn = modifiedOn?.ToString("o")
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates the content of a web resource. Does NOT publish.
    /// Maps to: ppds webresources update
    /// </summary>
    [JsonRpcMethod("webResources/update")]
    public async Task<WebResourcesUpdateResponse> WebResourcesUpdateAsync(
        string id,
        string content,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        if (content == null)
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'content' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            try
            {
                var webResourceService = sp.GetRequiredService<IWebResourceService>();
                await webResourceService.UpdateContentAsync(resourceId, content, ct);

                return new WebResourcesUpdateResponse
                {
                    Success = true
                };
            }
            catch (KeyNotFoundException ex)
            {
                throw new RpcException(ErrorCodes.WebResource.NotFound, ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(ErrorCodes.WebResource.NotEditable, ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Publishes specific web resources via PublishXml.
    /// Maps to: ppds webresources publish
    /// </summary>
    [JsonRpcMethod("webResources/publish")]
    public async Task<WebResourcesPublishResponse> WebResourcesPublishAsync(
        string[] ids,
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (ids == null || ids.Length == 0)
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'ids' parameter must contain at least one GUID");
        }

        var parsedIds = new List<Guid>(ids.Length);
        foreach (var rawId in ids)
        {
            if (!Guid.TryParse(rawId, out var parsed))
            {
                throw new RpcException(
                    ErrorCodes.Validation.InvalidValue,
                    $"The value '{rawId}' is not a valid GUID");
            }
            parsedIds.Add(parsed);
        }

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            try
            {
                var webResourceService = sp.GetRequiredService<IWebResourceService>();
                var count = await webResourceService.PublishAsync(parsedIds, ct);

                return new WebResourcesPublishResponse
                {
                    PublishedCount = count
                };
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(ErrorCodes.Operation.InProgress, ex.Message);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Publishes all customizations via PublishAllXml.
    /// Maps to: ppds webresources publish-all
    /// </summary>
    [JsonRpcMethod("webResources/publishAll")]
    public async Task<WebResourcesPublishAllResponse> WebResourcesPublishAllAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
        {
            try
            {
                var webResourceService = sp.GetRequiredService<IWebResourceService>();
                await webResourceService.PublishAllAsync(ct);

                return new WebResourcesPublishAllResponse
                {
                    Success = true
                };
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(ErrorCodes.Operation.InProgress, ex.Message);
            }
        }, cancellationToken);
    }

    private static WebResourceInfoDto MapWebResourceToDto(WebResourceInfoModel wr)
    {
        return new WebResourceInfoDto
        {
            Id = wr.Id.ToString(),
            Name = wr.Name,
            DisplayName = wr.DisplayName,
            Type = wr.WebResourceType,
            TypeName = wr.TypeName,
            FileExtension = wr.FileExtension,
            IsManaged = wr.IsManaged,
            IsTextType = wr.IsTextType,
            CreatedBy = wr.CreatedByName,
            CreatedOn = wr.CreatedOn?.ToString("o"),
            ModifiedBy = wr.ModifiedByName,
            ModifiedOn = wr.ModifiedOn?.ToString("o")
        };
    }

    #endregion

    #region Service Endpoints

    // ── Service Endpoints ──

    /// <summary>
    /// Lists all service endpoints and webhooks in the environment.
    /// Maps to: ppds service-endpoints list --json
    /// </summary>
    [JsonRpcMethod("serviceEndpoints/list")]
    public async Task<ServiceEndpointsListResponse> ServiceEndpointsListAsync(
        string? environmentUrl = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var endpointId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(contractType))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'contractType' parameter is required");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var endpointId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
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
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var endpointId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(environmentUrl, async (sp, ct) =>
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

    [JsonPropertyName("queryMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QueryMode { get; set; }

    [JsonPropertyName("dataSources")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<QueryDataSourceDto>? DataSources { get; set; }

    [JsonPropertyName("appliedHints")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AppliedHints { get; set; }
}

/// <summary>
/// Data source information in query results (for cross-env queries).
/// </summary>
public class QueryDataSourceDto
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("isRemote")] public bool IsRemote { get; set; }
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

    /// <summary>Gets or sets the total count of records matching the query.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    /// <summary>Gets or sets the filters that were applied.</summary>
    [JsonPropertyName("filtersApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FiltersApplied { get; set; }
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

    /// <summary>
    /// Gets or sets a value indicating whether the solution is visible.
    /// </summary>
    [JsonPropertyName("isVisible")]
    public bool IsVisible { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the solution is API managed.
    /// </summary>
    [JsonPropertyName("isApiManaged")]
    public bool IsApiManaged { get; set; }
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
    [JsonPropertyName("fetchXml")] public string? FetchXml { get; set; }
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
    [JsonPropertyName("dmlSafety")] public DmlSafetyRpcOptions? DmlSafety { get; set; }
}

/// <summary>
/// Options for DML safety checking passed from the TypeScript client.
/// When present, the SQL statement is parsed and checked via <see cref="Services.Query.DmlSafetyGuard"/>
/// before transpilation/execution.
/// </summary>
public sealed class DmlSafetyRpcOptions
{
    [JsonPropertyName("isConfirmed")] public bool IsConfirmed { get; set; }
    [JsonPropertyName("isDryRun")] public bool IsDryRun { get; set; }
    [JsonPropertyName("noLimit")] public bool NoLimit { get; set; }
    [JsonPropertyName("rowCap")] public int? RowCap { get; set; }
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
    [JsonPropertyName("fetchXml")] public string? FetchXml { get; set; }
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

// ── Import Jobs DTOs ────────────────────────────────────────────────────────

public class ImportJobsListResponse
{
    [JsonPropertyName("jobs")]
    public List<ImportJobInfoDto> Jobs { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public class ImportJobsGetResponse
{
    [JsonPropertyName("job")]
    public ImportJobDetailDto Job { get; set; } = null!;
}

public class ImportJobInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("solutionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SolutionName { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("startedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartedOn { get; set; }

    [JsonPropertyName("completedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedOn { get; set; }

    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }

    [JsonPropertyName("operationContext")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OperationContext { get; set; }
}

public class ImportJobDetailDto : ImportJobInfoDto
{
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
}

// ── Plugin Traces DTOs ──────────────────────────────────────────────────────

public class PluginTracesListResponse
{
    [JsonPropertyName("traces")]
    public List<PluginTraceInfoDto> Traces { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

public class PluginTracesGetResponse
{
    [JsonPropertyName("trace")]
    public PluginTraceDetailDto Trace { get; set; } = null!;
}

public class PluginTracesTimelineResponse
{
    [JsonPropertyName("nodes")]
    public List<TimelineNodeDto> Nodes { get; set; } = [];
}

public class PluginTracesDeleteResponse
{
    [JsonPropertyName("deletedCount")]
    public int DeletedCount { get; set; }
}

public class PluginTracesTraceLevelResponse
{
    [JsonPropertyName("level")]
    public string Level { get; set; } = "";

    [JsonPropertyName("levelValue")]
    public int LevelValue { get; set; }
}

public class PluginTracesSetTraceLevelResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class PluginTraceInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = "";

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("createdOn")]
    public string CreatedOn { get; set; } = "";

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }
}

public class PluginTraceDetailDto : PluginTraceInfoDto
{
    [JsonPropertyName("constructorDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConstructorDurationMs { get; set; }

    [JsonPropertyName("executionStartTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExecutionStartTime { get; set; }

    [JsonPropertyName("exceptionDetails")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExceptionDetails { get; set; }

    [JsonPropertyName("messageBlock")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageBlock { get; set; }

    [JsonPropertyName("configuration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Configuration { get; set; }

    [JsonPropertyName("secureConfiguration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SecureConfiguration { get; set; }

    [JsonPropertyName("requestId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; set; }

    // Additional fields (PT-01 through PT-09)
    [JsonPropertyName("stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stage { get; set; }

    [JsonPropertyName("constructorStartTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConstructorStartTime { get; set; }

    [JsonPropertyName("isSystemCreated")]
    public bool IsSystemCreated { get; set; }

    [JsonPropertyName("createdById")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedById { get; set; }

    [JsonPropertyName("createdOnBehalfById")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOnBehalfById { get; set; }

    [JsonPropertyName("pluginStepId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PluginStepId { get; set; }

    [JsonPropertyName("persistenceKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PersistenceKey { get; set; }

    [JsonPropertyName("organizationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OrganizationId { get; set; }

    [JsonPropertyName("profile")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Profile { get; set; }
}

public class TimelineNodeDto
{
    [JsonPropertyName("traceId")]
    public string TraceId { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("durationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DurationMs { get; set; }

    [JsonPropertyName("hasException")]
    public bool HasException { get; set; }

    [JsonPropertyName("offsetPercent")]
    public double OffsetPercent { get; set; }

    [JsonPropertyName("widthPercent")]
    public double WidthPercent { get; set; }

    [JsonPropertyName("hierarchyDepth")]
    public int HierarchyDepth { get; set; }

    [JsonPropertyName("children")]
    public List<TimelineNodeDto> Children { get; set; } = [];
}

public class TraceFilterDto
{
    [JsonPropertyName("typeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TypeName { get; set; }

    [JsonPropertyName("messageName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MessageName { get; set; }

    [JsonPropertyName("primaryEntity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryEntity { get; set; }

    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("hasException")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasException { get; set; }

    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; set; }

    [JsonPropertyName("minDurationMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinDurationMs { get; set; }

    [JsonPropertyName("startDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndDate { get; set; }
}

// ── Metadata DTOs ────────────────────────────────────────────────────────

public class MetadataEntitiesResponse
{
    [JsonPropertyName("entities")]
    public List<MetadataEntitySummaryDto> Entities { get; set; } = [];

    [JsonPropertyName("intersectHiddenCount")]
    public int IntersectHiddenCount { get; set; }
}

public class MetadataGlobalOptionSetsResponse
{
    [JsonPropertyName("optionSets")]
    public List<MetadataGlobalChoiceSummaryDto> OptionSets { get; set; } = [];
}

public class MetadataGlobalOptionSetDetailResponse
{
    [JsonPropertyName("optionSet")]
    public MetadataOptionSetDto OptionSet { get; set; } = null!;
}

public class MetadataGlobalChoiceSummaryDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("optionSetType")]
    public string OptionSetType { get; set; } = "";

    [JsonPropertyName("isCustomOptionSet")]
    public bool IsCustomOptionSet { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("optionCount")]
    public int OptionCount { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public class MetadataEntitySummaryDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    [JsonPropertyName("objectTypeCode")]
    public int ObjectTypeCode { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

public class MetadataEntityResponse
{
    [JsonPropertyName("entity")]
    public MetadataEntityDetailDto Entity { get; set; } = null!;
}

public class MetadataEntityDetailDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("isCustomEntity")]
    public bool IsCustomEntity { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("ownershipType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OwnershipType { get; set; }

    [JsonPropertyName("objectTypeCode")]
    public int ObjectTypeCode { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("primaryIdAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryIdAttribute { get; set; }

    [JsonPropertyName("primaryNameAttribute")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PrimaryNameAttribute { get; set; }

    [JsonPropertyName("entitySetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntitySetName { get; set; }

    [JsonPropertyName("isActivity")]
    public bool IsActivity { get; set; }

    [JsonPropertyName("attributes")]
    public List<MetadataAttributeDto> Attributes { get; set; } = [];

    [JsonPropertyName("oneToManyRelationships")]
    public List<MetadataRelationshipDto> OneToManyRelationships { get; set; } = [];

    [JsonPropertyName("manyToOneRelationships")]
    public List<MetadataRelationshipDto> ManyToOneRelationships { get; set; } = [];

    [JsonPropertyName("manyToManyRelationships")]
    public List<MetadataManyToManyDto> ManyToManyRelationships { get; set; } = [];

    [JsonPropertyName("keys")]
    public List<MetadataKeyDto> Keys { get; set; } = [];

    [JsonPropertyName("privileges")]
    public List<MetadataPrivilegeDto> Privileges { get; set; } = [];

    [JsonPropertyName("globalOptionSets")]
    public List<MetadataOptionSetDto> GlobalOptionSets { get; set; } = [];
}

public class MetadataAttributeDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("attributeType")]
    public string AttributeType { get; set; } = "";

    [JsonPropertyName("attributeTypeName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AttributeTypeName { get; set; }

    [JsonPropertyName("isPrimaryId")]
    public bool IsPrimaryId { get; set; }

    [JsonPropertyName("isPrimaryName")]
    public bool IsPrimaryName { get; set; }

    [JsonPropertyName("isCustomAttribute")]
    public bool IsCustomAttribute { get; set; }

    [JsonPropertyName("requiredLevel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequiredLevel { get; set; }

    [JsonPropertyName("maxLength")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLength { get; set; }

    [JsonPropertyName("minValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MinValue { get; set; }

    [JsonPropertyName("maxValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? MaxValue { get; set; }

    [JsonPropertyName("precision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Precision { get; set; }

    [JsonPropertyName("targets")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Targets { get; set; }

    [JsonPropertyName("optionSetName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OptionSetName { get; set; }

    [JsonPropertyName("isGlobalOptionSet")]
    public bool IsGlobalOptionSet { get; set; }

    [JsonPropertyName("options")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<MetadataOptionValueDto>? Options { get; set; }

    [JsonPropertyName("format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Format { get; set; }

    [JsonPropertyName("dateTimeBehavior")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DateTimeBehavior { get; set; }

    [JsonPropertyName("sourceType")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SourceType { get; set; }

    [JsonPropertyName("isSecured")]
    public bool IsSecured { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("autoNumberFormat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AutoNumberFormat { get; set; }
}

public class MetadataRelationshipDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("relationshipType")]
    public string RelationshipType { get; set; } = "";

    [JsonPropertyName("referencedEntity")]
    public string ReferencedEntity { get; set; } = "";

    [JsonPropertyName("referencedAttribute")]
    public string ReferencedAttribute { get; set; } = "";

    [JsonPropertyName("referencingEntity")]
    public string ReferencingEntity { get; set; } = "";

    [JsonPropertyName("referencingAttribute")]
    public string ReferencingAttribute { get; set; } = "";

    [JsonPropertyName("cascadeAssign")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeAssign { get; set; }

    [JsonPropertyName("cascadeDelete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeDelete { get; set; }

    [JsonPropertyName("cascadeMerge")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeMerge { get; set; }

    [JsonPropertyName("cascadeReparent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeReparent { get; set; }

    [JsonPropertyName("cascadeShare")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeShare { get; set; }

    [JsonPropertyName("cascadeUnshare")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CascadeUnshare { get; set; }

    [JsonPropertyName("isHierarchical")]
    public bool IsHierarchical { get; set; }
}

public class MetadataManyToManyDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("entity1LogicalName")]
    public string Entity1LogicalName { get; set; } = "";

    [JsonPropertyName("entity1IntersectAttribute")]
    public string Entity1IntersectAttribute { get; set; } = "";

    [JsonPropertyName("entity2LogicalName")]
    public string Entity2LogicalName { get; set; } = "";

    [JsonPropertyName("entity2IntersectAttribute")]
    public string Entity2IntersectAttribute { get; set; } = "";

    [JsonPropertyName("intersectEntityName")]
    public string IntersectEntityName { get; set; } = "";
}

public class MetadataKeyDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("keyAttributes")]
    public List<string> KeyAttributes { get; set; } = [];

    [JsonPropertyName("entityKeyIndexStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EntityKeyIndexStatus { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }
}

public class MetadataPrivilegeDto
{
    [JsonPropertyName("privilegeId")]
    public Guid PrivilegeId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("privilegeType")]
    public string PrivilegeType { get; set; } = "";

    [JsonPropertyName("canBeLocal")]
    public bool CanBeLocal { get; set; }

    [JsonPropertyName("canBeDeep")]
    public bool CanBeDeep { get; set; }

    [JsonPropertyName("canBeGlobal")]
    public bool CanBeGlobal { get; set; }

    [JsonPropertyName("canBeBasic")]
    public bool CanBeBasic { get; set; }
}

public class MetadataOptionSetDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    [JsonPropertyName("optionSetType")]
    public string OptionSetType { get; set; } = "";

    [JsonPropertyName("isGlobal")]
    public bool IsGlobal { get; set; }

    [JsonPropertyName("options")]
    public List<MetadataOptionValueDto> Options { get; set; } = [];
}

public class MetadataOptionValueDto
{
    [JsonPropertyName("value")]
    public int Value { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("color")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Color { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }
}

// ── Connection References DTOs ─────────────────────────────────────────────────

public class ConnectionReferencesListResponse
{
    [JsonPropertyName("references")]
    public List<ConnectionReferenceInfoDto> References { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("filtersApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FiltersApplied { get; set; }
}

public class ConnectionReferencesGetResponse
{
    [JsonPropertyName("reference")]
    public ConnectionReferenceDetailDto Reference { get; set; } = null!;
}

public class ConnectionReferencesAnalyzeResponse
{
    [JsonPropertyName("orphanedReferences")]
    public List<OrphanedReferenceDto> OrphanedReferences { get; set; } = [];

    [JsonPropertyName("orphanedFlows")]
    public List<OrphanedFlowDto> OrphanedFlows { get; set; } = [];

    [JsonPropertyName("totalReferences")]
    public int TotalReferences { get; set; }

    [JsonPropertyName("totalFlows")]
    public int TotalFlows { get; set; }
}

public class ConnectionReferenceInfoDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorId { get; set; }

    [JsonPropertyName("connectionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionId { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("connectionStatus")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionStatus { get; set; }

    [JsonPropertyName("connectorDisplayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorDisplayName { get; set; }
}

public class ConnectionReferenceDetailDto : ConnectionReferenceInfoDto
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("isBound")]
    public bool IsBound { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("flows")]
    public List<FlowReferenceDto> Flows { get; set; } = [];

    [JsonPropertyName("connectionOwner")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectionOwner { get; set; }

    [JsonPropertyName("connectionIsShared")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ConnectionIsShared { get; set; }
}

public class FlowReferenceDto
{
    [JsonPropertyName("flowId")]
    public string FlowId { get; set; } = "";

    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = "";
}

public class OrphanedReferenceDto
{
    [JsonPropertyName("logicalName")]
    public string LogicalName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("connectorId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConnectorId { get; set; }
}

public class OrphanedFlowDto
{
    [JsonPropertyName("uniqueName")]
    public string UniqueName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("missingReference")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MissingReference { get; set; }
}

// ── Environment Variables DTOs ─────────────────────────────────────────────────

public class EnvironmentVariablesListResponse
{
    [JsonPropertyName("variables")]
    public List<EnvironmentVariableInfoDto> Variables { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("filtersApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FiltersApplied { get; set; }
}

public class EnvironmentVariablesGetResponse
{
    [JsonPropertyName("variable")]
    public EnvironmentVariableDetailDto Variable { get; set; } = null!;
}

public class EnvironmentVariablesSetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class EnvironmentVariableInfoDto
{
    [JsonPropertyName("schemaName")]
    public string SchemaName { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("defaultValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("currentValue")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrentValue { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("hasOverride")]
    public bool HasOverride { get; set; }

    [JsonPropertyName("isMissing")]
    public bool IsMissing { get; set; }
}

public class EnvironmentVariableDetailDto : EnvironmentVariableInfoDto
{
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }
}

public class SyncStatisticsDto
{
    [JsonPropertyName("added")]
    public int Added { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    [JsonPropertyName("preserved")]
    public int Preserved { get; set; }
}

public class EnvironmentVariablesSyncDeploymentSettingsResponse
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = "";

    [JsonPropertyName("environmentVariables")]
    public SyncStatisticsDto EnvironmentVariables { get; set; } = new();

    [JsonPropertyName("connectionReferences")]
    public SyncStatisticsDto ConnectionReferences { get; set; } = new();
}

// ── Web Resources DTOs ──────────────────────────────────────────────────────

#region Web Resources DTOs

public class WebResourcesListResponse
{
    [JsonPropertyName("resources")]
    public List<WebResourceInfoDto> Resources { get; set; } = [];

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("filtersApplied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? FiltersApplied { get; set; }
}

public class WebResourcesGetResponse
{
    [JsonPropertyName("resource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WebResourceDetailDto? Resource { get; set; }
}

public class WebResourcesGetModifiedOnResponse
{
    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class WebResourcesUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class WebResourcesPublishResponse
{
    [JsonPropertyName("publishedCount")]
    public int PublishedCount { get; set; }
}

public class WebResourcesPublishAllResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class WebResourceInfoDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("displayName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; set; }

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("fileExtension")]
    public string FileExtension { get; set; } = "";

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    [JsonPropertyName("isTextType")]
    public bool IsTextType { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modifiedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedBy { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

public class WebResourceDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("webResourceType")]
    public int WebResourceType { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("modifiedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ModifiedOn { get; set; }
}

#endregion

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

#endregion
