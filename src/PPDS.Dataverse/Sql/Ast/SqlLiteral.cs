namespace PPDS.Dataverse.Sql.Ast;

/// <summary>
/// Literal value in SQL expression.
/// </summary>
public sealed class SqlLiteral
{
    /// <summary>
    /// The literal value. Can be string, number (as string), or null.
    /// </summary>
    public string? Value { get; }

    /// <summary>
    /// The type of the literal.
    /// </summary>
    public SqlLiteralType Type { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLiteral"/> class.
    /// </summary>
    public SqlLiteral(string? value, SqlLiteralType type)
    {
        Value = value;
        Type = type;
    }

    /// <summary>
    /// Creates a string literal.
    /// </summary>
    public static SqlLiteral String(string value) => new(value, SqlLiteralType.String);

    /// <summary>
    /// Creates a number literal.
    /// </summary>
    public static SqlLiteral Number(string value) => new(value, SqlLiteralType.Number);

    /// <summary>
    /// Creates a null literal.
    /// </summary>
    public static SqlLiteral Null() => new(null, SqlLiteralType.Null);
}

/// <summary>
/// Type of a SQL literal value.
/// </summary>
public enum SqlLiteralType
{
    /// <summary>String literal value.</summary>
    String,
    /// <summary>Numeric literal value.</summary>
    Number,
    /// <summary>NULL literal value.</summary>
    Null
}
