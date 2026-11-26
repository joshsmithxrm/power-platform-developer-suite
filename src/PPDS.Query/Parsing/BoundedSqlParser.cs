using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Query.Parsing;

/// <summary>
/// Runs the ScriptDom lexer and parser on a <see cref="QueryStack"/> thread, behind a bound on the text that
/// holds for any token sequence (#1952). ScriptDom recurses and cannot be changed, and a stack overflow ends
/// the process, so a text is parsed only when its counts prove the parse fits the thread's stack:
/// <list type="bullet">
/// <item><description>
/// Before lexing: the lexer recurses only for nested block comments, and every nesting level starts at its
/// own <c>/*</c>, so the number of <c>/*</c> in the text (anywhere: in strings, names, line comments) bounds
/// its depth.
/// </description></item>
/// <item><description>
/// Before parsing: every level of the parser's recursion, and of the tree walk inside <c>Parse</c>, is
/// entered at an operator, a keyword, a name or a parenthesis, never at a numeric or string literal or a
/// comma, so the number of the other tokens bounds its depth. Those all count the same, whatever their kind:
/// names and single keywords also carry nesting (<c>AT TIME ZONE</c> chains, <c>CASE</c> without <c>END</c>
/// in unfinished text). Literals and commas are not counted, so long flat lists (an IN list, VALUES rows and
/// columns, a select list, function arguments) cost almost nothing; runs of them were measured flat in every
/// list context, even where they are a syntax error. White space and comments are not grammar input.
/// </description></item>
/// </list>
/// Each count is multiplied by the worst stack cost measured per unit (any shape found, opener-only text
/// included), a margin of 2, and a factor for the platform; see the calibration tests.
/// </summary>
internal static class BoundedSqlParser
{
    /// <summary>
    /// Worst stack measured per counted token, Linux x64, cold JIT: 2,216 bytes on .NET 8, 2,104 on .NET 9 and
    /// 1,952 on .NET 10 (<c>(SELECT </c> without closers), rounded up. Over 80 single shapes, 59 shapes with
    /// literals and commas placed as densely as the grammar allows, and nearly 1,500 random nesting paths on both
    /// ScriptDom parsers, no text cost more.
    /// </summary>
    internal const int BytesPerToken = 2_300;

    /// <summary>Worst stack measured per nested <c>/*</c> in the lexer, Linux x64, cold JIT: 384 bytes at most, rounded up.</summary>
    internal const int BytesPerCommentOpen = 400;

    /// <summary>Margin over the measured worst cost.</summary>
    internal const int Margin = 2;

    /// <summary>Stack kept back from the budget: the parser's base use (50-130 KB measured) plus the runtime's reserve and slack.</summary>
    internal const int FixedReserve = 1 << 20;

    /// <summary>
    /// How much larger stack frames are on this platform than on Linux x64, where the weights were measured.
    /// Linux x64 is 1. Elsewhere it is 3 until the calibration test has measured that platform: one Windows
    /// crash suggested about 2.3.
    /// </summary>
    internal static readonly double PlatformFactor =
        RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && RuntimeInformation.ProcessArchitecture == Architecture.X64
            ? 1.0
            : 3.0;

    /// <summary>The bytes one counted token is charged on this platform, with the margin.</summary>
    internal static double ChargePerToken => Margin * PlatformFactor * BytesPerToken;

    /// <summary>The bytes one <c>/*</c> is charged on this platform, with the margin.</summary>
    internal static double ChargePerCommentOpen => Margin * PlatformFactor * BytesPerCommentOpen;

    /// <summary>
    /// The most counted tokens a text may hold on a thread with <paramref name="stackSize"/> bytes of stack.
    /// </summary>
    internal static long MaxTokens(int stackSize) => (long)((stackSize - (long)FixedReserve) / ChargePerToken);

    /// <summary>
    /// The most <c>/*</c> a text may hold on a thread with <paramref name="stackSize"/> bytes of stack.
    /// </summary>
    internal static long MaxCommentOpens(int stackSize) => (long)((stackSize - (long)FixedReserve) / ChargePerCommentOpen);

