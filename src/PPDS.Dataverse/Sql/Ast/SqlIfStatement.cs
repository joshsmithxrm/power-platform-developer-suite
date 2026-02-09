using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// IF condition BEGIN...END [ELSE BEGIN...END]
/// Supports conditional execution of statement blocks.
/// </summary>
public sealed class SqlIfStatement : ISqlStatement
{
    /// <summary>The condition to evaluate.</summary>
    public ISqlCondition Condition { get; }

    /// <summary>The statement block to execute when the condition is true.</summary>
    public SqlBlockStatement ThenBlock { get; }

    /// <summary>The optional statement block to execute when the condition is false.</summary>
    public SqlBlockStatement? ElseBlock { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlIfStatement"/> class.</summary>
    public SqlIfStatement(ISqlCondition condition, SqlBlockStatement thenBlock, SqlBlockStatement? elseBlock, int sourcePosition)
    {
        Condition = condition ?? throw new ArgumentNullException(nameof(condition));
        ThenBlock = thenBlock ?? throw new ArgumentNullException(nameof(thenBlock));
        ElseBlock = elseBlock;
        SourcePosition = sourcePosition;
    }
}
