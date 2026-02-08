using System;
using System.Collections.Generic;

namespace PPDS.Dataverse.Sql.Parsing;

/// <summary>
/// Result of SQL lexical analysis.
/// Contains both tokens and comments for full source reconstruction.
/// </summary>
/// <param name="Tokens">The list of tokens.</param>
/// <param name="Comments">The list of comments extracted during lexing.</param>
public readonly record struct SqlLexerResult(
    IReadOnlyList<SqlToken> Tokens,
    IReadOnlyList<SqlComment> Comments);

/// <summary>
/// SQL Lexer - tokenizes SQL strings for parsing.
/// Handles keywords, identifiers, strings, numbers, and operators.
/// Captures comments with positions for preservation during transpilation.
/// </summary>
/// <remarks>
/// Business Rules:
/// - Keywords are case-insensitive
/// - Identifiers can be quoted with square brackets [name] or double quotes "name"
/// - String literals use single quotes
/// - Line comments (--) and block comments (/* */) are captured with positions
/// </remarks>
public sealed class SqlLexer
{
    private readonly string _sql;
    private readonly List<SqlComment> _comments = new();
    private int _position;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlLexer"/> class.
    /// </summary>
    /// <param name="sql">The SQL string to tokenize.</param>
    public SqlLexer(string sql)
    {
        _sql = sql ?? throw new ArgumentNullException(nameof(sql));
    }

    /// <summary>
    /// Tokenizes the entire SQL string.
    /// Returns both tokens and comments for full source preservation.
    /// </summary>
    public SqlLexerResult Tokenize()
    {
        var tokens = new List<SqlToken>();

        while (!IsAtEnd())
        {
            SkipWhitespaceAndCaptureComments();
            if (IsAtEnd()) break;

            var token = NextToken();
            tokens.Add(token);
        }

        tokens.Add(new SqlToken(SqlTokenType.Eof, "", _position));
        return new SqlLexerResult(tokens, _comments);
    }

    /// <summary>
    /// Gets the next token from the input.
    /// </summary>
    private SqlToken NextToken()
    {
        var startPosition = _position;
        var ch = Peek();

        // Single-character tokens
        switch (ch)
        {
            case ',':
                Advance();
                return new SqlToken(SqlTokenType.Comma, ",", startPosition);
            case '.':
                Advance();
                return new SqlToken(SqlTokenType.Dot, ".", startPosition);
            case '*':
                Advance();
                return new SqlToken(SqlTokenType.Star, "*", startPosition);
            case '(':
                Advance();
                return new SqlToken(SqlTokenType.LeftParen, "(", startPosition);
            case ')':
                Advance();
                return new SqlToken(SqlTokenType.RightParen, ")", startPosition);
            case '+':
                Advance();
                return new SqlToken(SqlTokenType.Plus, "+", startPosition);
            case '/':
                Advance();
                return new SqlToken(SqlTokenType.Slash, "/", startPosition);
            case '%':
                Advance();
                return new SqlToken(SqlTokenType.Percent, "%", startPosition);
        }

        // Operators
        if (ch == '=')
        {
            Advance();
            return new SqlToken(SqlTokenType.Equals, "=", startPosition);
        }

        if (ch == '<')
        {
            Advance();
            if (Peek() == '=')
            {
                Advance();
                return new SqlToken(SqlTokenType.LessThanOrEqual, "<=", startPosition);
            }
            if (Peek() == '>')
            {
                Advance();
                return new SqlToken(SqlTokenType.NotEquals, "<>", startPosition);
            }
            return new SqlToken(SqlTokenType.LessThan, "<", startPosition);
        }

        if (ch == '>')
        {
            Advance();
            if (Peek() == '=')
            {
                Advance();
                return new SqlToken(SqlTokenType.GreaterThanOrEqual, ">=", startPosition);
            }
            return new SqlToken(SqlTokenType.GreaterThan, ">", startPosition);
        }

        if (ch == '!' && PeekNext() == '=')
        {
            Advance();
            Advance();
            return new SqlToken(SqlTokenType.NotEquals, "!=", startPosition);
        }

        // Minus: emit Minus token always; parser handles unary negation vs subtraction
        if (ch == '-')
        {
            Advance();
            return new SqlToken(SqlTokenType.Minus, "-", startPosition);
        }

        // String literals
        if (ch == '\'')
        {
            return ReadString();
        }

        // Quoted identifiers
        if (ch == '[')
        {
            return ReadBracketedIdentifier();
        }

        if (ch == '"')
        {
            return ReadQuotedIdentifier();
        }

        // Numbers
        if (IsDigit(ch))
        {
            return ReadNumber();
        }

        // Identifiers and keywords
        if (IsIdentifierStart(ch))
        {
            return ReadIdentifierOrKeyword();
        }

        throw SqlParseException.AtPosition($"Unexpected character: '{ch}'", startPosition, _sql);
    }

    /// <summary>
    /// Reads a string literal enclosed in single quotes.
    /// </summary>
    private SqlToken ReadString()
    {
        var startPosition = _position;
        Advance(); // consume opening quote

        var value = new System.Text.StringBuilder();
        while (!IsAtEnd())
        {
            var ch = Peek();
            if (ch == '\'')
            {
                // Check for escaped quote ('')
                if (PeekNext() == '\'')
                {
                    value.Append('\'');
                    Advance();
                    Advance();
                }
                else
                {
                    Advance(); // consume closing quote
                    return new SqlToken(SqlTokenType.String, value.ToString(), startPosition);
                }
            }
            else
            {
                value.Append(ch);
                Advance();
            }
        }

        throw SqlParseException.AtPosition("Unterminated string literal", startPosition, _sql);
    }

