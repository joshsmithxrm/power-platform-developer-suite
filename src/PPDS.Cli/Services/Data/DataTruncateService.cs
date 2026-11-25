using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.BulkOperations;
using DataverseProgressSnapshot = PPDS.Dataverse.Progress.ProgressSnapshot;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Deletes every record of a table in passes (<c>data truncate</c>). Each record is attempted at most once per run, so a
/// record that cannot be deleted does not keep the run going (#1922).
/// </summary>
public interface IDataTruncateService
{
    /// <summary>
    /// Deletes the records of <paramref name="entity"/> in passes of at most <see cref="DataTruncateOptions.BatchSize"/>
    /// records until none is left that this run has not attempted, or until <see cref="DataTruncateResult.StopReason"/>
    /// says why it stopped earlier.
    /// </summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="options">Batch size, error handling and custom-logic bypass.</param>
    /// <param name="progress">Receives the number of records processed so far; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counts of deleted and failed records and why the run ended.</returns>
    Task<DataTruncateResult> TruncateAsync(
        string entity,
        DataTruncateOptions options,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Options for <see cref="IDataTruncateService.TruncateAsync"/>.</summary>
public sealed record DataTruncateOptions
{
    /// <summary>Records deleted per pass (1 to 1000).</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>When false, the run stops after the first pass with a failure.</summary>
    public bool ContinueOnError { get; init; } = true;

    /// <summary>Custom business logic to bypass.</summary>
    public CustomLogicBypass BypassCustomLogic { get; init; } = CustomLogicBypass.None;

    /// <summary>Whether Power Automate flows are bypassed.</summary>
    public bool BypassPowerAutomateFlows { get; init; }

    /// <summary>The record count shown to the user before the run, used only as the progress total.</summary>
    public int ExpectedCount { get; init; }
}

/// <summary>Why a truncate run ended.</summary>
public enum DataTruncateStopReason
{
    /// <summary>No record of the table was left to read.</summary>
    Completed,

    /// <summary>Every record left is one that this run failed to delete; none was retried.</summary>
    OnlyFailedRecordsRemain,

    /// <summary>
    /// A read returned only records that this run failed to delete, and as many as one read can return: records not
    /// attempted may remain.
    /// </summary>
    TooManyFailedRecords,

    /// <summary>A pass deleted no record.</summary>
    PassDeletedNothing,

    /// <summary>A pass had a failure and <see cref="DataTruncateOptions.ContinueOnError"/> is false.</summary>
    StoppedOnError,

    /// <summary>A record reported as deleted was read again, so the counts are not certain.</summary>
    DeletedRecordReadAgain
}

/// <summary>The outcome of <see cref="IDataTruncateService.TruncateAsync"/>.</summary>
public sealed record DataTruncateResult
{
    /// <summary>Records deleted.</summary>
    public int DeletedCount { get; init; }

    /// <summary>Records that could not be deleted (each counted once).</summary>
    public int FailedCount { get; init; }

    /// <summary>Delete passes made.</summary>
    public int Passes { get; init; }

    /// <summary>Why the run ended.</summary>
    public DataTruncateStopReason StopReason { get; init; }

    /// <summary>The errors of the failed records.</summary>
    public IReadOnlyList<BulkOperationError> Errors { get; init; } =
        Array.Empty<BulkOperationError>();

    /// <summary>Time taken.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>True when the table was emptied with no failure.</summary>
    public bool IsSuccess => StopReason == DataTruncateStopReason.Completed && FailedCount == 0;
}

/// <inheritdoc />
public sealed class DataTruncateService : IDataTruncateService
{
    /// <summary>The most rows one read may return (QueryExpression.TopCount).</summary>
    internal const int MaxFetch = 5000;

    private readonly IDataQueryService _dataQueryService;
    private readonly IBulkOperationExecutor _bulkExecutor;
    private readonly ILogger<DataTruncateService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DataTruncateService"/>.
    /// </summary>
    /// <param name="dataQueryService">Reads the ids of each pass.</param>
    /// <param name="bulkExecutor">Deletes each pass.</param>
    /// <param name="logger">Logger instance.</param>
    public DataTruncateService(
        IDataQueryService dataQueryService,
        IBulkOperationExecutor bulkExecutor,
        ILogger<DataTruncateService> logger)
    {
        _dataQueryService = dataQueryService ?? throw new ArgumentNullException(nameof(dataQueryService));
        _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<DataTruncateResult> TruncateAsync(
        string entity,
        DataTruncateOptions options,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));
        ArgumentNullException.ThrowIfNull(options);
        if (options.BatchSize < 1 || options.BatchSize > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Batch size must be between 1 and 1000.");
        }

        var stopwatch = Stopwatch.StartNew();
        var attempted = new HashSet<Guid>();
        var failed = new HashSet<Guid>();
        var errors = new List<BulkOperationError>();
        var deleted = 0;
        var failedCount = 0;
        var unidentifiedFailures = 0;
        var passes = 0;
        DataTruncateStopReason reason;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ask for enough rows that the records already failed cannot crowd out the next batch.
            var fetchSize = Math.Min(MaxFetch, options.BatchSize + failed.Count);
            var fetched = await _dataQueryService.FetchBatchIdsAsync(entity, fetchSize, cancellationToken);
            if (fetched.Count == 0)
            {
                reason = DataTruncateStopReason.Completed;
                break;
            }

            // A record this run already attempted is never sent again. One that was not reported as failed came back
            // although reported deleted: if the executor reported failures it could not name, it is one of those;
            // otherwise the counts are not certain and the run stops.
            var readAgain = fetched.Where(id => attempted.Contains(id) && !failed.Contains(id)).ToList();
            foreach (var id in readAgain)
            {
                if (unidentifiedFailures == 0)
                {
                    break;
                }

                unidentifiedFailures--;
                failed.Add(id);
            }

            if (fetched.Any(id => attempted.Contains(id) && !failed.Contains(id)))
            {
                reason = DataTruncateStopReason.DeletedRecordReadAgain;
                break;
            }

            var batch = fetched.Where(id => !attempted.Contains(id)).Distinct().Take(options.BatchSize).ToList();
            if (batch.Count == 0)
            {
                reason = fetched.Count >= fetchSize
                    ? DataTruncateStopReason.TooManyFailedRecords
                    : DataTruncateStopReason.OnlyFailedRecordsRemain;
                break;
            }

            attempted.UnionWith(batch);
            passes++;

            var processedBefore = deleted + failedCount;
            var batchProgress = progress is null
                ? null
                : new Progress<DataverseProgressSnapshot>(snapshot => progress.ReportProgress(new ProgressSnapshot
                {
                    CurrentItem = processedBefore + (int)snapshot.Processed,
                    TotalItems = options.ExpectedCount,
                    CurrentEntity = entity,
                    RecordsPerSecond = snapshot.InstantRatePerSecond
                }));

            var result = await _bulkExecutor.DeleteMultipleAsync(
                entity,
                batch,
                new BulkOperationOptions
                {
                    BatchSize = options.BatchSize,
                    ContinueOnError = options.ContinueOnError,
                    BypassCustomLogic = options.BypassCustomLogic,
                    BypassPowerAutomateFlows = options.BypassPowerAutomateFlows
                },
                progress: batchProgress,
                cancellationToken: cancellationToken);

            deleted += result.SuccessCount;
            failedCount += result.FailureCount;
            errors.AddRange(result.Errors);

            var named = 0;
            foreach (var error in result.Errors)
            {
                var id = error.RecordId ??
                         (error.Index >= 0 && error.Index < batch.Count ? batch[error.Index] : (Guid?)null);
                if (id is { } failedId && failed.Add(failedId))
                {
                    named++;
                }
            }

            unidentifiedFailures += Math.Max(0, result.FailureCount - named);

            _logger.LogDebug(
                "Truncate pass {Pass} of {Entity}: {Deleted} deleted, {Failed} failed",
                passes, entity, result.SuccessCount, result.FailureCount);

            if (result.FailureCount > 0 && !options.ContinueOnError)
            {
                reason = DataTruncateStopReason.StoppedOnError;
                break;
            }

            if (result.SuccessCount == 0)
            {
                reason = DataTruncateStopReason.PassDeletedNothing;
                break;
            }
        }

        stopwatch.Stop();
        return new DataTruncateResult
        {
            DeletedCount = deleted,
            FailedCount = failedCount,
            Passes = passes,
            StopReason = reason,
            Errors = errors,
            Duration = stopwatch.Elapsed
        };
    }
}
