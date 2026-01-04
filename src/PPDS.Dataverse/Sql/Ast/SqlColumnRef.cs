namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Column reference in SELECT clause.
/// Can be: column, table.column, *, or table.*
/// </summary>
public sealed class SqlColumnRef : ISqlSelectColumn
{
    /// <summary>
    /// The table name or alias, if specified.
    /// </summary>
    public string? TableName { get; }

    /// <summary>
    /// The column name, or "*" for wildcards.
    /// </summary>
    public string ColumnName { get; }

    /// <summary>
    /// The column alias, if specified.
    /// </summary>
    public string? Alias { get; }

    /// <summary>
    /// Whether this is a wildcard (*) reference.
    /// </summary>
    public bool IsWildcard { get; }

    /// <summary>
    /// Optional trailing comment (e.g., "-- account name" after "name,")
    /// </summary>
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlColumnRef"/> class.
    /// </summary>
    public SqlColumnRef(string? tableName, string columnName, string? alias, bool isWildcard)
    {
        TableName = tableName;
        ColumnName = columnName;
        Alias = alias;
        IsWildcard = isWildcard;
    }

    /// <summary>
    /// Gets the full qualified name (table.column or just column).
    /// </summary>
    public string GetFullName()
    {
        return TableName != null
            ? $"{TableName}.{ColumnName}"
            : ColumnName;
    }

    /// <summary>
    /// Creates a simple column reference without table qualifier.
    /// </summary>
    public static SqlColumnRef Simple(string columnName, string? alias = null) =>
        new(null, columnName, alias, false);

    /// <summary>
    /// Creates a qualified column reference with table.
    /// </summary>
    public static SqlColumnRef Qualified(string tableName, string columnName, string? alias = null) =>
        new(tableName, columnName, alias, false);

    /// <summary>
    /// Creates a wildcard (*) reference.
    /// </summary>
    public static SqlColumnRef Wildcard(string? tableName = null) =>
        new(tableName, "*", null, true);
}
