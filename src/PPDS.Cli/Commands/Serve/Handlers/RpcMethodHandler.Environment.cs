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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        var store = _authServices.GetRequiredService<ProfileStore>();
        var collection = await store.LoadAsync(cancellationToken);

        AuthProfile? profile;
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            profile = collection.GetByNameOrIndex(profileName)
                ?? throw new RpcException(ErrorCodes.Auth.ProfileNotFound, $"Profile '{profileName}' not found");
        }
        else
        {
            profile = collection.ActiveProfile
                ?? throw new RpcException(ErrorCodes.Auth.NoActiveProfile, "No active profile configured");
        }

        var selectedUrl = profile.Environment?.Url?.TrimEnd('/').ToLowerInvariant();
        var profileKey = profile.Name ?? profile.DisplayIdentifier;

        // 1. Get discovered environments (cached per profile, unless forceRefresh)
        List<EnvironmentInfo> discovered;
        if (!forceRefresh
            && _envCacheByProfile.TryGetValue(profileKey, out var cached)
            && DateTime.UtcNow.Ticks < cached.expiry)
        {
            discovered = cached.environments;
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
            _envCacheByProfile[profileKey] = (discovered, (DateTime.UtcNow + DiscoveryCacheTtl).Ticks);
        }

        // 2. Get configured environments from environments.json and tag discovered envs with profile
        var configStore = _authServices.GetRequiredService<EnvironmentConfigStore>();
        var resolvedProfileName = profileKey;

        // Tag all discovered environments with this profile in environments.json
        foreach (var env in discovered)
        {
            await configStore.SaveConfigAsync(
                env.ApiUrl,
                discoveredType: env.Type,
                profileName: resolvedProfileName,
                ct: cancellationToken);
        }

        // Load config (fresh — after tagging) so the merge below reflects the writes above.
        configStore.ClearCache();
        var configCollection = await configStore.LoadAsync(cancellationToken);

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
            else if (config.Profiles.Contains(resolvedProfileName, StringComparer.OrdinalIgnoreCase))
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

        // Invalidate the active profile's env list cache; other profiles' caches remain valid.
        var activeProfileKey = profile.Name ?? profile.DisplayIdentifier;
        _envCacheByProfile.TryRemove(activeProfileKey, out _);

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
