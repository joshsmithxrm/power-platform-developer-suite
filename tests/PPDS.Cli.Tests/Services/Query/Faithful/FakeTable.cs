using Microsoft.Xrm.Sdk;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>The Dataverse type of a column of a <see cref="FakeTable"/>; decides how a FetchXML value is read.</summary>
internal enum FakeColumnType
{
    String,
    Integer,
    BigInt,
    Decimal,
    Double,
    Money,
    Boolean,
    /// <summary>A user-local date and time, stored in UTC.</summary>
    DateTime,
    /// <summary>A date-only column: no time and no time-zone conversion.</summary>
    DateOnly,
    Uniqueidentifier,
    Choice,
    Lookup,
    /// <summary>A multi-select choice; only <c>null</c> and <c>not-null</c> conditions are observed (audit W39).</summary>
    MultiChoice,
    /// <summary>The virtual <c>&lt;column&gt;name</c> of a choice or two-option column (audit V04, V08, V09, J19).</summary>
    VirtualName
}

/// <summary>
/// One in-memory Dataverse table for <see cref="FakeDataverse"/>: its columns with their types, the labels of its choice
/// columns, and its rows. Row values use the SDK types Dataverse returns (string, int, decimal, <see cref="Money"/>,
/// <see cref="OptionSetValue"/>, <see cref="EntityReference"/>, UTC <see cref="DateTime"/>, ...); a missing or null value is
/// NULL.
/// </summary>
internal sealed class FakeTable
{
    private readonly Dictionary<string, FakeColumnType> _columns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, string>> _labels = new(StringComparer.Ordinal);
    private readonly List<Dictionary<string, object?>> _rows = new();

    public FakeTable(string logicalName)
    {
        LogicalName = logicalName;
        PrimaryIdAttribute = logicalName + "id";
        _columns[PrimaryIdAttribute] = FakeColumnType.Uniqueidentifier;
    }

    public string LogicalName { get; }

    public string PrimaryIdAttribute { get; }

    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows => _rows;

    public IReadOnlyDictionary<string, FakeColumnType> Columns => _columns;

    public FakeTable Column(string name, FakeColumnType type)
    {
        _columns[name] = type;
        return this;
    }

    /// <summary>A choice column and its labels (value, label).</summary>
    public FakeTable Choice(string name, params (int Value, string Label)[] options)
    {
        _columns[name] = FakeColumnType.Choice;
        _labels[name] = options.ToDictionary(o => o.Value, o => o.Label);
        return this;
    }

    /// <summary>Adds a row. Every key must be a declared column (names are case-sensitive); the primary id is generated when absent.</summary>
    public FakeTable Row(params (string Column, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, value) in values)
        {
            if (!_columns.ContainsKey(column))
                throw new ArgumentException($"Fake table {LogicalName} has no column {column}.");
            // Not observed: how Dataverse stores a string of spaces only. Observed (audit V11, V12): a lookup carries the
            // name of the record it points to.
            if (value is string { Length: > 0 } text && text.Trim(' ').Length == 0)
                throw new NotSupportedException($"Fake table {LogicalName}: not observed: storing the spaces-only string in {column}.");
            if (value is EntityReference { Name: null })
                throw new NotSupportedException($"Fake table {LogicalName}: not observed: a lookup in {column} without the name of its record.");
            row[column] = value;
        }

        if (!row.ContainsKey(PrimaryIdAttribute) || row[PrimaryIdAttribute] is null)
            row[PrimaryIdAttribute] = DeterministicId(LogicalName, _rows.Count + 1);
        _rows.Add(row);
        return this;
    }

    public bool HasColumn(string name) => _columns.ContainsKey(name);

    public FakeColumnType TypeOf(string name) => _columns[name];

    public string? Label(string column, int value)
        => _labels.TryGetValue(column, out var options) && options.TryGetValue(value, out var label) ? label : null;

    /// <summary>A stable id for row <paramref name="number"/> (1-based) of <paramref name="table"/>.</summary>
    public static Guid DeterministicId(string table, int number)
    {
        var prefix = table.Length switch { >= 8 => table[..8], _ => table.PadRight(8, '0') };
        var hex = string.Concat(prefix.Select(c => ((int)c % 16).ToString("x", System.Globalization.CultureInfo.InvariantCulture)));
        return Guid.Parse($"{hex}-0000-0000-0000-{number:D12}");
    }
}
