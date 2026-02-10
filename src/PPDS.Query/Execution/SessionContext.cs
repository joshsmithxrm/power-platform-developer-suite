using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Query.Execution;

/// <summary>
/// Holds session-scoped state for a query execution session, including temp table data,
/// cursor state, and impersonation context.
/// Temp tables (names starting with #) are stored in memory and persist across
/// statements within the same session.
/// </summary>
public sealed class SessionContext
{
    private readonly Dictionary<string, TempTable> _tempTables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CursorState> _cursors = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The current @@FETCH_STATUS value.
    /// 0 = success, -1 = past end of cursor, -2 = row not found.
    /// </summary>
    public int FetchStatus { get; set; } = -1;

    /// <summary>@@ERROR value — the error number from the last statement. 0 means success.</summary>
    public int ErrorNumber { get; set; }

    /// <summary>ERROR_MESSAGE() value — the error message from the last caught exception.</summary>
    public string ErrorMessage { get; set; } = string.Empty;

    /// <summary>
    /// Optional CallerObjectId for impersonation (EXECUTE AS USER).
    /// When set, Dataverse requests should use this as the CallerObjectId.
    /// Null means no impersonation is active.
    /// </summary>
    public Guid? CallerObjectId { get; set; }

    // ════════════════════════════════════════════════════════════════════
    //  Temp Table operations
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a temp table with the specified name and column definitions.
    /// Throws if the table already exists.
    /// </summary>
    /// <param name="tableName">The temp table name (must start with #).</param>
    /// <param name="columns">Column names for the table.</param>
    public void CreateTempTable(string tableName, IReadOnlyList<string> columns)
    {
        if (!tableName.StartsWith("#"))
            throw new ArgumentException("Temp table name must start with #.", nameof(tableName));

        if (_tempTables.ContainsKey(tableName))
            throw new InvalidOperationException($"There is already an object named '{tableName}' in the session.");

        _tempTables[tableName] = new TempTable(tableName, columns);
    }

