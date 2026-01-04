using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// ORDER BY item in a SQL SELECT statement.
/// </summary>
public sealed class SqlOrderByItem
{
    /// <summary>
    /// The column to order by.
    /// </summary>
    public SqlColumnRef Column { get; }

    /// <summary>
    /// The sort direction (ASC or DESC).
    /// </summary>
    public SqlSortDirection Direction { get; }

    /// <summary>
    /// Optional trailing comment.
    /// </summary>
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlOrderByItem"/> class.
    /// </summary>
    public SqlOrderByItem(SqlColumnRef column, SqlSortDirection direction = SqlSortDirection.Ascending)
    {
        Column = column ?? throw new ArgumentNullException(nameof(column));
        Direction = direction;
    }
}
