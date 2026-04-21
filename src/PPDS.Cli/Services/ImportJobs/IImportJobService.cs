using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Models;

namespace PPDS.Cli.Services.ImportJobs;

/// <summary>
/// Service for querying and monitoring Dataverse import jobs.
/// </summary>
public interface IImportJobService
{
    /// <summary>
    /// Lists import jobs in the environment.
    /// </summary>
    /// <param name="solutionName">Optional filter by solution name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ListResult<ImportJobInfo>> ListAsync(
        string? solutionName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an import job by ID.
    /// </summary>
    /// <param name="importJobId">The import job ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImportJobInfo?> GetAsync(Guid importJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the raw XML data for an import job.
    /// </summary>
    /// <param name="importJobId">The import job ID.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<string?> GetDataAsync(Guid importJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for an import job to complete.
    /// </summary>
    /// <param name="importJobId">The import job ID.</param>
    /// <param name="pollInterval">Interval between status checks.</param>
    /// <param name="timeout">Maximum time to wait.</param>
    /// <param name="onProgress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<ImportJobInfo> WaitForCompletionAsync(
        Guid importJobId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        Action<ImportJobInfo>? onProgress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Information about an import job.
/// </summary>
public record ImportJobInfo(
    Guid Id,
    string? Name,
    string? SolutionName,
    Guid? SolutionId,
    double Progress,
    DateTime? StartedOn,
    DateTime? CompletedOn,
    DateTime? CreatedOn,
    bool IsComplete,
    string? CreatedByName = null,
    string? OperationContext = null)
{
    /// <summary>
    /// Computed status: Succeeded, Failed, or In Progress.
    /// Single code path for all surfaces (Constitution A2).
    /// </summary>
    public string Status => CompletedOn.HasValue
        ? (Progress >= 100 ? "Succeeded" : "Failed")
        : "In Progress";

    /// <summary>
    /// Computed formatted duration, or null if StartedOn is not set.
    /// </summary>
    public string? FormattedDuration
    {
        get
        {
            if (!StartedOn.HasValue) return null;
            var span = (CompletedOn ?? DateTime.UtcNow) - StartedOn.Value;
            var formatted = span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m {span.Seconds}s"
                : span.TotalMinutes >= 1
                    ? $"{(int)span.TotalMinutes}m {span.Seconds}s"
                    : span.TotalSeconds >= 1
                        ? $"{span.Seconds}s"
                        : "< 1s";
            return CompletedOn.HasValue ? formatted : formatted + " (ongoing)";
        }
    }
}
