using System.Collections.Generic;
using System.Threading;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// Helpers for creating test contexts and mock dependencies for plan node tests.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// Creates a minimal QueryPlanContext with mocked dependencies for testing.
    /// </summary>
    public static QueryPlanContext CreateTestContext()
    {
        var mockExecutor = new Mock<IQueryExecutor>();
        var mockEvaluator = new Mock<IExpressionEvaluator>();

        // Set up the evaluator to handle column expressions
        mockEvaluator
            .Setup(e => e.Evaluate(It.IsAny<ISqlExpression>(), It.IsAny<IReadOnlyDictionary<string, QueryValue>>()))
            .Returns((ISqlExpression expr, IReadOnlyDictionary<string, QueryValue> row) =>
            {
                if (expr is SqlColumnExpression colExpr)
                {
                    var colName = colExpr.Column.GetFullName();
                    if (row.TryGetValue(colName, out var qv))
                        return qv.Value;
                }
                return null;
            });

        return new QueryPlanContext(mockExecutor.Object, mockEvaluator.Object);
    }

    /// <summary>
    /// Collects all rows from an IQueryPlanNode into a list.
    /// </summary>
    public static async System.Threading.Tasks.Task<List<QueryRow>> CollectRowsAsync(
        IQueryPlanNode node,
        QueryPlanContext? context = null)
    {
        context ??= CreateTestContext();
        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context, CancellationToken.None))
        {
            rows.Add(row);
        }
        return rows;
    }
}
