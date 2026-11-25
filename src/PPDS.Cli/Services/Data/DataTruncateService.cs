using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using DataverseProgressSnapshot = PPDS.Dataverse.Progress.ProgressSnapshot;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Deletes every record of a table in passes (<c>data truncate</c>). The table is read once, page by page in the order
/// of its primary id, so each record is attempted at most once per run and a record that cannot be deleted neither keeps
/// the run going nor hides the records after it (#1922).
/// </summary>
public interface IDataTruncateService
{
    /// <summary>Counts the records of <paramref name="entity"/> (aggregate count on its primary id column).</summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of records.</returns>
    /// <exception cref="PpdsException"><c>Query.ExecutionFailed</c> when the table's metadata or the count cannot be read.</exception>
    Task<int> CountRecordsAsync(string entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the records of <paramref name="entity"/>, reading pages of <see cref="DataTruncateOptions.BatchSize"/>
    /// records after the last primary id read, until the end of the table or until
    /// <see cref="DataTruncateResult.StopReason"/> says why it stopped earlier; then counts the records left.
    /// </summary>
    /// <param name="entity">Logical name of the table.</param>
    /// <param name="options">Batch size, error handling and custom-logic bypass.</param>
    /// <param name="progress">Receives the number of records processed so far; may be <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The counts of deleted, failed and remaining records and why the run ended.</returns>
    /// <exception cref="PpdsException"><c>Query.ExecutionFailed</c> when the table's metadata or a page cannot be read.</exception>
    Task<DataTruncateResult> TruncateAsync(
        string entity,
        DataTruncateOptions options,
        IProgressReporter? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Options for <see cref="IDataTruncateService.TruncateAsync"/>.</summary>
public sealed record DataTruncateOptions
{
    /// <summary>Records read and deleted per pass (1 to 1000).</summary>
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
    /// <summary>The table was read to its end: every record in it was attempted once.</summary>
    Completed,

    /// <summary>A pass had a failure and <see cref="DataTruncateOptions.ContinueOnError"/> is false.</summary>
    StoppedOnError,

    /// <summary>A record already attempted was read again, so the counts are not certain.</summary>
    DeletedRecordReadAgain,

    /// <summary>
    /// Dataverse returned no new paging cookie, or an empty page that claims more records, so the run could not read
    /// past the page it had.
    /// </summary>
    PagingUnavailable,

    /// <summary>The run was cancelled after it had deleted or attempted records.</summary>
    Cancelled,

    /// <summary>A read or a delete failed after the run had deleted or attempted records.</summary>
    Interrupted
}

/// <summary>The outcome of <see cref="IDataTruncateService.TruncateAsync"/>.</summary>
public sealed record DataTruncateResult
{
    /// <summary>Records deleted.</summary>
    public int DeletedCount { get; init; }

    /// <summary>Records that could not be deleted (each counted once).</summary>
    public int FailedCount { get; init; }

    /// <summary>The ids of the failed records the executor named, in the order they failed.</summary>
    public IReadOnlyList<Guid> FailedIds { get; init; } = Array.Empty<Guid>();

    /// <summary>Records left in the table after the run, or <see langword="null"/> when they could not be counted.</summary>
    public int? RemainingCount { get; init; }

    /// <summary>Delete passes made.</summary>
    public int Passes { get; init; }

    /// <summary>Why the run ended.</summary>
    public DataTruncateStopReason StopReason { get; init; }

    /// <summary>The errors of the failed records.</summary>
    public IReadOnlyList<BulkOperationError> Errors { get; init; } = Array.Empty<BulkOperationError>();

    /// <summary>Time taken.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// For <see cref="DataTruncateStopReason.Interrupted"/>: the error code of the failure (a <c>PpdsException</c>'s
    /// code, or the exception's type name), never its message.
    /// </summary>
    public string? InterruptionCode { get; init; }

    /// <summary>True when the table was read to its end with no failure and no record is left.</summary>
    public bool IsSuccess => StopReason == DataTruncateStopReason.Completed && FailedCount == 0 && RemainingCount == 0;
}

/// <inheritdoc />
public sealed class DataTruncateService : IDataTruncateService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly IMetadataQueryService _metadata;
    private readonly IBulkOperationExecutor _bulkExecutor;
    private readonly ILogger<DataTruncateService> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="DataTruncateService"/>.
    /// </summary>
    /// <param name="pool">Reads the pages and the counts.</param>
    /// <param name="metadata">Gives the table's primary id column.</param>
    /// <param name="bulkExecutor">Deletes each pass.</param>
    /// <param name="logger">Logger instance.</param>
    public DataTruncateService(
        IDataverseConnectionPool pool,
        IMetadataQueryService metadata,
        IBulkOperationExecutor bulkExecutor,
        ILogger<DataTruncateService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        _bulkExecutor = bulkExecutor ?? throw new ArgumentNullException(nameof(bulkExecutor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<int> CountRecordsAsync(string entity, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(entity)) throw new ArgumentNullException(nameof(entity));
        var primaryId = await ReadPrimaryIdAsync(entity, cancellationToken);
        return await CountAsync(entity, primaryId, cancellationToken);
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

        var primaryId = await ReadPrimaryIdAsync(entity, cancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var attempted = new HashSet<Guid>();
        var failed = new HashSet<Guid>();
        var failedIds = new List<Guid>();
        var errors = new List<BulkOperationError>();
        var deleted = 0;
        var failedCount = 0;
        var passes = 0;
        var page = 1;
        string? pagingCookie = null;
        DataTruncateStopReason reason;
        string? interruptionCode = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Each read continues after the last primary id of the previous one (the paging cookie), so a record that
                // failed is behind the read and cannot fill it, and every record is read once.
                var read = await ReadPageAsync(entity, primaryId, options.BatchSize, page, pagingCookie, cancellationToken);
                var fetched = read.Ids;

                if (fetched.Any(attempted.Contains))
                {
                    reason = DataTruncateStopReason.DeletedRecordReadAgain;
                    break;
                }

                var batch = fetched.Distinct().ToList();
                if (batch.Count > 0)
                {
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
                    foreach (var error in result.Errors)
                    {
                        var id = error.RecordId ??
                                 (error.Index >= 0 && error.Index < batch.Count ? batch[error.Index] : (Guid?)null);
                        if (id is { } failedId && failed.Add(failedId))
                        {
                            failedIds.Add(failedId);
                        }
                    }

                    _logger.LogDebug(
                        "Truncate pass {Pass} of {Entity}: {Deleted} deleted, {Failed} failed",
                        passes, entity, result.SuccessCount, result.FailureCount);

                    // A pass in which every delete fails does not end the run: the next page starts after it, so the
                    // failed records cannot hide the ones after them, and the run still ends at the end of the table.
                    if (result.FailureCount > 0 && !options.ContinueOnError)
                    {
                        reason = DataTruncateStopReason.StoppedOnError;
                        break;
                    }
                }
                else if (read.MoreRecords)
                {
                    // An empty page that claims more records gives nothing to continue after: stop rather than spin.
                    reason = DataTruncateStopReason.PagingUnavailable;
                    break;
                }

                if (!read.MoreRecords)
                {
                    reason = DataTruncateStopReason.Completed;
                    break;
                }

                if (string.IsNullOrEmpty(read.PagingCookie) || read.PagingCookie == pagingCookie)
                {
                    // Without a new cookie the next page would be counted by position (and the deletes have moved positions)
                    // or would repeat the last one.
                    reason = DataTruncateStopReason.PagingUnavailable;
                    break;
                }

                page++;
                pagingCookie = read.PagingCookie;
            }
        }
        catch (OperationCanceledException) when (passes > 0)
        {
            // Records were already deleted: report them rather than lose the counts.
            reason = DataTruncateStopReason.Cancelled;
        }
        catch (Exception ex) when (passes > 0)
        {
            _logger.LogError(ex, "Truncate of {Entity} failed after {Passes} passes", entity, passes);
            reason = DataTruncateStopReason.Interrupted;
            interruptionCode = ex is PpdsException ppds ? ppds.ErrorCode : ex.GetType().Name;
        }

        int? remaining = null;
        if (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                remaining = await CountAsync(entity, primaryId, cancellationToken);
            }
            catch (PpdsException ex)
            {
                _logger.LogWarning(ex, "Could not count the records left in {Entity} after truncate", entity);
            }
        }

        stopwatch.Stop();
        return new DataTruncateResult
        {
            DeletedCount = deleted,
            FailedCount = failedCount,
            FailedIds = failedIds,
            RemainingCount = remaining,
            Passes = passes,
            StopReason = reason,
            Errors = errors,
            Duration = stopwatch.Elapsed,
            InterruptionCode = interruptionCode
        };
    }

    private sealed record PageRead(IReadOnlyList<Guid> Ids, bool MoreRecords, string? PagingCookie);

    private async Task<PageRead> ReadPageAsync(
        string entity,
        string primaryId,
        int count,
        int page,
        string? pagingCookie,
        CancellationToken cancellationToken)
    {
        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(primaryId),
            PageInfo = new PagingInfo { Count = count, PageNumber = page, PagingCookie = pagingCookie }
        };
        query.AddOrder(primaryId, OrderType.Ascending);

        try
        {
            EntityCollection result;
            await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
            {
                result = await client.RetrieveMultipleAsync(query, cancellationToken);
            }

            var ids = result.Entities
                .Select(e => e.Contains(primaryId) && e[primaryId] is Guid id ? id : e.Id)
                .ToList();
            return new PageRead(ids, result.MoreRecords, result.PagingCookie);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read a page of {Entity} for truncate", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to read the records of '{entity}': {ex.Message}", ex);
        }
    }

