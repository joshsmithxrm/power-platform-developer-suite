using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using PPDS.Dataverse.Services;
using PPDS.Mcp.Infrastructure;

namespace PPDS.Mcp.Tools;

/// <summary>
/// MCP tool that gets full details of a specific import job.
/// </summary>
[McpServerToolType]
public sealed class ImportJobsGetTool
{
    private readonly McpToolContext _context;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportJobsGetTool"/> class.
    /// </summary>
    /// <param name="context">The MCP tool context.</param>
    public ImportJobsGetTool(McpToolContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Gets full details of a specific import job including the XML import log.
    /// </summary>
    /// <param name="id">The import job ID (GUID).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Import job details with XML data.</returns>
    [McpServerTool(Name = "ppds_import_jobs_get")]
    [Description("Get full details of a specific import job including the XML import log. Use the id from ppds_import_jobs_list. The import log XML contains detailed component-level success/failure information for troubleshooting failed imports.")]
    public async Task<ImportJobGetResult> ExecuteAsync(
        [Description("The import job ID (GUID) from ppds_import_jobs_list")]
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(id, out var importJobId))
        {
            throw new ArgumentException($"Invalid import job ID: '{id}'. Must be a valid GUID.");
        }

        await using var serviceProvider = await _context.CreateServiceProviderAsync(cancellationToken).ConfigureAwait(false);
        var service = serviceProvider.GetRequiredService<IImportJobService>();

        var job = await service.GetAsync(importJobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Import job '{id}' not found.");

        var data = await service.GetDataAsync(importJobId, cancellationToken).ConfigureAwait(false);

        return new ImportJobGetResult
        {
            Id = job.Id.ToString(),
            SolutionName = job.SolutionName,
            Status = job.Status,
            Progress = job.Progress,
            CreatedBy = job.CreatedByName,
            CreatedOn = job.CreatedOn?.ToString("o"),
            CompletedOn = job.CompletedOn?.ToString("o"),
            Data = data
        };
    }
}

/// <summary>
/// Result of the import_jobs_get tool.
/// </summary>
public sealed class ImportJobGetResult
{
    /// <summary>
    /// Import job ID.
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
    /// Raw XML import log data with component-level details.
    /// </summary>
    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Data { get; set; }
}