    /// <summary>
    /// Lexes and parses <paramref name="sql"/> with <paramref name="parser"/> on a <see cref="QueryStack"/> thread.
    /// </summary>
    /// <exception cref="QueryNestedTooDeeplyException">The text is over the bound; nothing was parsed.</exception>
    internal static TSqlFragment Parse(TSqlParser parser, string sql, out IList<ParseError> errors)
    {
        var (fragment, parseErrors) = QueryStack.Run(() => ParseOnThisThread(parser, sql));
        errors = parseErrors;
        return fragment;
    }

    /// <summary>
    /// Lexes <paramref name="sql"/> with <paramref name="parser"/> on a <see cref="QueryStack"/> thread.
    /// </summary>
    /// <exception cref="QueryNestedTooDeeplyException">The text holds more <c>/*</c> than the lexer can nest; nothing was lexed.</exception>
    internal static IList<TSqlParserToken> GetTokenStream(TSqlParser parser, string sql, out IList<ParseError> errors)
    {
        var (tokens, lexErrors) = QueryStack.Run(() =>
        {
            CheckCommentOpens(sql, StackSizeOfThisThread());
            using var reader = new StringReader(sql);
            var list = parser.GetTokenStream(reader, out var e);
            return (list, e);
        });
        errors = lexErrors;
        return tokens;
    }

    private static (TSqlFragment Fragment, IList<ParseError> Errors) ParseOnThisThread(TSqlParser parser, string sql)
    {
        var stackSize = StackSizeOfThisThread();
        CheckCommentOpens(sql, stackSize);

        IList<TSqlParserToken> tokens;
        using (var tokenReader = new StringReader(sql))
            tokens = parser.GetTokenStream(tokenReader, out _);
        CheckTokens(tokens, stackSize);

        // Parse the text itself (the lexer is deterministic, so it produces the same tokens again):
        // that keeps every result and error exactly as before.
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        return (fragment, errors);
    }

    private static int StackSizeOfThisThread()
    {
        var size = QueryStack.CurrentThreadStackSize;
        if (size <= 0)
            throw new InvalidOperationException("The SQL parser must run on a QueryStack thread.");
        return size;
    }

    internal static void CheckCommentOpens(string sql, int stackSize)
    {
        if (CountCommentOpens(sql) > MaxCommentOpens(stackSize))
            throw new QueryNestedTooDeeplyException(QueryDepthLimit.CommentNesting);
    }

    internal static void CheckTokens(IList<TSqlParserToken> tokens, int stackSize)
    {
        if (CountTokens(tokens) > MaxTokens(stackSize))
            throw new QueryNestedTooDeeplyException(QueryDepthLimit.TokenCount);
    }

    /// <summary>The number of <c>/*</c> anywhere in the text.</summary>
    internal static long CountCommentOpens(string sql)
    {
        long count = 0;
        var index = 0;
        while ((index = sql.IndexOf("/*", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 2;
        }
        return count;
    }

    /// <summary>
    /// The number of counted tokens: every token except numeric and string literals, commas, white space,
    /// comments and the end of the text. A double-quoted token that may be a name
    /// (<see cref="TSqlTokenType.AsciiStringOrQuotedIdentifier"/>) is counted.
    /// </summary>
    internal static long CountTokens(IList<TSqlParserToken> tokens)
    {
        long count = 0;
        foreach (var token in tokens)
        {
            switch (token.TokenType)
            {
                case TSqlTokenType.WhiteSpace:
                case TSqlTokenType.SingleLineComment:
                case TSqlTokenType.MultilineComment:
                case TSqlTokenType.EndOfFile:
                case TSqlTokenType.Integer:
                case TSqlTokenType.Numeric:
                case TSqlTokenType.Real:
                case TSqlTokenType.Money:
                case TSqlTokenType.HexLiteral:
                case TSqlTokenType.AsciiStringLiteral:
                case TSqlTokenType.UnicodeStringLiteral:
                case TSqlTokenType.Comma:
                    continue;
                default:
                    count++;
                    break;
            }
        }
        return count;
    }
}