    /// <summary>
    /// Inserts a row into the specified temp table.
    /// </summary>
    /// <param name="tableName">The temp table name.</param>
    /// <param name="row">The row to insert.</param>
    public void InsertIntoTempTable(string tableName, QueryRow row)
    {
        if (!_tempTables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException($"Invalid object name '{tableName}'.");

        table.Rows.Add(row);
    }

    /// <summary>
    /// Inserts multiple rows into the specified temp table.
    /// </summary>
    /// <param name="tableName">The temp table name.</param>
    /// <param name="rows">The rows to insert.</param>
    public void InsertIntoTempTable(string tableName, IEnumerable<QueryRow> rows)
    {
        if (!_tempTables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException($"Invalid object name '{tableName}'.");

        table.Rows.AddRange(rows);
    }

    /// <summary>
    /// Gets all rows from the specified temp table.
    /// </summary>
    /// <param name="tableName">The temp table name.</param>
    /// <returns>The rows in the temp table.</returns>
    public IReadOnlyList<QueryRow> GetTempTableRows(string tableName)
    {
        if (!_tempTables.TryGetValue(tableName, out var table))
            throw new InvalidOperationException($"Invalid object name '{tableName}'.");

        return table.Rows;
    }

    /// <summary>
    /// Drops (removes) a temp table from the session.
    /// </summary>
    /// <param name="tableName">The temp table name.</param>
    public void DropTempTable(string tableName)
    {
        if (!_tempTables.Remove(tableName))
            throw new InvalidOperationException($"Cannot drop the table '{tableName}', because it does not exist.");
    }

    /// <summary>
    /// Returns true if the specified temp table exists in this session.
    /// </summary>
    /// <param name="tableName">The temp table name to check.</param>
    public bool TempTableExists(string tableName)
    {
        return _tempTables.ContainsKey(tableName);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Cursor operations
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Declares a cursor with the given name and associated query plan node.
    /// The cursor is not yet open; call <see cref="OpenCursorAsync"/> to execute the query.
    /// </summary>
    /// <param name="cursorName">The cursor name (case-insensitive).</param>
    /// <param name="queryNode">The plan node for the cursor's SELECT query.</param>
    public void DeclareCursor(string cursorName, IQueryPlanNode queryNode)
    {
        if (_cursors.ContainsKey(cursorName))
            throw new InvalidOperationException(
                $"A cursor with the name '{cursorName}' already exists.");

        var state = new CursorState { QueryNode = queryNode };
        _cursors[cursorName] = state;
    }

    /// <summary>
    /// Opens a declared cursor by executing its query and materializing the results.
    /// </summary>
    /// <param name="cursorName">The cursor name.</param>
    /// <param name="context">The query plan execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task OpenCursorAsync(
        string cursorName,
        QueryPlanContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_cursors.TryGetValue(cursorName, out var state))
            throw new InvalidOperationException(
                $"A cursor with the name '{cursorName}' does not exist. Use DECLARE CURSOR first.");

        if (state.IsOpen)
            throw new InvalidOperationException(
                $"The cursor '{cursorName}' is already open.");

        if (state.QueryNode == null)
            throw new InvalidOperationException(
                $"The cursor '{cursorName}' has no associated query.");

        state.Rows.Clear();
        await foreach (var row in state.QueryNode.ExecuteAsync(context, cancellationToken))
        {
            state.Rows.Add(row);
        }

        state.Position = -1;
        state.IsOpen = true;
        FetchStatus = -1;
    }

    /// <summary>
    /// Fetches the next row from an open cursor.
    /// Advances the cursor position and updates <see cref="FetchStatus"/>.
    /// Returns null if the cursor is past the end.
    /// </summary>
    /// <param name="cursorName">The cursor name.</param>
    /// <returns>The next row, or null if past end.</returns>
    public QueryRow? FetchNextFromCursor(string cursorName)
    {
        if (!_cursors.TryGetValue(cursorName, out var state))
            throw new InvalidOperationException(
                $"A cursor with the name '{cursorName}' does not exist.");

        if (!state.IsOpen)
            throw new InvalidOperationException(
                $"The cursor '{cursorName}' is not open.");

        state.Position++;
        if (state.Position < state.Rows.Count)
        {
            FetchStatus = 0; // Success
            return state.Rows[state.Position];
        }

        FetchStatus = -1; // Past end
        return null;
    }

    /// <summary>
    /// Closes an open cursor. The cursor can be reopened with OPEN.
    /// </summary>
    /// <param name="cursorName">The cursor name.</param>
    public void CloseCursor(string cursorName)
    {
        if (!_cursors.TryGetValue(cursorName, out var state))
            throw new InvalidOperationException(
                $"A cursor with the name '{cursorName}' does not exist.");

        if (!state.IsOpen)
            throw new InvalidOperationException(
                $"The cursor '{cursorName}' is not open.");

        state.IsOpen = false;
        state.Rows.Clear();
        state.Position = -1;
    }

    /// <summary>
    /// Deallocates a cursor, removing it from the session entirely.
    /// The cursor must be closed first.
    /// </summary>
    /// <param name="cursorName">The cursor name.</param>
    public void DeallocateCursor(string cursorName)
    {
        if (!_cursors.TryGetValue(cursorName, out var state))
            throw new InvalidOperationException(
                $"A cursor with the name '{cursorName}' does not exist.");

        if (state.IsOpen)
            throw new InvalidOperationException(
                $"The cursor '{cursorName}' is still open. Use CLOSE before DEALLOCATE.");

        _cursors.Remove(cursorName);
    }

    /// <summary>
    /// Returns true if a cursor with the given name exists (declared or open).
    /// </summary>
    /// <param name="cursorName">The cursor name to check.</param>
    public bool CursorExists(string cursorName)
    {
        return _cursors.ContainsKey(cursorName);
    }

    /// <summary>
    /// Returns true if the cursor exists and is currently open.
    /// </summary>
    /// <param name="cursorName">The cursor name to check.</param>
    public bool IsCursorOpen(string cursorName)
    {
        return _cursors.TryGetValue(cursorName, out var state) && state.IsOpen;
    }

    /// <summary>
    /// Represents a temp table stored in memory.
    /// </summary>
    private sealed class TempTable
    {
        public string Name { get; }
        public IReadOnlyList<string> Columns { get; }
        public List<QueryRow> Rows { get; } = new();

        public TempTable(string name, IReadOnlyList<string> columns)
        {
            Name = name;
            Columns = columns;
        }
    }
}
