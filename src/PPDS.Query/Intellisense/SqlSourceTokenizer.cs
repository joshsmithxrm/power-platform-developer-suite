using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Query.Intellisense;

/// <summary>
/// Tokenizes SQL text for syntax highlighting using ScriptDom's <see cref="TSql170Parser"/>.
/// Maps <see cref="TSqlTokenType"/> to <see cref="SourceTokenType"/> for rendering.
/// Never throws — invalid input produces Error tokens for the unparseable region.
/// </summary>
public sealed class SqlSourceTokenizer : ISourceTokenizer
{
    /// <summary>
    /// Set of <see cref="TSqlTokenType"/> values that represent SQL keywords.
    /// Used to distinguish keywords from identifiers in the token stream.
    /// </summary>
    private static readonly HashSet<TSqlTokenType> KeywordTokenTypes = new()
    {
        TSqlTokenType.Select,
        TSqlTokenType.From,
        TSqlTokenType.Where,
        TSqlTokenType.And,
        TSqlTokenType.Or,
        TSqlTokenType.Order,
        TSqlTokenType.By,
        TSqlTokenType.Asc,
        TSqlTokenType.Desc,
        TSqlTokenType.Top,
        TSqlTokenType.Is,
        TSqlTokenType.Null,
        TSqlTokenType.Not,
        TSqlTokenType.In,
        TSqlTokenType.Like,
        TSqlTokenType.Join,
        TSqlTokenType.Inner,
        TSqlTokenType.Left,
        TSqlTokenType.Right,
        TSqlTokenType.Outer,
        TSqlTokenType.On,
        TSqlTokenType.As,
        TSqlTokenType.Distinct,
        TSqlTokenType.Group,
        TSqlTokenType.Having,
        TSqlTokenType.Case,
        TSqlTokenType.When,
        TSqlTokenType.Then,
        TSqlTokenType.Else,
        TSqlTokenType.End,
        TSqlTokenType.Exists,
        TSqlTokenType.Union,
        TSqlTokenType.All,
        TSqlTokenType.Insert,
        TSqlTokenType.Into,
        TSqlTokenType.Values,
        TSqlTokenType.Update,
        TSqlTokenType.Set,
        TSqlTokenType.Delete,
        TSqlTokenType.Between,
        TSqlTokenType.Over,
        TSqlTokenType.Pivot,
        TSqlTokenType.Unpivot,
        TSqlTokenType.Declare,
        TSqlTokenType.If,
        TSqlTokenType.Begin,
        TSqlTokenType.Create,
        TSqlTokenType.Alter,
        TSqlTokenType.Drop,
        TSqlTokenType.Table,
        TSqlTokenType.Index,
        TSqlTokenType.View,
        TSqlTokenType.Procedure,
        TSqlTokenType.Function,
        TSqlTokenType.Trigger,
        TSqlTokenType.Return,
        TSqlTokenType.While,
        TSqlTokenType.Break,
        TSqlTokenType.Continue,
        TSqlTokenType.Go,
        TSqlTokenType.Use,
        TSqlTokenType.Grant,
        TSqlTokenType.Revoke,
        TSqlTokenType.Deny,
        TSqlTokenType.Execute,
        TSqlTokenType.Exec,
        TSqlTokenType.Cursor,
        TSqlTokenType.Open,
        TSqlTokenType.Close,
        TSqlTokenType.Fetch,
        TSqlTokenType.For,
        TSqlTokenType.Cross,
        TSqlTokenType.Full,
        TSqlTokenType.With,
        TSqlTokenType.Option,
        TSqlTokenType.Constraint,
        TSqlTokenType.Primary,
        TSqlTokenType.Foreign,
        TSqlTokenType.Key,
        TSqlTokenType.References,
        TSqlTokenType.Default,
        TSqlTokenType.Check,
        TSqlTokenType.Unique,
    };

    /// <summary>
    /// Set of <see cref="TSqlTokenType"/> values that represent SQL function names.
    /// ScriptDom has dedicated token types for CONVERT, COALESCE, and NULLIF.
    /// </summary>
    private static readonly HashSet<TSqlTokenType> FunctionTokenTypes = new()
    {
        TSqlTokenType.Convert,
        TSqlTokenType.Coalesce,
        TSqlTokenType.NullIf,
    };

