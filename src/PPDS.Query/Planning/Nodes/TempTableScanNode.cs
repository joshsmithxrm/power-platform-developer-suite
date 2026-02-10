using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Reads rows from a temp table stored in a <see cref="SessionContext"/>.
/// Used for SELECT FROM #tempTable queries.
/// </summary>
public sealed class TempTableScanNode : IQueryPlanNode
{
    private readonly SessionContext _sessionContext;

    /// <summary>The temp table name (starts with #).</summary>
    public string TableName { get; }

    /// <inheritdoc />
    public string Description => $"TempTableScan: {TableName}";

    /// <inheritdoc />
    public long EstimatedRows => -1; // Unknown until execution

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="TempTableScanNode"/> class.
    /// </summary>
    /// <param name="tableName">The temp table name (must start with #).</param>
    /// <param name="sessionContext">The session context holding temp table data.</param>
    public TempTableScanNode(string tableName, SessionContext sessionContext)
    {
        if (string.IsNullOrEmpty(tableName) || !tableName.StartsWith("#"))
            throw new ArgumentException("Temp table name must start with #.", nameof(tableName));

        TableName = tableName;
        _sessionContext = sessionContext ?? throw new ArgumentNullException(nameof(sessionContext));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rows = _sessionContext.GetTempTableRows(TableName);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
        }

        await System.Threading.Tasks.Task.CompletedTask; // Ensure async signature
    }
}
