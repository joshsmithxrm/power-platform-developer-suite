using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Dataverse.Query.Execution;

/// <summary>
/// Executes a query plan by consuming the root node's IAsyncEnumerable
/// and collecting results into QueryResult format.
/// </summary>
public sealed class PlanExecutor
{
    /// <summary>
    /// Executes a query plan and collects all results.
    /// </summary>
    /// <param name="planResult">The plan to execute.</param>
    /// <param name="context">The execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The collected query result.</returns>
    public async Task<QueryResult> ExecuteAsync(
        QueryPlanResult planResult,
        QueryPlanContext context,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var records = new List<IReadOnlyDictionary<string, QueryValue>>();
        IReadOnlyList<QueryColumn>? columns = null;

        await foreach (var row in planResult.RootNode.ExecuteAsync(context, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();

            records.Add(row.Values);
            context.Statistics.IncrementRowsOutput();

            // Infer columns from first row if not yet determined
            if (columns == null)
            {
                columns = InferColumnsFromRow(row);
            }
        }

        stopwatch.Stop();
        context.Statistics.AddExecutionTimeMs(stopwatch.ElapsedMilliseconds);

        return new QueryResult
        {
            EntityLogicalName = planResult.EntityLogicalName,
            Columns = columns ?? Array.Empty<QueryColumn>(),
            Records = records,
            Count = records.Count,
            TotalCount = context.Statistics.LastTotalCount,
            MoreRecords = context.Statistics.LastMoreRecords,
            PagingCookie = context.Statistics.LastPagingCookie,
            PageNumber = context.Statistics.LastPageNumber > 0
                ? context.Statistics.LastPageNumber
                : 1,
            ExecutionTimeMs = stopwatch.ElapsedMilliseconds,
            ExecutedFetchXml = planResult.FetchXml
        };
    }

    private static List<QueryColumn> InferColumnsFromRow(QueryRow row)
    {
        var columns = new List<QueryColumn>();
        foreach (var key in row.Values.Keys)
        {
            columns.Add(new QueryColumn
            {
                LogicalName = key,
                DataType = QueryColumnType.Unknown
            });
        }
        return columns;
    }
}