    /// <summary>
    /// Set of identifier texts that should be classified as functions.
    /// ScriptDom tokenizes aggregate functions, CAST, IIF, and window functions
    /// as <see cref="TSqlTokenType.Identifier"/> rather than as dedicated token types.
    /// </summary>
    private static readonly HashSet<string> FunctionIdentifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        "COUNT",
        "SUM",
        "AVG",
        "MIN",
        "MAX",
        "CAST",
        "IIF",
        "ROW_NUMBER",
        "RANK",
        "DENSE_RANK",
    };

    /// <inheritdoc />
    public IReadOnlyList<SourceToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<SourceToken>();

        try
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            IList<ParseError> errors;
            var scriptDomTokens = parser.GetTokenStream(new StringReader(text), out errors);

            if (scriptDomTokens == null || scriptDomTokens.Count == 0)
                return new[] { new SourceToken(0, text.Length, SourceTokenType.Error) };

            return BuildSourceTokens(text, scriptDomTokens);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Tokenizer threw — fall back to error token for entire input.
            return new[] { new SourceToken(0, text.Length, SourceTokenType.Error) };
        }
    }

    private static List<SourceToken> BuildSourceTokens(
        string text, IList<TSqlParserToken> tokens)
    {
        var result = new List<SourceToken>(tokens.Count);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Skip the end-of-file token — it has zero length.
            if (token.TokenType == TSqlTokenType.EndOfFile)
                continue;

            // ScriptDom uses Offset and Text.Length for position/length.
            var start = token.Offset;
            var length = token.Text.Length;

            // Guard against tokens that extend beyond the source text.
            if (start < 0 || start >= text.Length)
                continue;

            if (start + length > text.Length)
                length = text.Length - start;

            if (length <= 0)
                continue;

            // Skip whitespace — the old tokenizer never emitted whitespace tokens.
            if (token.TokenType == TSqlTokenType.WhiteSpace)
                continue;

            var sourceType = MapTokenType(token, tokens, i);
            result.Add(new SourceToken(start, length, sourceType));
        }

        return result;
    }

    /// <summary>
    /// Maps a ScriptDom <see cref="TSqlParserToken"/> to a <see cref="SourceTokenType"/>.
    /// </summary>
    private static SourceTokenType MapTokenType(
        TSqlParserToken token, IList<TSqlParserToken> tokens, int index)
    {
        var tokenType = token.TokenType;

        // Functions
        if (FunctionTokenTypes.Contains(tokenType))
            return SourceTokenType.Function;

        // Check for function-like identifiers (ROW_NUMBER, RANK, DENSE_RANK)
        if (tokenType == TSqlTokenType.Identifier &&
            FunctionIdentifiers.Contains(token.Text))
        {
            return SourceTokenType.Function;
        }

        // Keywords
        if (KeywordTokenTypes.Contains(tokenType))
            return SourceTokenType.Keyword;

        switch (tokenType)
        {
            // Identifiers
            case TSqlTokenType.Identifier:
            case TSqlTokenType.QuotedIdentifier:
            case TSqlTokenType.PseudoColumn:
                return SourceTokenType.Identifier;

            // String literals
            case TSqlTokenType.AsciiStringLiteral:
            case TSqlTokenType.UnicodeStringLiteral:
                return SourceTokenType.StringLiteral;

            // Numeric literals
            case TSqlTokenType.Integer:
            case TSqlTokenType.Real:
            case TSqlTokenType.Numeric:
            case TSqlTokenType.Money:
            case TSqlTokenType.HexLiteral:
                return SourceTokenType.NumericLiteral;

            // Comments
            case TSqlTokenType.SingleLineComment:
            case TSqlTokenType.MultilineComment:
                return SourceTokenType.Comment;

            // Operators
            case TSqlTokenType.EqualsSign:
            case TSqlTokenType.LessThan:
            case TSqlTokenType.GreaterThan:
            case TSqlTokenType.Plus:
            case TSqlTokenType.Minus:
            case TSqlTokenType.Divide:
            case TSqlTokenType.PercentSign:
            case TSqlTokenType.Tilde:
            case TSqlTokenType.Ampersand:
            case TSqlTokenType.VerticalLine:
            case TSqlTokenType.Circumflex:
            case TSqlTokenType.Bang:
                return SourceTokenType.Operator;

            // Star can be operator (multiply) or wildcard
            case TSqlTokenType.Star:
                return SourceTokenType.Operator;

            // Punctuation
            case TSqlTokenType.Comma:
            case TSqlTokenType.Dot:
            case TSqlTokenType.LeftParenthesis:
            case TSqlTokenType.RightParenthesis:
            case TSqlTokenType.Semicolon:
            case TSqlTokenType.LeftCurly:
            case TSqlTokenType.RightCurly:
            case TSqlTokenType.Colon:
            case TSqlTokenType.DoubleColon:
                return SourceTokenType.Punctuation;

            // Variables
            case TSqlTokenType.Variable:
                return SourceTokenType.Variable;

            default:
                // For any unrecognized token type, try to determine if it looks
                // like a keyword by checking the token text against known patterns.
                // ScriptDom classifies many T-SQL reserved words as specific token
                // types that we may not have enumerated above.
                if (IsLikelyKeyword(tokenType))
                    return SourceTokenType.Keyword;

                return SourceTokenType.Identifier;
        }
    }

    /// <summary>
    /// Heuristic check for token types that are likely keywords but not
    /// explicitly listed in our keyword set. ScriptDom has hundreds of
    /// token types for T-SQL reserved words.
    /// </summary>
    private static bool IsLikelyKeyword(TSqlTokenType tokenType)
    {
        // The ScriptDom TSqlTokenType enum uses values < 200 or so for
        // operators, punctuation, literals, identifiers, and whitespace.
        // Most keyword-specific token types have higher enum values.
        // However, the safest approach is to check the name of the enum
        // value to avoid collisions with non-keyword types.
        var name = tokenType.ToString();

        // Known non-keyword prefixes/types to exclude
        if (name.StartsWith("Ascii", StringComparison.Ordinal) ||
            name.StartsWith("Unicode", StringComparison.Ordinal) ||
            name.StartsWith("Multiline", StringComparison.Ordinal) ||
            name.StartsWith("SingleLine", StringComparison.Ordinal) ||
            name == "EndOfFile" ||
            name == "WhiteSpace" ||
            name == "Variable" ||
            name == "Integer" ||
            name == "Real" ||
            name == "Numeric" ||
            name == "Money" ||
            name == "HexLiteral" ||
            name == "Identifier" ||
            name == "QuotedIdentifier" ||
            name == "PseudoColumn")
        {
            return false;
        }

        // If it's not a known literal/identifier/operator/punctuation type,
        // it's most likely a keyword.
        return true;
    }
}
