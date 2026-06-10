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
    #region Data Providers and Data Sources

    // ── Data Providers ──

    /// <summary>
    /// Lists all data providers, optionally filtered by data source.
    /// Maps to: ppds data-providers list --json
    /// </summary>
    [JsonRpcMethod("dataProviders/list")]
    public async Task<DataProvidersListResponse> DataProvidersListAsync(
        string? dataSourceId = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        Guid? dataSourceGuid = null;
        if (!string.IsNullOrWhiteSpace(dataSourceId))
        {
            if (!Guid.TryParse(dataSourceId, out var dsGuid))
                throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'dataSourceId' parameter must be a valid GUID");
            dataSourceGuid = dsGuid;
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            var providers = await service.ListDataProvidersAsync(dataSourceGuid, ct);

            return new DataProvidersListResponse
            {
                Providers = providers.Select(MapDataProviderToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single data provider by name or ID.
    /// Maps to: ppds data-providers get --json
    /// </summary>
    [JsonRpcMethod("dataProviders/get")]
    public async Task<DataProvidersGetResponse> DataProvidersGetAsync(
        string nameOrId,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'nameOrId' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            var provider = await service.GetDataProviderAsync(nameOrId, ct)
                ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Data provider '{nameOrId}' not found");

            return new DataProvidersGetResponse
            {
                Provider = MapDataProviderToDto(provider)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers a new data provider with plugin operation bindings.
    /// Maps to: ppds data-providers register --json
    /// </summary>
    [JsonRpcMethod("dataProviders/register")]
    public async Task<DataProvidersRegisterResponse> DataProvidersRegisterAsync(
        string name,
        string dataSourceId,
        string? retrievePlugin = null,
        string? retrieveMultiplePlugin = null,
        string? createPlugin = null,
        string? updatePlugin = null,
        string? deletePlugin = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");
        if (string.IsNullOrWhiteSpace(dataSourceId) || !Guid.TryParse(dataSourceId, out var dataSourceGuid))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'dataSourceId' parameter must be a valid GUID");

        static Guid? ParseOptionalGuid(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Guid.TryParse(value, out var g))
                throw new RpcException(ErrorCodes.Validation.RequiredField, $"The '{paramName}' parameter must be a valid GUID");
            return g;
        }

        var retrieveGuid = ParseOptionalGuid(retrievePlugin, "retrievePlugin");
        var retrieveMultipleGuid = ParseOptionalGuid(retrieveMultiplePlugin, "retrieveMultiplePlugin");
        var createGuid = ParseOptionalGuid(createPlugin, "createPlugin");
        var updateGuid = ParseOptionalGuid(updatePlugin, "updatePlugin");
        var deleteGuid = ParseOptionalGuid(deletePlugin, "deletePlugin");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            var newId = await service.RegisterDataProviderAsync(
                new DataProviderRegistration(
                    name,
                    dataSourceGuid,
                    retrieveGuid,
                    retrieveMultipleGuid,
                    createGuid,
                    updateGuid,
                    deleteGuid),
                ct);

            return new DataProvidersRegisterResponse { Id = newId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Updates plugin bindings on an existing data provider.
    /// Maps to: ppds data-providers update --json
    /// </summary>
    [JsonRpcMethod("dataProviders/update")]
    public async Task<DataProvidersUpdateResponse> DataProvidersUpdateAsync(
        string id,
        string? retrievePlugin = null,
        string? retrieveMultiplePlugin = null,
        string? createPlugin = null,
        string? updatePlugin = null,
        string? deletePlugin = null,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var providerId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        static Guid? ParseOptionalGuid(string? value, string paramName)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Guid.TryParse(value, out var g))
                throw new RpcException(ErrorCodes.Validation.RequiredField, $"The '{paramName}' parameter must be a valid GUID");
            return g;
        }

        var retrieveGuid = ParseOptionalGuid(retrievePlugin, "retrievePlugin");
        var retrieveMultipleGuid = ParseOptionalGuid(retrieveMultiplePlugin, "retrieveMultiplePlugin");
        var createGuid = ParseOptionalGuid(createPlugin, "createPlugin");
        var updateGuid = ParseOptionalGuid(updatePlugin, "updatePlugin");
        var deleteGuid = ParseOptionalGuid(deletePlugin, "deletePlugin");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            await service.UpdateDataProviderAsync(
                providerId,
                new DataProviderUpdateRequest(
                    retrieveGuid,
                    retrieveMultipleGuid,
                    createGuid,
                    updateGuid,
                    deleteGuid),
                ct);

            return new DataProvidersUpdateResponse { Success = true };
        }, cancellationToken);
    }

    /// <summary>
    /// Unregisters a data provider.
    /// Maps to: ppds data-providers unregister --json
    /// </summary>
    [JsonRpcMethod("dataProviders/unregister")]
    public async Task<DataProvidersUnregisterResponse> DataProvidersUnregisterAsync(
        string id,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var providerId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            await service.UnregisterDataProviderAsync(providerId, ct);

            return new DataProvidersUnregisterResponse { Success = true };
        }, cancellationToken);
    }

    private static DataProviderDto MapDataProviderToDto(DataProviderInfo p) =>
        new()
        {
            Id = p.Id.ToString(),
            Name = p.Name,
            DataSourceName = p.DataSourceName,
            RetrievePlugin = p.RetrievePlugin?.ToString(),
            RetrieveMultiplePlugin = p.RetrieveMultiplePlugin?.ToString(),
            CreatePlugin = p.CreatePlugin?.ToString(),
            UpdatePlugin = p.UpdatePlugin?.ToString(),
            DeletePlugin = p.DeletePlugin?.ToString(),
            IsManaged = p.IsManaged
        };

    // ── Data Sources ──

    /// <summary>
    /// Lists all data sources in the environment.
    /// Maps to: ppds data-sources list --json
    /// </summary>
    [JsonRpcMethod("dataSources/list")]
    public async Task<DataSourcesListResponse> DataSourcesListAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            var sources = await service.ListDataSourcesAsync(ct);

            return new DataSourcesListResponse
            {
                DataSources = sources.Select(MapDataSourceToDto).ToList()
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a single data source by name or ID.
    /// Maps to: ppds data-sources get --json
    /// </summary>
    [JsonRpcMethod("dataSources/get")]
    public async Task<DataSourcesGetResponse> DataSourcesGetAsync(
        string nameOrId,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nameOrId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'nameOrId' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            var source = await service.GetDataSourceAsync(nameOrId, ct)
                ?? throw new RpcException(ErrorCodes.Operation.NotFound, $"Data source '{nameOrId}' not found");

            return new DataSourcesGetResponse
            {
                DataSource = MapDataSourceToDto(source)
            };
        }, cancellationToken);
    }

    /// <summary>
    /// Registers a new data source entity.
    /// Maps to: ppds data-sources register --json
    /// </summary>
    [JsonRpcMethod("dataSources/register")]
    public async Task<DataSourcesRegisterResponse> DataSourcesRegisterAsync(
        string name,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'name' parameter is required");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            var newId = await service.RegisterDataSourceAsync(
                new DataSourceRegistration(name),
                ct);

            return new DataSourcesRegisterResponse { Id = newId.ToString() };
        }, cancellationToken);
    }

    /// <summary>
    /// Rejects updates — entitydatasource has no mutable attributes.
    /// The logical name is assigned at creation time and cannot be changed.
    /// </summary>
    [JsonRpcMethod("dataSources/update")]
    public Task<DataSourcesUpdateResponse> DataSourcesUpdateAsync(
        string id,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        throw new RpcException(
            ErrorCodes.Validation.InvalidArguments,
            "Data sources have no mutable attributes. To rename, unregister and re-register.");
    }

    /// <summary>
    /// Unregisters a data source and cascade-deletes all child data providers.
    /// Maps to: ppds data-sources unregister --json
    /// </summary>
    [JsonRpcMethod("dataSources/unregister")]
    public async Task<DataSourcesUnregisterResponse> DataSourcesUnregisterAsync(
        string id,
        bool force = false,
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var sourceId))
            throw new RpcException(ErrorCodes.Validation.RequiredField, "The 'id' parameter must be a valid GUID");

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var service = sp.GetRequiredService<IDataProviderService>();
            await service.UnregisterDataSourceAsync(sourceId, force, ct);

            return new DataSourcesUnregisterResponse { Success = true };
        }, cancellationToken);
    }

    private static DataSourceDto MapDataSourceToDto(DataSourceInfo s) =>
        new()
        {
            Id = s.Id.ToString(),
            Name = s.Name
        };

    #endregion
}

