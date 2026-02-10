using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// WHILE condition BEGIN...END
/// Supports iterative execution of a statement block while a condition is true.
/// </summary>
public sealed class SqlWhileStatement : ISqlStatement
{
    /// <summary>The condition to evaluate before each iteration.</summary>
    public ISqlCondition Condition { get; }

    /// <summary>The statement block to execute while the condition is true.</summary>
    public SqlBlockStatement Body { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlWhileStatement"/> class.</summary>
    public SqlWhileStatement(ISqlCondition condition, SqlBlockStatement body, int sourcePosition)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        SourcePosition = sourcePosition;
    }
}
