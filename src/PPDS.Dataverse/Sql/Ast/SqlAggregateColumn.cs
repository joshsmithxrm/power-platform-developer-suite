namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Aggregate column expression in SELECT clause.
/// Examples: COUNT(*), COUNT(name), COUNT(DISTINCT name), SUM(revenue)
/// </summary>
public sealed class SqlAggregateColumn : ISqlSelectColumn
{
    /// <summary>
    /// The aggregate function.
    /// </summary>
    public SqlAggregateFunction Function { get; }

    /// <summary>
    /// The column being aggregated, or null for COUNT(*).
    /// </summary>
    public SqlColumnRef? Column { get; }

    /// <summary>
    /// Whether DISTINCT is applied (e.g., COUNT(DISTINCT name)).
    /// </summary>
    public bool IsDistinct { get; }

    /// <summary>
    /// The column alias.
    /// </summary>
    public string? Alias { get; }

    /// <summary>
    /// Optional trailing comment.
    /// </summary>
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlAggregateColumn"/> class.
    /// </summary>
    public SqlAggregateColumn(SqlAggregateFunction function, SqlColumnRef? column, bool isDistinct, string? alias)
    {
        Function = function;
        Column = column;
        IsDistinct = isDistinct;
        Alias = alias;
    }

    /// <summary>
    /// Checks if this is COUNT(*) - counts all rows.
    /// </summary>
    public bool IsCountAll => Function == SqlAggregateFunction.Count && Column == null;

    /// <summary>
    /// Gets the column name for FetchXML transpilation.
    /// For COUNT(*), returns null. For others, returns the column name.
    /// </summary>
    public string? GetColumnName() => Column?.ColumnName;
}

/// <summary>
/// Marker interface for columns in a SELECT clause (either regular or aggregate).
/// </summary>
public interface ISqlSelectColumn
{
    /// <summary>
    /// The column alias, if specified.
    /// </summary>
    string? Alias { get; }

    /// <summary>
    /// Optional trailing comment.
    /// </summary>
    string? TrailingComment { get; set; }
}
