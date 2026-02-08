using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Represents an INSERT statement: INSERT INTO entity (cols) VALUES (...) or INSERT INTO entity (cols) SELECT ...
/// </summary>
public sealed class SqlInsertStatement : ISqlStatement
{
    /// <summary>The target entity (table) name.</summary>
    public string TargetEntity { get; }

    /// <summary>The columns being inserted into.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The value rows for INSERT ... VALUES syntax. Null when using INSERT ... SELECT.</summary>
    public IReadOnlyList<IReadOnlyList<ISqlExpression>>? ValueRows { get; }

    /// <summary>The source query for INSERT ... SELECT syntax. Null when using INSERT ... VALUES.</summary>
    public SqlSelectStatement? SourceQuery { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlInsertStatement"/> class.
    /// </summary>
    public SqlInsertStatement(
        string targetEntity,
        IReadOnlyList<string> columns,
        IReadOnlyList<IReadOnlyList<ISqlExpression>>? valueRows,
        SqlSelectStatement? sourceQuery,
        int sourcePosition)
    {
        TargetEntity = targetEntity;
        Columns = columns;
        ValueRows = valueRows;
        SourceQuery = sourceQuery;
        SourcePosition = sourcePosition;
    }
}
