namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Base interface for all SQL statement types.
/// </summary>
public interface ISqlStatement
{
    /// <summary>Position of the first token for error reporting.</summary>
    int SourcePosition { get; }
}
