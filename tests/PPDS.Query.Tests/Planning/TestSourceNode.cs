using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Tests.Planning;

/// <summary>
/// A simple in-memory source node for testing. Yields pre-built rows.
/// </summary>
internal sealed class TestSourceNode : IQueryPlanNode
{
    private readonly List<QueryRow> _rows;
    private readonly string _entityName;

    public string Description => $"TestSource: {_entityName} ({_rows.Count} rows)";
    public long EstimatedRows => _rows.Count;
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    public TestSourceNode(string entityName, List<QueryRow> rows)
    {
        _entityName = entityName;
        _rows = rows;
    }

    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var row in _rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }
        await System.Threading.Tasks.Task.CompletedTask;
    }

    /// <summary>
    /// Creates a QueryRow from a dictionary of column name to value.
    /// </summary>
    public static QueryRow MakeRow(string entityName, params (string column, object? value)[] columns)
    {
        var dict = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in columns)
        {
            dict[column] = QueryValue.Simple(value);
        }
        return new QueryRow(dict, entityName);
    }

    /// <summary>
    /// Creates a test source from column/value tuples.
    /// </summary>
    public static TestSourceNode Create(string entityName, params QueryRow[] rows)
    {
        return new TestSourceNode(entityName, new List<QueryRow>(rows));
    }
}
