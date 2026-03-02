using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;

namespace PPDS.Query.Provider;

/// <summary>
/// ADO.NET data reader that streams rows from the PPDS query engine.
/// Wraps a list of <see cref="QueryRow"/> results and exposes them through
/// the standard <see cref="DbDataReader"/> interface.
/// </summary>
public sealed class PpdsDataReader : DbDataReader
{
    private readonly List<QueryRow> _rows;
    private readonly List<string> _columnNames;
    private readonly Dictionary<string, int> _columnOrdinals;
    private int _currentRowIndex = -1;
    private bool _isClosed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PpdsDataReader"/> class
    /// from pre-loaded query rows.
    /// </summary>
    /// <param name="rows">The query result rows.</param>
    /// <param name="columns">Column metadata from the query result.</param>
    internal PpdsDataReader(
        IReadOnlyList<QueryRow> rows,
        IReadOnlyList<QueryColumn> columns)
    {
        _rows = rows?.ToList() ?? throw new ArgumentNullException(nameof(rows));

        // Build column names from metadata; if none, infer from first row
        if (columns != null && columns.Count > 0)
        {
            _columnNames = columns.Select(c => c.EffectiveName).ToList();
        }
        else if (_rows.Count > 0)
        {
            _columnNames = _rows[0].Values.Keys.ToList();
        }
        else
        {
            _columnNames = new List<string>();
        }

        _columnOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _columnNames.Count; i++)
        {
            // First occurrence wins (in case of duplicates)
            _columnOrdinals.TryAdd(_columnNames[i], i);
        }
    }

    /// <summary>
    /// Creates a <see cref="PpdsDataReader"/> from a <see cref="QueryResult"/>.
    /// </summary>
    internal static PpdsDataReader FromQueryResult(QueryResult result)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));

        var rows = result.Records
            .Select(r => new QueryRow(r, result.EntityLogicalName))
            .ToList();

        return new PpdsDataReader(rows, result.Columns);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DbDataReader property overrides
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override int Depth => 0;

    /// <inheritdoc />
    public override int FieldCount => _columnNames.Count;

    /// <inheritdoc />
    public override bool HasRows => _rows.Count > 0;

    /// <inheritdoc />
    public override bool IsClosed => _isClosed;

    /// <inheritdoc />
    public override int RecordsAffected => -1;

    /// <inheritdoc />
    public override object this[int ordinal] => GetValue(ordinal);

    /// <inheritdoc />
    public override object this[string name] => GetValue(GetOrdinal(name));

    // ═══════════════════════════════════════════════════════════════════
    //  Navigation
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override bool Read()
    {
        ThrowIfClosed();
        _currentRowIndex++;
        return _currentRowIndex < _rows.Count;
    }

    /// <inheritdoc />
    public override bool NextResult()
    {
        // Single result set only
        return false;
    }

    /// <inheritdoc />
    public override void Close()
    {
        _isClosed = true;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Column metadata
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override string GetName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return _columnNames[ordinal];
    }

    /// <inheritdoc />
    public override int GetOrdinal(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));

        if (_columnOrdinals.TryGetValue(name, out var ordinal))
            return ordinal;

        throw new IndexOutOfRangeException($"Column '{name}' not found.");
    }

    /// <inheritdoc />
    public override string GetDataTypeName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        var value = GetRawValueSafe(ordinal);
        if (value is null) return "nvarchar";

        return value switch
        {
            int => "int",
            long => "bigint",
            decimal => "decimal",
            double => "float",
            float => "real",
            bool => "bit",
            DateTime => "datetime",
            DateTimeOffset => "datetimeoffset",
            Guid => "uniqueidentifier",
            byte[] => "varbinary",
            _ => "nvarchar"
        };
    }

    /// <inheritdoc />
    public override Type GetFieldType(int ordinal)
    {
        ValidateOrdinal(ordinal);
        var value = GetRawValueSafe(ordinal);
        return value?.GetType() ?? typeof(string);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Value accessors
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    public override object GetValue(int ordinal)
    {
        ThrowIfClosed();
        ThrowIfNoCurrentRow();
        ValidateOrdinal(ordinal);

        var value = GetRawValue(ordinal);
        return value ?? DBNull.Value;
    }

    /// <inheritdoc />
    public override int GetValues(object[] values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
        ThrowIfClosed();
        ThrowIfNoCurrentRow();

        var count = Math.Min(values.Length, _columnNames.Count);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }
        return count;
    }

    /// <inheritdoc />
    public override bool IsDBNull(int ordinal)
    {
        ThrowIfClosed();
        ThrowIfNoCurrentRow();
        ValidateOrdinal(ordinal);

        return GetRawValue(ordinal) is null;
    }

    /// <inheritdoc />
    public override string GetString(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get string from null value.");

        if (value is string s) return s;
        return Convert.ToString(value, CultureInfo.InvariantCulture)
            ?? throw new InvalidCastException($"Cannot convert {value.GetType().Name} to String.");
    }

    /// <inheritdoc />
    public override int GetInt32(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Int32 from null value.");

        if (value is int i) return i;
        return Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override long GetInt64(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Int64 from null value.");

        if (value is long l) return l;
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override Guid GetGuid(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Guid from null value.");

        if (value is Guid g) return g;
        if (value is string s) return Guid.Parse(s);
        throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Guid.");
    }

    /// <inheritdoc />
    public override DateTime GetDateTime(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get DateTime from null value.");

        if (value is DateTime dt) return dt;
        if (value is DateTimeOffset dto) return dto.DateTime;
        return Convert.ToDateTime(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override bool GetBoolean(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Boolean from null value.");

        if (value is bool b) return b;
        return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override decimal GetDecimal(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Decimal from null value.");

        if (value is decimal d) return d;
        return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override double GetDouble(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Double from null value.");

        if (value is double d) return d;
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override float GetFloat(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Float from null value.");

        if (value is float f) return f;
        return Convert.ToSingle(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override short GetInt16(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Int16 from null value.");

        if (value is short s) return s;
        return Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override byte GetByte(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Byte from null value.");

        if (value is byte b) return b;
        return Convert.ToByte(value, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        throw new NotSupportedException("GetBytes is not supported by the PPDS data reader.");
    }

    /// <inheritdoc />
    public override char GetChar(int ordinal)
    {
        var value = GetRawValue(ordinal);
        if (value is null)
            throw new InvalidCastException("Cannot get Char from null value.");

        if (value is char c) return c;
        var str = Convert.ToString(value, CultureInfo.InvariantCulture);
        if (str != null && str.Length > 0) return str[0];
        throw new InvalidCastException($"Cannot convert {value.GetType().Name} to Char.");
    }

    /// <inheritdoc />
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        throw new NotSupportedException("GetChars is not supported by the PPDS data reader.");
    }

    /// <inheritdoc />
    public override IEnumerator GetEnumerator()
    {
        return new DbEnumerator(this, closeReader: false);
    }

    /// <inheritdoc />
    public override DataTable GetSchemaTable()
    {
        var table = new DataTable("SchemaTable");
        table.Columns.Add("ColumnName", typeof(string));
        table.Columns.Add("ColumnOrdinal", typeof(int));
        table.Columns.Add("DataType", typeof(Type));
        table.Columns.Add("ColumnSize", typeof(int));
        table.Columns.Add("AllowDBNull", typeof(bool));

        for (var i = 0; i < _columnNames.Count; i++)
        {
            var row = table.NewRow();
            row["ColumnName"] = _columnNames[i];
            row["ColumnOrdinal"] = i;
            row["DataType"] = GetFieldType(i);
            row["ColumnSize"] = -1;
            row["AllowDBNull"] = true;
            table.Rows.Add(row);
        }

        return table;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════════════

    private object? GetRawValue(int ordinal)
    {
        var columnName = _columnNames[ordinal];
        var currentRow = _rows[_currentRowIndex];

        if (currentRow.Values.TryGetValue(columnName, out var queryValue))
            return queryValue.Value;

        return null;
    }

    /// <summary>
    /// Gets the raw value for a column, falling back to the first row
    /// if no current row is positioned (for metadata queries like
    /// GetFieldType/GetDataTypeName that can be called before Read()).
    /// </summary>
    private object? GetRawValueSafe(int ordinal)
    {
        // Use current row if positioned, otherwise peek at first row
        var rowIndex = _currentRowIndex >= 0 && _currentRowIndex < _rows.Count
            ? _currentRowIndex
            : (_rows.Count > 0 ? 0 : -1);

        if (rowIndex < 0)
            return null;

        var columnName = _columnNames[ordinal];
        var row = _rows[rowIndex];

        if (row.Values.TryGetValue(columnName, out var queryValue))
            return queryValue.Value;

        return null;
    }

    private void ValidateOrdinal(int ordinal)
    {
        if (ordinal < 0 || ordinal >= _columnNames.Count)
            throw new IndexOutOfRangeException(
                $"Column ordinal {ordinal} is out of range. The reader has {_columnNames.Count} columns.");
    }

    private void ThrowIfClosed()
    {
        if (_isClosed)
            throw new InvalidOperationException("The data reader is closed.");
    }

    private void ThrowIfNoCurrentRow()
    {
        if (_currentRowIndex < 0 || _currentRowIndex >= _rows.Count)
            throw new InvalidOperationException(
                "No data available. Call Read() first and ensure it returns true.");
    }
}
