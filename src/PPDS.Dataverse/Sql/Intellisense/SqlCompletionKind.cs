namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// The kind of completion item in SQL IntelliSense results.
/// </summary>
public enum SqlCompletionKind
{
    /// <summary>SQL keyword (SELECT, WHERE, JOIN, etc.).</summary>
    Keyword,

    /// <summary>Entity/table name from Dataverse metadata.</summary>
    Entity,

    /// <summary>Attribute/column name from Dataverse metadata.</summary>
    Attribute
}