    private async Task<int> CountAsync(string entity, string primaryId, CancellationToken cancellationToken)
    {
        var fetchXml =
            $"<fetch aggregate='true'><entity name='{SecurityElement.Escape(entity)}'>" +
            $"<attribute name='{SecurityElement.Escape(primaryId)}' alias='count' aggregate='count' />" +
            "</entity></fetch>";

        try
        {
            EntityCollection result;
            await using (var client = await _pool.GetClientAsync(cancellationToken: cancellationToken))
            {
                result = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml), cancellationToken);
            }

            if (result.Entities.Count > 0 && result.Entities[0].Contains("count") &&
                result.Entities[0]["count"] is AliasedValue { Value: { } value })
            {
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to count records for entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"Failed to count records for '{entity}': {ex.Message}", ex);
        }
    }

    private async Task<string> ReadPrimaryIdAsync(string entity, CancellationToken cancellationToken)
    {
        string? primaryId;
        try
        {
            var metadata = await _metadata.GetEntityAsync(
                entity,
                includeAttributes: false,
                includeRelationships: false,
                includeKeys: false,
                includePrivileges: false,
                cancellationToken: cancellationToken);
            primaryId = metadata.PrimaryIdAttribute;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read the primary id column of entity {Entity}", entity);
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"The primary id column of '{entity}' could not be read (does the table exist?). Nothing was deleted.", ex);
        }

        if (string.IsNullOrWhiteSpace(primaryId))
        {
            throw new PpdsException(ErrorCodes.Query.ExecutionFailed,
                $"The metadata of '{entity}' names no primary id column. Nothing was deleted.");
        }

        return primaryId;
    }
}
