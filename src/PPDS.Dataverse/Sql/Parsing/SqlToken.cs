using System.Linq;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Represents a single token from SQL lexical analysis.
/// Immutable with position information for error reporting.
/// </summary>
/// <param name="Type">The type of the token.</param>
/// <param name="Value">The token's string value.</param>
/// <param name="Position">The character position in the source where this token starts.</param>
public readonly record struct SqlToken(SqlTokenType Type, string Value, int Position)
{
    /// <summary>
    /// Checks if this token is a specific type.
    /// </summary>
    public bool Is(SqlTokenType type) => Type == type;

    /// <summary>
    /// Checks if this token is one of the specified types.
    /// </summary>
    public bool IsOneOf(params SqlTokenType[] types) => types.Contains(Type);

    /// <summary>
    /// Checks if this token is a keyword.
    /// </summary>
    public bool IsKeyword() => Type.IsKeyword();

    /// <summary>
    /// Checks if this token is a comparison operator.
    /// </summary>
    public bool IsComparisonOperator() => Type.IsComparisonOperator();

    /// <summary>
    /// Checks if this token is an aggregate function.
    /// </summary>
    public bool IsAggregateFunction() => Type.IsAggregateFunction();

    /// <inheritdoc />
    public override string ToString() => $"{Type}({Value})@{Position}";
}

/// <summary>
/// Represents a comment extracted during lexical analysis.
/// Tracks position for associating with nearby tokens/AST nodes.
/// </summary>
/// <param name="Text">The comment text (without delimiters).</param>
/// <param name="Position">Character position where the comment starts in the source.</param>
/// <param name="IsBlock">Whether this is a block comment vs line comment.</param>
public readonly record struct SqlComment(string Text, int Position, bool IsBlock);
