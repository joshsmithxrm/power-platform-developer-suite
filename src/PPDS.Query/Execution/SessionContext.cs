using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Execution;

/// <summary>
/// Holds session-scoped state for a query execution session, including temp table data.
/// Temp tables (names starting with #) are stored in memory and persist across
/// statements within the same session.
/// </summary>
public sealed class SessionContext
{
    private readonly Dictionary<string, TempTable> _tempTables = new(StringComparer.OrdinalIgnoreCase);

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