public class DataProvidersListResponse
{
    [JsonPropertyName("providers")]
    public List<DataProviderDto> Providers { get; set; } = [];
}

public class DataProvidersGetResponse
{
    [JsonPropertyName("provider")]
    public DataProviderDto? Provider { get; set; }
}

public class DataProviderDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("dataSourceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DataSourceName { get; set; }

    [JsonPropertyName("retrievePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetrievePlugin { get; set; }

    [JsonPropertyName("retrieveMultiplePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RetrieveMultiplePlugin { get; set; }

    [JsonPropertyName("createPlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatePlugin { get; set; }

    [JsonPropertyName("updatePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatePlugin { get; set; }

    [JsonPropertyName("deletePlugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeletePlugin { get; set; }

    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }
}

public class DataProvidersRegisterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public class DataProvidersUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class DataProvidersUnregisterResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

// ── Data Sources DTOs ─────────────────────────────────────────────────────────

public class DataSourcesListResponse
{
    [JsonPropertyName("dataSources")]
    public List<DataSourceDto> DataSources { get; set; } = [];
}

public class DataSourcesGetResponse
{
    [JsonPropertyName("dataSource")]
    public DataSourceDto? DataSource { get; set; }
}

public class DataSourceDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class DataSourcesRegisterResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";
}

public class DataSourcesUpdateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}

public class DataSourcesUnregisterResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }
}
