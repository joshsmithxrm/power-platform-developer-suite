namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Language-agnostic source token classification for syntax highlighting.
/// </summary>
public enum SourceTokenType
{
    /// <summary>Language keyword (SELECT, FROM, WHERE, etc.).</summary>
    Keyword,

    /// <summary>Built-in function (COUNT, SUM, ROW_NUMBER, etc.).</summary>
    Function,

    /// <summary>String literal ('hello').</summary>
    StringLiteral,

    /// <summary>Numeric literal (42, 3.14).</summary>
    NumericLiteral,

    /// <summary>Comment (-- line or /* block */).</summary>
    Comment,

    /// <summary>Operator (=, &lt;&gt;, +, -, etc.).</summary>
    Operator,

    /// <summary>Identifier (column name, table name, alias).</summary>
    Identifier,

    /// <summary>Punctuation (, . * ( ) ;).</summary>
    Punctuation,

    /// <summary>Variable reference (@name).</summary>
    Variable,

    /// <summary>Error/unrecognized token.</summary>
    Error
}
