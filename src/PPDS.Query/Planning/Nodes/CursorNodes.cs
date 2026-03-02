using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;

namespace PPDS.Query.Planning.Nodes;

/// <summary>
/// Holds the state of an open cursor: the materialized result set and current position.
/// </summary>
public sealed class CursorState
{
    /// <summary>The materialized result rows from the cursor's query.</summary>
    public List<QueryRow> Rows { get; }

    /// <summary>Current fetch position (0-based index into Rows). -1 means before first row.</summary>
    public int Position { get; set; }

    /// <summary>Whether the cursor is currently open.</summary>
    public bool IsOpen { get; set; }

    /// <summary>The plan node for the cursor's query (used when OPEN executes the query).</summary>
    public IQueryPlanNode? QueryNode { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CursorState"/> class.
    /// </summary>
    public CursorState()
    {
        Rows = new List<QueryRow>();
        Position = -1;
        IsOpen = false;
    }
}

/// <summary>
/// Plan node for DECLARE cursor_name CURSOR FOR SELECT ...
/// Registers the cursor in the SessionContext with its query plan but does not execute it.
/// </summary>
public sealed class DeclareCursorNode : IQueryPlanNode
{
    /// <summary>The cursor name.</summary>
    public string CursorName { get; }

    /// <summary>The query plan node for the cursor's SELECT statement.</summary>
    public IQueryPlanNode QueryNode { get; }

    /// <summary>The session context to register the cursor in.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => $"DeclareCursor: {CursorName}";

    /// <inheritdoc />
    public long EstimatedRows => 0;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => new[] { QueryNode };

    /// <summary>
    /// Initializes a new instance of the <see cref="DeclareCursorNode"/> class.
    /// </summary>
    public DeclareCursorNode(string cursorName, IQueryPlanNode queryNode, SessionContext session)
    {
        CursorName = cursorName ?? throw new ArgumentNullException(nameof(cursorName));
        QueryNode = queryNode ?? throw new ArgumentNullException(nameof(queryNode));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Session.DeclareCursor(CursorName, QueryNode);
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Plan node for OPEN cursor_name.
/// Executes the cursor's query and materializes the results into the cursor state.
/// </summary>
public sealed class OpenCursorNode : IQueryPlanNode
{
    /// <summary>The cursor name to open.</summary>
    public string CursorName { get; }

    /// <summary>The session context containing cursor state.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => $"OpenCursor: {CursorName}";

    /// <inheritdoc />
    public long EstimatedRows => 0;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenCursorNode"/> class.
    /// </summary>
    public OpenCursorNode(string cursorName, SessionContext session)
    {
        CursorName = cursorName ?? throw new ArgumentNullException(nameof(cursorName));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Session.OpenCursorAsync(CursorName, context, cancellationToken);
        yield break;
    }
}

/// <summary>
/// Plan node for FETCH NEXT FROM cursor_name INTO @var1, @var2, ...
/// Reads the next row from the cursor and assigns column values to variables.
/// Updates @@FETCH_STATUS in the session.
/// </summary>
public sealed class FetchCursorNode : IQueryPlanNode
{
    /// <summary>The cursor name to fetch from.</summary>
    public string CursorName { get; }

    /// <summary>Variable names to assign fetched column values into (in column order).</summary>
    public IReadOnlyList<string> IntoVariables { get; }

    /// <summary>The session context containing cursor state.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => $"FetchCursor: {CursorName} INTO {string.Join(", ", IntoVariables)}";

    /// <inheritdoc />
    public long EstimatedRows => 1;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="FetchCursorNode"/> class.
    /// </summary>
    public FetchCursorNode(string cursorName, IReadOnlyList<string> intoVariables, SessionContext session)
    {
        CursorName = cursorName ?? throw new ArgumentNullException(nameof(cursorName));
        IntoVariables = intoVariables ?? throw new ArgumentNullException(nameof(intoVariables));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var row = Session.FetchNextFromCursor(CursorName);

        if (row != null && context.VariableScope != null)
        {
            // Assign column values to variables in order
            var columnNames = new List<string>(row.Values.Keys);
            for (int i = 0; i < IntoVariables.Count && i < columnNames.Count; i++)
            {
                var value = row.Values[columnNames[i]].Value;
                context.VariableScope.Set(IntoVariables[i], value);
            }
        }

        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Plan node for CLOSE cursor_name.
/// Marks the cursor as closed but keeps the declaration so it can be reopened.
/// </summary>
public sealed class CloseCursorNode : IQueryPlanNode
{
    /// <summary>The cursor name to close.</summary>
    public string CursorName { get; }

    /// <summary>The session context containing cursor state.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => $"CloseCursor: {CursorName}";

    /// <inheritdoc />
    public long EstimatedRows => 0;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="CloseCursorNode"/> class.
    /// </summary>
    public CloseCursorNode(string cursorName, SessionContext session)
    {
        CursorName = cursorName ?? throw new ArgumentNullException(nameof(cursorName));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Session.CloseCursor(CursorName);
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Plan node for DEALLOCATE cursor_name.
/// Completely removes the cursor from the session.
/// </summary>
public sealed class DeallocateCursorNode : IQueryPlanNode
{
    /// <summary>The cursor name to deallocate.</summary>
    public string CursorName { get; }

    /// <summary>The session context containing cursor state.</summary>
    public SessionContext Session { get; }

    /// <inheritdoc />
    public string Description => $"DeallocateCursor: {CursorName}";

    /// <inheritdoc />
    public long EstimatedRows => 0;

    /// <inheritdoc />
    public IReadOnlyList<IQueryPlanNode> Children => Array.Empty<IQueryPlanNode>();

    /// <summary>
    /// Initializes a new instance of the <see cref="DeallocateCursorNode"/> class.
    /// </summary>
    public DeallocateCursorNode(string cursorName, SessionContext session)
    {
        CursorName = cursorName ?? throw new ArgumentNullException(nameof(cursorName));
        Session = session ?? throw new ArgumentNullException(nameof(session));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<QueryRow> ExecuteAsync(
        QueryPlanContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Session.DeallocateCursor(CursorName);
        await Task.CompletedTask;
        yield break;
    }
}
