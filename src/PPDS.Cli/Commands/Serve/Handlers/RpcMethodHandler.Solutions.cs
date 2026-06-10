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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'uniqueName' parameter is required");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
