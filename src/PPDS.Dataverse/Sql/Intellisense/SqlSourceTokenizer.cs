using System;
using System.Collections.Generic;
using PPDS.Dataverse.Sql.Parsing;

namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Tokenizes SQL text for syntax highlighting by wrapping <see cref="SqlLexer"/>.
/// Maps <see cref="SqlTokenType"/> to <see cref="SourceTokenType"/> and interleaves comments.
/// Never throws — invalid input produces Error tokens for the unparseable region.
/// </summary>
public sealed class SqlSourceTokenizer : ISourceTokenizer
{
    /// <inheritdoc />
    public IReadOnlyList<SourceToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<SourceToken>();

        try
        {
            var lexer = new SqlLexer(text);
            var result = lexer.Tokenize();
            return BuildSourceTokens(text, result.Tokens, result.Comments);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Lexer threw (e.g. unterminated string) — fall back to error token for entire input
            return new[] { new SourceToken(0, text.Length, SourceTokenType.Error) };
        }
    }

    private static List<SourceToken> BuildSourceTokens(
        string text,
        IReadOnlyList<SqlToken> tokens,
        IReadOnlyList<SqlComment> comments)
    {
        // Merge tokens and comments into a single ordered list by position.
        // Comments are skipped by SqlLexer and stored separately.
        var merged = new List<SourceToken>();

        var commentIndex = 0;

        foreach (var token in tokens)
        {
            if (token.Type == SqlTokenType.Eof)
                break;

            // Insert any comments that appear before this token
            while (commentIndex < comments.Count && comments[commentIndex].Position < token.Position)
            {
                var comment = comments[commentIndex];
                var commentLength = GetCommentLength(text, comment);
                merged.Add(new SourceToken(comment.Position, commentLength, SourceTokenType.Comment));
                commentIndex++;
            }

            var sourceType = MapTokenType(token);
            var length = GetTokenLength(text, token);
            merged.Add(new SourceToken(token.Position, length, sourceType));
        }

        // Add any trailing comments after the last token
        while (commentIndex < comments.Count)
        {
            var comment = comments[commentIndex];
            var commentLength = GetCommentLength(text, comment);
            merged.Add(new SourceToken(comment.Position, commentLength, SourceTokenType.Comment));
            commentIndex++;
        }

        return merged;
    }

    private static SourceTokenType MapTokenType(SqlToken token) => token.Type switch
    {
        // Aggregate/window functions
        SqlTokenType.Count or SqlTokenType.Sum or SqlTokenType.Avg or
        SqlTokenType.Min or SqlTokenType.Max or
        SqlTokenType.RowNumber or SqlTokenType.Rank or SqlTokenType.DenseRank or
        SqlTokenType.Cast or SqlTokenType.Convert or SqlTokenType.Iif
            => SourceTokenType.Function,

        // String literals
        SqlTokenType.String => SourceTokenType.StringLiteral,

        // Numeric literals
        SqlTokenType.Number => SourceTokenType.NumericLiteral,

        // Comparison and arithmetic operators
        SqlTokenType.Equals or SqlTokenType.NotEquals or
        SqlTokenType.LessThan or SqlTokenType.GreaterThan or
        SqlTokenType.LessThanOrEqual or SqlTokenType.GreaterThanOrEqual or
        SqlTokenType.Plus or SqlTokenType.Minus or SqlTokenType.Slash or SqlTokenType.Percent
            => SourceTokenType.Operator,

        // Star can be operator (multiply) or wildcard — treat as operator
        SqlTokenType.Star => SourceTokenType.Operator,

        // Punctuation
        SqlTokenType.Comma or SqlTokenType.Dot or
        SqlTokenType.LeftParen or SqlTokenType.RightParen or SqlTokenType.Semicolon
            => SourceTokenType.Punctuation,

        // Variables
        SqlTokenType.Variable => SourceTokenType.Variable,

        // Identifiers
        SqlTokenType.Identifier => SourceTokenType.Identifier,

        // Everything else is a keyword
        _ => token.Type.IsKeyword() ? SourceTokenType.Keyword : SourceTokenType.Identifier
    };

    /// <summary>
    /// Calculates the actual length of a token in the source text.
    /// For string literals, the Value doesn't include quotes, so we re-scan.
    /// For quoted identifiers, similarly re-scan for brackets/quotes.
    /// </summary>
    private static int GetTokenLength(string text, SqlToken token)
    {
        var pos = token.Position;

        switch (token.Type)
        {
            case SqlTokenType.String:
                // String tokens: 'value' — scan to find closing quote
                if (pos < text.Length && text[pos] == '\'')
                {
                    var end = pos + 1;
                    while (end < text.Length)
                    {
                        if (text[end] == '\'' && end + 1 < text.Length && text[end + 1] == '\'')
                        {
                            end += 2; // escaped quote
                            continue;
                        }
                        if (text[end] == '\'')
                        {
                            end++;
                            break;
                        }
                        end++;
                    }
                    return end - pos;
                }
                return token.Value.Length;

            case SqlTokenType.Identifier:
                // Identifier might be bracketed [name] or "quoted"
                if (pos < text.Length && text[pos] == '[')
                {
                    var end = text.IndexOf(']', pos);
                    return end >= 0 ? end - pos + 1 : token.Value.Length;
                }
                if (pos < text.Length && text[pos] == '"')
                {
                    var end = text.IndexOf('"', pos + 1);
                    return end >= 0 ? end - pos + 1 : token.Value.Length;
                }
                return token.Value.Length;

            default:
                return token.Value.Length;
        }
    }

    /// <summary>
    /// Calculates the full length of a comment in source text, including delimiters.
    /// </summary>
    private static int GetCommentLength(string text, SqlComment comment)
    {
        var pos = comment.Position;
        if (comment.IsBlock)
        {
            // Block comment: /* ... */
            var endMarker = text.IndexOf("*/", pos + 2, StringComparison.Ordinal);
            return endMarker >= 0 ? endMarker - pos + 2 : text.Length - pos;
        }
        else
        {
            // Line comment: -- ... (to end of line)
            var newline = text.IndexOf('\n', pos);
            return newline >= 0 ? newline - pos : text.Length - pos;
        }
    }
}
