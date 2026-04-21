using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Cli.Services.ImportJobs;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that lists recent solution import jobs.
/// </summary>
[McpServerToolType]
public sealed class ImportJobsListTool : McpToolBase
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ImportJobsListTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public ImportJobsListTool(McpToolContext context) : base(context) { }

    /// <summary>
    /// Lists recent solution import jobs for the current environment.
    /// </summary>
    /// <param name="top">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of import job summaries.</returns>
    [McpServerTool(Name = "ppds_import_jobs_list")]
    [Description("List recent solution import jobs for the current environment. Shows import status, progress, solution name, and timing. Use this to check if a solution import succeeded or failed.")]
    public async Task<ImportJobsListResult> ExecuteAsync(
        CancellationToken cancellationToken = default)
    {
        await using var serviceProvider = await CreateScopeAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IImportJobService>();

        var result = await service.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ImportJobsListResult
        {
            TotalCount = result.TotalCount,
            Jobs = result.Items.Select(j => new ImportJobSummary
            {
                Id = j.Id.ToString(),
                SolutionName = j.SolutionName,
                Status = j.Status,
                Progress = j.Progress,
                CreatedBy = j.CreatedByName,
                CreatedOn = j.CreatedOn?.ToString("o"),
                CompletedOn = j.CompletedOn?.ToString("o"),
                Duration = j.FormattedDuration
            }).ToList()
        };
    }
}

/// <summary>
/// Result of the import_jobs_list tool.
/// </summary>
public sealed class ImportJobsListResult
{
    /// <summary>
    /// List of import job summaries.
    /// </summary>
    [JsonPropertyName("jobs")]
    public List<ImportJobSummary> Jobs { get; set; } = [];

    /// <summary>Total count of records.</summary>
    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }
}

/// <summary>
/// Summary information about an import job.
/// </summary>
public sealed class ImportJobSummary
{
    /// <summary>
    /// Import job ID (use with ppds_import_jobs_get for details).
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>
    /// Solution name that was imported.
    /// </summary>
    [JsonPropertyName("solutionName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SolutionName { get; set; }

    /// <summary>
    /// Import status: Succeeded, Failed, or In Progress.
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>
    /// Import progress percentage (0-100).
    /// </summary>
    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    /// <summary>
    /// Name of the user who initiated the import.
    /// </summary>
    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    /// <summary>
    /// When the import job was created (ISO 8601).
    /// </summary>
    [JsonPropertyName("createdOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedOn { get; set; }

    /// <summary>
    /// When the import job completed (ISO 8601), or null if still in progress.
    /// </summary>
    [JsonPropertyName("completedOn")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CompletedOn { get; set; }

    /// <summary>
    /// Formatted duration string (e.g., "2m 30s").
    /// </summary>
    [JsonPropertyName("duration")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Duration { get; set; }
}
