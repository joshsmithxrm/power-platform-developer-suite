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
    #region Import Jobs

    /// <summary>
    /// Lists import jobs for an environment.
    /// Maps to: ppds importjobs list --json
    /// </summary>
    [JsonRpcMethod("importJobs/list")]
    public async Task<ImportJobsListResponse> ImportJobsListAsync(
        string? environmentUrl = null,
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
        string? profileName = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id) || !Guid.TryParse(id, out var importJobId))
        {
            throw new RpcException(
                ErrorCodes.Validation.RequiredField,
                "The 'id' parameter must be a valid GUID");
        }

        return await WithProfileAndEnvironmentAsync(profileName, environmentUrl, async (sp, ct) =>
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
}

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
