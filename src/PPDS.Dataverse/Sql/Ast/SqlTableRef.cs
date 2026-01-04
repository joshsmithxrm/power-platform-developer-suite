using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Table reference in FROM clause.
/// </summary>
public sealed class SqlTableRef
{
    /// <summary>
    /// The table name.
    /// </summary>
    public string TableName { get; }

    /// <summary>
    /// The table alias, if specified.
    /// </summary>
    public string? Alias { get; }

    /// <summary>
    /// Optional trailing comment.
    /// </summary>
    public string? TrailingComment { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlTableRef"/> class.
    /// </summary>
    public SqlTableRef(string tableName, string? alias = null)
    {
        TableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
        Alias = alias;
    }

    /// <summary>
    /// Gets the effective name (alias if present, otherwise table name).
    /// </summary>
    public string GetEffectiveName() => Alias ?? TableName;
}
