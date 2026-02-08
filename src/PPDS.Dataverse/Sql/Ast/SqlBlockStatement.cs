using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// BEGIN ... END block containing a sequence of statements.
/// </summary>
public sealed class SqlBlockStatement : ISqlStatement
{
    /// <summary>The ordered list of statements in the block.</summary>
    public IReadOnlyList<ISqlStatement> Statements { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    public SqlBlockStatement(IReadOnlyList<ISqlStatement> statements, int sourcePosition)
    {
        Statements = statements ?? throw new ArgumentNullException(nameof(statements));
        SourcePosition = sourcePosition;
    }
}
