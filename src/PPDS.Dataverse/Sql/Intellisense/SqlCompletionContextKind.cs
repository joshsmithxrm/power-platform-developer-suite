namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// The kind of completion context at a cursor position in SQL text.
/// </summary>
public enum SqlCompletionContextKind
{
    /// <summary>No completions available at this position.</summary>
    None,

    /// <summary>SQL keyword completions (SELECT, FROM, WHERE, etc.).</summary>
    Keyword,

    /// <summary>Entity/table name completions.</summary>
    Entity,

    /// <summary>Attribute/column name completions.</summary>
    Attribute
}
