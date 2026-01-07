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
/// Service for querying and managing plugin trace logs.
/// </summary>
public class PluginTraceService : IPluginTraceService
{
    private readonly IDataverseConnectionPool _pool;
    private readonly ILogger<PluginTraceService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginTraceService"/> class.
    /// </summary>
    /// <param name="pool">The connection pool.</param>
    /// <param name="logger">The logger.</param>
    public PluginTraceService(
        IDataverseConnectionPool pool,
        ILogger<PluginTraceService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<PluginTraceInfo>> ListAsync(
        PluginTraceFilter? filter = null,
        int top = 100,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = BuildListQuery(filter, top);

        _logger.LogDebug("Querying plugin traces with top: {Top}", top);
        var result = await client.RetrieveMultipleAsync(query, cancellationToken);

        return result.Entities.Select(MapToPluginTraceInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<PluginTraceDetail?> GetAsync(
        Guid traceId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            var trace = await client.RetrieveAsync(
                PluginTraceLog.EntityLogicalName,
                traceId,
                new ColumnSet(true),
                cancellationToken);

            return MapToPluginTraceDetail(trace);
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<List<PluginTraceInfo>> GetRelatedAsync(
        Guid correlationId,
        int top = 1000,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(PluginTraceLog.EntityLogicalName)
        {
            ColumnSet = GetListColumnSet(),
            TopCount = top,
            Orders = { new OrderExpression(PluginTraceLog.Fields.CreatedOn, OrderType.Ascending) }
        };

        query.Criteria.AddCondition(PluginTraceLog.Fields.CorrelationId, ConditionOperator.Equal, correlationId);

        _logger.LogDebug("Getting related traces for correlation ID: {CorrelationId}", correlationId);
        var result = await client.RetrieveMultipleAsync(query, cancellationToken);

        return result.Entities.Select(MapToPluginTraceInfo).ToList();
    }

    /// <inheritdoc />
    public async Task<List<TimelineNode>> BuildTimelineAsync(
        Guid correlationId,
        CancellationToken cancellationToken = default)
    {
        var traces = await GetRelatedAsync(correlationId, 1000, cancellationToken);
        return TimelineHierarchyBuilder.BuildWithPositioning(traces);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Guid traceId,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        try
        {
            await client.DeleteAsync(PluginTraceLog.EntityLogicalName, traceId, cancellationToken);
            _logger.LogInformation("Deleted plugin trace: {TraceId}", traceId);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("does not exist"))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<int> DeleteByIdsAsync(
        IEnumerable<Guid> traceIds,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var ids = traceIds.ToList();
        if (ids.Count == 0) return 0;

        int deleted = 0;

        // Use pool parallelism for efficient deletion (ADR-0002)
        var parallelism = Math.Min(_pool.GetTotalRecommendedParallelism(), ids.Count);

        await Parallel.ForEachAsync(
            ids,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = parallelism,
                CancellationToken = cancellationToken
            },
            async (traceId, ct) =>
            {
                await using var client = await _pool.GetClientAsync(cancellationToken: ct);
                try
                {
                    await client.DeleteAsync(PluginTraceLog.EntityLogicalName, traceId, ct);
                    var count = Interlocked.Increment(ref deleted);
                    progress?.Report(count);
                }
                catch (Exception ex) when (ex.Message.Contains("does not exist"))
                {
                    // Trace already deleted, skip
                }
            });

        _logger.LogInformation("Deleted {Count} of {Total} plugin traces", deleted, ids.Count);
        return deleted;
    }

    /// <inheritdoc />
    public async Task<int> DeleteByFilterAsync(
        PluginTraceFilter filter,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        int totalDeleted = 0;

        // Page through all matching records
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var traces = await ListAsync(filter, top: 5000, cancellationToken);
            if (traces.Count == 0)
            {
                break;
            }

            var ids = traces.Select(t => t.Id).ToList();

            // Create wrapper progress that adds to total
            var batchProgress = new Progress<int>(count =>
            {
                progress?.Report(totalDeleted + count);
            });

            var batchDeleted = await DeleteByIdsAsync(ids, batchProgress, cancellationToken);
            totalDeleted += batchDeleted;

            // If we deleted fewer than requested, we're done
            if (traces.Count < 5000)
            {
                break;
            }
        }

        return totalDeleted;
    }

    /// <inheritdoc />
    public async Task<int> DeleteAllAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await DeleteByFilterAsync(new PluginTraceFilter(), progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteOlderThanAsync(
        TimeSpan olderThan,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var filter = new PluginTraceFilter { CreatedBefore = cutoff };
        return await DeleteByFilterAsync(filter, progress, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<PluginTraceSettings> GetSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // Query the single organization record for the trace setting
        var query = new QueryExpression("organization")
        {
            ColumnSet = new ColumnSet("plugintracelogsetting"),
            TopCount = 1
        };

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        var org = result.Entities.FirstOrDefault();

        var settingValue = org?.GetAttributeValue<OptionSetValue>("plugintracelogsetting")?.Value ?? 0;

        return new PluginTraceSettings
        {
            Setting = (PluginTraceLogSetting)settingValue
        };
    }

    /// <inheritdoc />
    public async Task SetSettingsAsync(
        PluginTraceLogSetting setting,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        // First get the organization ID
        var query = new QueryExpression("organization")
        {
            ColumnSet = new ColumnSet("organizationid"),
            TopCount = 1
        };

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        var org = result.Entities.FirstOrDefault()
            ?? throw new InvalidOperationException("Organization record not found.");

        // Update the trace setting
        var update = new Entity("organization", org.Id)
        {
            ["plugintracelogsetting"] = new OptionSetValue((int)setting)
        };

        await client.UpdateAsync(update, cancellationToken);
        _logger.LogInformation("Set plugin trace log setting to: {Setting}", setting);
    }

    /// <inheritdoc />
    public async Task<int> CountAsync(
        PluginTraceFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await using var client = await _pool.GetClientAsync(cancellationToken: cancellationToken);

        var fetchXml = BuildCountFetchXml(filter);
        var result = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml), cancellationToken);

        var countEntity = result.Entities.FirstOrDefault();
        if (countEntity == null) return 0;

        var countValue = countEntity.GetAttributeValue<AliasedValue>("count");
        return countValue?.Value is int count ? count : 0;
    }

    private static string BuildCountFetchXml(PluginTraceFilter? filter)
    {
        var conditions = new System.Text.StringBuilder();

        if (filter != null)
        {
            if (!string.IsNullOrEmpty(filter.TypeName))
                conditions.Append($"<condition attribute=\"typename\" operator=\"like\" value=\"%{EscapeXml(filter.TypeName)}%\" />");

            if (!string.IsNullOrEmpty(filter.MessageName))
                conditions.Append($"<condition attribute=\"messagename\" operator=\"like\" value=\"%{EscapeXml(filter.MessageName)}%\" />");

            if (!string.IsNullOrEmpty(filter.PrimaryEntity))
                conditions.Append($"<condition attribute=\"primaryentity\" operator=\"like\" value=\"%{EscapeXml(filter.PrimaryEntity)}%\" />");

            if (filter.Mode.HasValue)
                conditions.Append($"<condition attribute=\"mode\" operator=\"eq\" value=\"{(int)filter.Mode.Value}\" />");

            if (filter.OperationType.HasValue)
                conditions.Append($"<condition attribute=\"operationtype\" operator=\"eq\" value=\"{(int)filter.OperationType.Value}\" />");

            if (filter.MinDepth.HasValue)
                conditions.Append($"<condition attribute=\"depth\" operator=\"ge\" value=\"{filter.MinDepth.Value}\" />");

            if (filter.MaxDepth.HasValue)
                conditions.Append($"<condition attribute=\"depth\" operator=\"le\" value=\"{filter.MaxDepth.Value}\" />");

            if (filter.CreatedAfter.HasValue)
                conditions.Append($"<condition attribute=\"createdon\" operator=\"on-or-after\" value=\"{filter.CreatedAfter.Value:o}\" />");

            if (filter.CreatedBefore.HasValue)
                conditions.Append($"<condition attribute=\"createdon\" operator=\"on-or-before\" value=\"{filter.CreatedBefore.Value:o}\" />");

            if (filter.MinDurationMs.HasValue)
                conditions.Append($"<condition attribute=\"performanceexecutionduration\" operator=\"ge\" value=\"{filter.MinDurationMs.Value}\" />");

            if (filter.MaxDurationMs.HasValue)
                conditions.Append($"<condition attribute=\"performanceexecutionduration\" operator=\"le\" value=\"{filter.MaxDurationMs.Value}\" />");

            if (filter.HasException.HasValue)
            {
                if (filter.HasException.Value)
                    conditions.Append("<condition attribute=\"exceptiondetails\" operator=\"not-null\" />");
                else
                    conditions.Append("<condition attribute=\"exceptiondetails\" operator=\"null\" />");
            }

            if (filter.CorrelationId.HasValue)
                conditions.Append($"<condition attribute=\"correlationid\" operator=\"eq\" value=\"{filter.CorrelationId.Value}\" />");

            if (filter.RequestId.HasValue)
                conditions.Append($"<condition attribute=\"requestid\" operator=\"eq\" value=\"{filter.RequestId.Value}\" />");

            if (filter.PluginStepId.HasValue)
                conditions.Append($"<condition attribute=\"pluginstepid\" operator=\"eq\" value=\"{filter.PluginStepId.Value}\" />");
        }

        return $@"<fetch aggregate=""true"">
  <entity name=""plugintracelog"">
    <attribute name=""plugintracelogid"" alias=""count"" aggregate=""count"" />
    <filter type=""and"">{conditions}</filter>
  </entity>
</fetch>";
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static QueryExpression BuildListQuery(PluginTraceFilter? filter, int top)
    {
        var query = new QueryExpression(PluginTraceLog.EntityLogicalName)
        {
            ColumnSet = GetListColumnSet(),
            TopCount = top
        };

        // Default ordering by created date descending (most recent first)
        var orderBy = filter?.OrderBy ?? "createdon desc";
        var orderParts = orderBy.Split(' ', 2);
        var orderField = orderParts[0];
        var orderDir = orderParts.Length > 1 && orderParts[1].Equals("asc", StringComparison.OrdinalIgnoreCase)
            ? OrderType.Ascending
            : OrderType.Descending;
        query.Orders.Add(new OrderExpression(orderField, orderDir));

        if (filter == null) return query;

        // Apply filter conditions
        if (!string.IsNullOrEmpty(filter.TypeName))
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.TypeName, ConditionOperator.Like, $"%{filter.TypeName}%");
        }

        if (!string.IsNullOrEmpty(filter.MessageName))
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.MessageName, ConditionOperator.Like, $"%{filter.MessageName}%");
        }

        if (!string.IsNullOrEmpty(filter.PrimaryEntity))
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.PrimaryEntity, ConditionOperator.Like, $"%{filter.PrimaryEntity}%");
        }

        if (filter.Mode.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.Mode, ConditionOperator.Equal, (int)filter.Mode.Value);
        }

        if (filter.OperationType.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.OperationType, ConditionOperator.Equal, (int)filter.OperationType.Value);
        }

        if (filter.MinDepth.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.Depth, ConditionOperator.GreaterEqual, filter.MinDepth.Value);
        }

        if (filter.MaxDepth.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.Depth, ConditionOperator.LessEqual, filter.MaxDepth.Value);
        }

        if (filter.CreatedAfter.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.CreatedOn, ConditionOperator.OnOrAfter, filter.CreatedAfter.Value);
        }

        if (filter.CreatedBefore.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.CreatedOn, ConditionOperator.OnOrBefore, filter.CreatedBefore.Value);
        }

        if (filter.MinDurationMs.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.PerformanceExecutionDuration, ConditionOperator.GreaterEqual, filter.MinDurationMs.Value);
        }

        if (filter.MaxDurationMs.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.PerformanceExecutionDuration, ConditionOperator.LessEqual, filter.MaxDurationMs.Value);
        }

        if (filter.HasException.HasValue)
        {
            if (filter.HasException.Value)
            {
                query.Criteria.AddCondition(PluginTraceLog.Fields.ExceptionDetails, ConditionOperator.NotNull);
            }
            else
            {
                query.Criteria.AddCondition(PluginTraceLog.Fields.ExceptionDetails, ConditionOperator.Null);
            }
        }

        if (filter.CorrelationId.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.CorrelationId, ConditionOperator.Equal, filter.CorrelationId.Value);
        }

        if (filter.RequestId.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.RequestId, ConditionOperator.Equal, filter.RequestId.Value);
        }

        if (filter.PluginStepId.HasValue)
        {
            query.Criteria.AddCondition(PluginTraceLog.Fields.PluginStepId, ConditionOperator.Equal, filter.PluginStepId.Value);
        }

        return query;
    }

    private static ColumnSet GetListColumnSet()
    {
        // Minimal columns for list view (excludes large text fields)
        return new ColumnSet(
            PluginTraceLog.Fields.PluginTraceLogId,
            PluginTraceLog.Fields.TypeName,
            PluginTraceLog.Fields.MessageName,
            PluginTraceLog.Fields.PrimaryEntity,
            PluginTraceLog.Fields.Mode,
            PluginTraceLog.Fields.OperationType,
            PluginTraceLog.Fields.Depth,
            PluginTraceLog.Fields.CreatedOn,
            PluginTraceLog.Fields.PerformanceExecutionDuration,
            PluginTraceLog.Fields.ExceptionDetails, // Only check if non-null for HasException
            PluginTraceLog.Fields.CorrelationId,
            PluginTraceLog.Fields.RequestId,
            PluginTraceLog.Fields.PluginStepId);
    }

    private static PluginTraceInfo MapToPluginTraceInfo(Entity trace)
    {
        var modeValue = trace.GetAttributeValue<OptionSetValue>(PluginTraceLog.Fields.Mode);
        var opTypeValue = trace.GetAttributeValue<OptionSetValue>(PluginTraceLog.Fields.OperationType);

        return new PluginTraceInfo
        {
            Id = trace.Id,
            TypeName = trace.GetAttributeValue<string>(PluginTraceLog.Fields.TypeName) ?? string.Empty,
            MessageName = trace.GetAttributeValue<string>(PluginTraceLog.Fields.MessageName),
            PrimaryEntity = trace.GetAttributeValue<string>(PluginTraceLog.Fields.PrimaryEntity),
            Mode = modeValue != null ? (PluginTraceMode)modeValue.Value : PluginTraceMode.Synchronous,
            OperationType = opTypeValue != null ? (PluginTraceOperationType)opTypeValue.Value : PluginTraceOperationType.Unknown,
            Depth = trace.GetAttributeValue<int?>(PluginTraceLog.Fields.Depth) ?? 1,
            CreatedOn = trace.GetAttributeValue<DateTime?>(PluginTraceLog.Fields.CreatedOn) ?? DateTime.MinValue,
            DurationMs = trace.GetAttributeValue<int?>(PluginTraceLog.Fields.PerformanceExecutionDuration),
            HasException = !string.IsNullOrEmpty(trace.GetAttributeValue<string>(PluginTraceLog.Fields.ExceptionDetails)),
            CorrelationId = trace.GetAttributeValue<Guid?>(PluginTraceLog.Fields.CorrelationId),
            RequestId = trace.GetAttributeValue<Guid?>(PluginTraceLog.Fields.RequestId),
            PluginStepId = trace.GetAttributeValue<Guid?>(PluginTraceLog.Fields.PluginStepId)
        };
    }

    private static PluginTraceDetail MapToPluginTraceDetail(Entity trace)
    {
        var info = MapToPluginTraceInfo(trace);
        var createdBy = trace.GetAttributeValue<EntityReference>(PluginTraceLog.Fields.CreatedBy);
        var createdOnBehalfBy = trace.GetAttributeValue<EntityReference>(PluginTraceLog.Fields.CreatedOnBehalfBy);

        return new PluginTraceDetail
        {
            Id = info.Id,
            TypeName = info.TypeName,
            MessageName = info.MessageName,
            PrimaryEntity = info.PrimaryEntity,
            Mode = info.Mode,
            OperationType = info.OperationType,
            Depth = info.Depth,
            CreatedOn = info.CreatedOn,
            DurationMs = info.DurationMs,
            HasException = info.HasException,
            CorrelationId = info.CorrelationId,
            RequestId = info.RequestId,
            PluginStepId = info.PluginStepId,
            ConstructorDurationMs = trace.GetAttributeValue<int?>(PluginTraceLog.Fields.PerformanceConstructorDuration),
            ExecutionStartTime = trace.GetAttributeValue<DateTime?>(PluginTraceLog.Fields.PerformanceExecutionStartTime),
            ConstructorStartTime = trace.GetAttributeValue<DateTime?>(PluginTraceLog.Fields.PerformanceConstructorStartTime),
            ExceptionDetails = trace.GetAttributeValue<string>(PluginTraceLog.Fields.ExceptionDetails),
            MessageBlock = trace.GetAttributeValue<string>(PluginTraceLog.Fields.MessageBlock),
            Configuration = trace.GetAttributeValue<string>(PluginTraceLog.Fields.Configuration),
            SecureConfiguration = trace.GetAttributeValue<string>(PluginTraceLog.Fields.SecureConfiguration),
            Profile = trace.GetAttributeValue<string>(PluginTraceLog.Fields.Profile),
            OrganizationId = trace.GetAttributeValue<Guid?>(PluginTraceLog.Fields.OrganizationId),
            PersistenceKey = trace.GetAttributeValue<Guid?>(PluginTraceLog.Fields.PersistenceKey),
            IsSystemCreated = trace.GetAttributeValue<bool?>(PluginTraceLog.Fields.IsSystemCreated) ?? false,
            CreatedById = createdBy?.Id,
            CreatedOnBehalfById = createdOnBehalfBy?.Id
        };
    }
}
