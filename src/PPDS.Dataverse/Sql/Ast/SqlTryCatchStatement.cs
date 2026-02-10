using System;

namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH
/// Supports structured error handling in scripts.
/// </summary>
public sealed class SqlTryCatchStatement : ISqlStatement
{
    /// <summary>The statement block to execute in the TRY section.</summary>
    public SqlBlockStatement TryBlock { get; }

    /// <summary>The statement block to execute in the CATCH section if an error occurs.</summary>
    public SqlBlockStatement CatchBlock { get; }

    /// <inheritdoc />
    public int SourcePosition { get; }

    /// <summary>Initializes a new instance of the <see cref="SqlTryCatchStatement"/> class.</summary>
    public SqlTryCatchStatement(SqlBlockStatement tryBlock, SqlBlockStatement catchBlock, int sourcePosition)
    {
        TryBlock = tryBlock ?? throw new ArgumentNullException(nameof(tryBlock));
        CatchBlock = catchBlock ?? throw new ArgumentNullException(nameof(catchBlock));
        SourcePosition = sourcePosition;
    }
}
