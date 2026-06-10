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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schemaName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'schemaName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
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

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
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

        // Constrain the caller-supplied path to the workspace root. RPC is untrusted; a malicious
        // webview message must not be able to read or overwrite files outside the workspace.
        var fullPath = ResolveWorkspacePath(filePath, nameof(filePath));

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
        {
            var settingsService = sp.GetRequiredService<IDeploymentSettingsService>();

            // Load existing settings if file already exists
            DeploymentSettingsFile? existingSettings = null;

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
}

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
