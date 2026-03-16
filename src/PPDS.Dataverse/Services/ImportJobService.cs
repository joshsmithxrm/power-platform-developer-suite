using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Services;

/// <summary>
/// Service for querying and monitoring Dataverse import jobs.
/// </summary>
public class ImportJobService : IImportJobService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<ImportJobService> _logger;

    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Initializes a new instance of the <see cref="ImportJobService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="logger">The logger.</param>
    public ImportJobService(IDataverseConnectionPool pool, ILogger<ImportJobService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<ImportJobInfo>> ListAsync(
        string? solutionName = null,
        int top = 50,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Note: ImportJob entity doesn't support $skip, so we use TopCount only
        var query = new QueryExpression(ImportJob.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                ImportJob.Fields.ImportJobId,
                ImportJob.Fields.Name,
                ImportJob.Fields.SolutionName,
                ImportJob.Fields.SolutionId,
                ImportJob.Fields.Progress,
                ImportJob.Fields.StartedOn,
                ImportJob.Fields.CompletedOn,
                ImportJob.Fields.CreatedOn,
                ImportJob.Fields.CreatedBy),
            TopCount = top,
            Orders = { new OrderExpression(ImportJob.Fields.CreatedOn, OrderType.Descending) }
        };

        if (!string.IsNullOrWhiteSpace(solutionName))
        {
            query.Criteria.AddCondition(ImportJob.Fields.SolutionName, ConditionOperator.Contains, solutionName);
        }

        _logger.LogDebug("Querying import jobs with solutionName filter: {SolutionName}, top: {Top}", solutionName, top);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.Select(MapToImportJobInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<ImportJobInfo?> GetAsync(Guid importJobId, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(ImportJob.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(
                ImportJob.Fields.ImportJobId,
                ImportJob.Fields.Name,
                ImportJob.Fields.SolutionName,
                ImportJob.Fields.SolutionId,
                ImportJob.Fields.Progress,
                ImportJob.Fields.StartedOn,
                ImportJob.Fields.CompletedOn,
                ImportJob.Fields.CreatedOn,
                ImportJob.Fields.CreatedBy),
            TopCount = 1
        };

        query.Criteria.AddCondition(ImportJob.Fields.ImportJobId, ConditionOperator.Equal, importJobId);

        _logger.LogDebug("Getting import job: {ImportJobId}", importJobId);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.FirstOrDefault() is { } entity ? MapToImportJobInfo(entity) : null;
    }

    /// <inheritdoc />
    public async Task<string?> GetDataAsync(Guid importJobId, CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Data is an expensive field - retrieve separately
        var query = new QueryExpression(ImportJob.EntityLogicalName)
        {
            ColumnSet = new ColumnSet(ImportJob.Fields.Data),
            TopCount = 1
        };

        query.Criteria.AddCondition(ImportJob.Fields.ImportJobId, ConditionOperator.Equal, importJobId);

        _logger.LogDebug("Getting import job data: {ImportJobId}", importJobId);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        return results.Entities.FirstOrDefault()?.GetAttributeValue<string>(ImportJob.Fields.Data);
    }

    /// <inheritdoc />
    public async Task<ImportJobInfo> WaitForCompletionAsync(
        Guid importJobId,
        TimeSpan? pollInterval = null,
        TimeSpan? timeout = null,
        Action<ImportJobInfo>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? DefaultPollInterval;
        var maxWait = timeout ?? DefaultTimeout;
        var startTime = DateTime.UtcNow;

        _logger.LogInformation("Waiting for import job {ImportJobId} to complete (timeout: {Timeout})", importJobId, maxWait);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = await GetAsync(importJobId, cancellationToken);

            if (job == null)
            {
                throw new InvalidOperationException($"Import job {importJobId} not found.");
            }

            onProgress?.Invoke(job);

            if (job.IsComplete)
            {
                _logger.LogInformation("Import job {ImportJobId} completed with progress: {Progress}%", importJobId, job.Progress);
                return job;
            }

            if (DateTime.UtcNow - startTime > maxWait)
            {
                throw new TimeoutException($"Import job {importJobId} did not complete within {maxWait.TotalMinutes} minutes.");
            }

            _logger.LogDebug("Import job {ImportJobId} progress: {Progress}%, waiting {Interval}...", importJobId, job.Progress, interval);
            await Task.Delay(interval, cancellationToken);
        }
    }

    private static ImportJobInfo MapToImportJobInfo(Entity entity)
    {
        var completedOn = entity.GetAttributeValue<DateTime?>(ImportJob.Fields.CompletedOn);
        var progress = entity.GetAttributeValue<double?>(ImportJob.Fields.Progress) ?? 0;
        var createdByRef = entity.GetAttributeValue<EntityReference>(ImportJob.Fields.CreatedBy);

        return new ImportJobInfo(
            entity.Id,
            entity.GetAttributeValue<string>(ImportJob.Fields.Name),
            entity.GetAttributeValue<string>(ImportJob.Fields.SolutionName),
            entity.GetAttributeValue<Guid?>(ImportJob.Fields.SolutionId),
            progress,
            entity.GetAttributeValue<DateTime?>(ImportJob.Fields.StartedOn),
            completedOn,
            entity.GetAttributeValue<DateTime?>(ImportJob.Fields.CreatedOn),
            completedOn.HasValue,
            createdByRef?.Name);
    }
}
