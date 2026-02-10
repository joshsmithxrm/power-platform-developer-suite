using System.Collections.Generic;
using System.Threading;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

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

        return new QueryPlanContext(mockExecutor.Object);
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
