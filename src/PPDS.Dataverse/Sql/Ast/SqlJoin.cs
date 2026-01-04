using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// JOIN clause in a SQL SELECT statement.
/// </summary>
public sealed class SqlJoin
{
    /// <summary>
    /// The type of join (INNER, LEFT, RIGHT).
    /// </summary>
    public SqlJoinType Type { get; }

    /// <summary>
    /// The table being joined.
    /// </summary>
    public SqlTableRef Table { get; }

    /// <summary>
    /// The left column in the ON clause (from the main table or previous join).
    /// </summary>
    public SqlColumnRef LeftColumn { get; }

    /// <summary>
    /// The right column in the ON clause (from the joined table).
    /// </summary>
    public SqlColumnRef RightColumn { get; }

    /// <summary>
    /// Optional trailing comment.
    /// </summary>
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlJoin"/> class.
    /// </summary>
    public SqlJoin(SqlJoinType type, SqlTableRef table, SqlColumnRef leftColumn, SqlColumnRef rightColumn)
    {
        Type = type;
        Table = table ?? throw new ArgumentNullException(nameof(table));
        LeftColumn = leftColumn ?? throw new ArgumentNullException(nameof(leftColumn));
        RightColumn = rightColumn ?? throw new ArgumentNullException(nameof(rightColumn));
    }
}
