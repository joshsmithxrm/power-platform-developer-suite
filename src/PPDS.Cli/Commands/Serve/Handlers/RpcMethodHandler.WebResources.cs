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
        string? profileName = null,
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

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var resourceId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
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

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
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

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
}

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