    /// <summary>
    /// Reads a bracketed identifier [name].
    /// </summary>
    private SqlToken ReadBracketedIdentifier()
    {
        var startPosition = _position;
        Advance(); // consume [

        var value = new System.Text.StringBuilder();
        while (!IsAtEnd() && Peek() != ']')
        {
            value.Append(Peek());
            Advance();
        }

        if (IsAtEnd())
        {
            throw SqlParseException.AtPosition("Unterminated bracketed identifier", startPosition, _sql);
        }

        Advance(); // consume ]
        return new SqlToken(SqlTokenType.Identifier, value.ToString(), startPosition);
    }

    /// <summary>
    /// Reads a double-quoted identifier "name".
    /// </summary>
    private SqlToken ReadQuotedIdentifier()
    {
        var startPosition = _position;
        Advance(); // consume "

        var value = new System.Text.StringBuilder();
        while (!IsAtEnd() && Peek() != '"')
        {
            value.Append(Peek());
            Advance();
        }

        if (IsAtEnd())
        {
            throw SqlParseException.AtPosition("Unterminated quoted identifier", startPosition, _sql);
        }

        Advance(); // consume "
        return new SqlToken(SqlTokenType.Identifier, value.ToString(), startPosition);
    }

    /// <summary>
    /// Reads a number (integer or decimal). Negative sign is handled by the parser as unary negation.
    /// </summary>
    private SqlToken ReadNumber()
    {
        var startPosition = _position;
        var value = new System.Text.StringBuilder();

        // Integer part
        while (!IsAtEnd() && IsDigit(Peek()))
        {
            value.Append(Peek());
            Advance();
        }

        // Decimal part
        if (Peek() == '.' && IsDigit(PeekNext()))
        {
            value.Append('.');
            Advance();
            while (!IsAtEnd() && IsDigit(Peek()))
            {
                value.Append(Peek());
                Advance();
            }
        }

        return new SqlToken(SqlTokenType.Number, value.ToString(), startPosition);
    }

    /// <summary>
    /// Reads an identifier or keyword.
    /// Preserves original casing in token value (important for aliases).
    /// </summary>
    private SqlToken ReadIdentifierOrKeyword()
    {
        var startPosition = _position;
        var value = new System.Text.StringBuilder();

        while (!IsAtEnd() && IsIdentifierChar(Peek()))
        {
            value.Append(Peek());
            Advance();
        }

        var valueStr = value.ToString();

        // Check if it's a keyword (case-insensitive)
        if (SqlTokenTypeExtensions.KeywordMap.TryGetValue(valueStr, out var keywordType))
        {
            // Preserve original casing in value - important when keyword is used as alias
            return new SqlToken(keywordType, valueStr, startPosition);
        }

        return new SqlToken(SqlTokenType.Identifier, valueStr, startPosition);
    }

    /// <summary>
    /// Skips whitespace and captures comments with their positions.
    /// Comments are stored for later association with AST nodes.
    /// </summary>
    private void SkipWhitespaceAndCaptureComments()
    {
        while (!IsAtEnd())
        {
            var ch = Peek();

            // Whitespace
            if (IsWhitespace(ch))
            {
                Advance();
                continue;
            }

            // Line comment: -- ...
            if (ch == '-' && PeekNext() == '-')
            {
                var startPosition = _position;
                Advance(); // consume first -
                Advance(); // consume second -

                var commentText = new System.Text.StringBuilder();
                while (!IsAtEnd() && Peek() != '\n')
                {
                    commentText.Append(Peek());
                    Advance();
                }

                // Store the comment with trimmed text
                var trimmedText = commentText.ToString().Trim();
                if (trimmedText.Length > 0)
                {
                    _comments.Add(new SqlComment(trimmedText, startPosition, false));
                }
                continue;
            }

            // Block comment: /* ... */
            if (ch == '/' && PeekNext() == '*')
            {
                var startPosition = _position;
                Advance(); // consume /
                Advance(); // consume *

                var commentText = new System.Text.StringBuilder();
                while (!IsAtEnd() && !(Peek() == '*' && PeekNext() == '/'))
                {
                    commentText.Append(Peek());
                    Advance();
                }

                if (!IsAtEnd())
                {
                    Advance(); // consume *
                    Advance(); // consume /
                }

                // Store the comment with trimmed text
                var trimmedText = commentText.ToString().Trim();
                if (trimmedText.Length > 0)
                {
                    _comments.Add(new SqlComment(trimmedText, startPosition, true));
                }
                continue;
            }

            break;
        }
    }

    private char Peek() => _position < _sql.Length ? _sql[_position] : '\0';

    private char PeekNext() => _position + 1 < _sql.Length ? _sql[_position + 1] : '\0';

    private void Advance() => _position++;

    private bool IsAtEnd() => _position >= _sql.Length;

    private static bool IsWhitespace(char ch) => ch is ' ' or '\t' or '\n' or '\r';

    private static bool IsDigit(char ch) => ch is >= '0' and <= '9';

    private static bool IsIdentifierStart(char ch) => ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_';

    private static bool IsIdentifierChar(char ch) => IsIdentifierStart(ch) || IsDigit(ch);
}
