using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Planning;

/// <summary>
/// Parses query hints from ScriptDom AST and SQL comments.
/// Standard OPTION() hints (MAXDOP) are extracted from the AST.
/// Custom hints use SQL comment syntax: <c>-- ppds:HINT_NAME [value]</c>.
/// Unrecognized hints are silently ignored.
/// </summary>
public static class QueryHintParser
{
    private const string CommentPrefix = "ppds:";

    /// <summary>
    /// Extracts recognized query hints from a parsed SQL fragment and raw SQL text.
    /// </summary>
    public static QueryHintOverrides Parse(TSqlFragment fragment)
    {
        var overrides = new QueryHintOverrides();

        // Extract standard hints from AST (MAXDOP, etc.)
        var statement = fragment is TSqlScript script && script.Batches.Count > 0
            ? script.Batches[0].Statements.FirstOrDefault()
            : fragment as TSqlStatement;

        if (statement is StatementWithCtesAndXmlNamespaces stmtWithHints
            && stmtWithHints.OptimizerHints != null)
        {
            foreach (var hint in stmtWithHints.OptimizerHints)
            {
                if (hint is LiteralOptimizerHint literalHint
                    && literalHint.HintKind.ToString().Equals("MaxDop", StringComparison.OrdinalIgnoreCase)
                    && literalHint.Value?.Value is string maxdopStr
                    && int.TryParse(maxdopStr, out var maxdop))
                {
                    overrides.MaxParallelism = maxdop;
                }
            }
        }

        // Extract custom hints from SQL comments (-- ppds:HINT_NAME [value])
        ParseCommentHints(fragment, overrides);

        return overrides;
    }

    private static void ParseCommentHints(TSqlFragment fragment, QueryHintOverrides overrides)
    {
        // ScriptDom preserves comments in the token stream but doesn't expose them
        // on the AST. We need to walk the raw SQL text to find comment-based hints.
        // The fragment has ScriptTokenStream with all tokens including comments.
        if (fragment.ScriptTokenStream == null)
            return;

        foreach (var token in fragment.ScriptTokenStream)
        {
            if (token.TokenType != TSqlTokenType.SingleLineComment)
                continue;

            var text = token.Text?.Trim();
            if (text == null)
                continue;

            // Strip leading "--" and whitespace
            if (text.StartsWith("--"))
                text = text[2..].TrimStart();

            if (!text.StartsWith(CommentPrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var hintText = text[CommentPrefix.Length..].Trim();
            ApplyCommentHint(hintText, overrides);
        }
    }

    private static void ApplyCommentHint(string hintText, QueryHintOverrides overrides)
    {
        // Split into name and optional value
        var parts = hintText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var name = parts[0].ToUpperInvariant();
        var value = parts.Length > 1 ? parts[1].Trim() : null;

        switch (name)
        {
            case "USE_TDS":
                overrides.UseTdsEndpoint = true;
                break;
            case "BYPASS_PLUGINS":
                overrides.BypassPlugins = true;
                break;
            case "BYPASS_FLOWS":
                overrides.BypassFlows = true;
                break;
            case "NOLOCK":
                overrides.NoLock = true;
                break;
            case "BATCH_SIZE" when int.TryParse(value, out var batchSize):
                overrides.DmlBatchSize = batchSize;
                break;
            case "MAX_ROWS" when int.TryParse(value, out var maxRows):
                overrides.MaxResultRows = maxRows;
                break;
            case "MAXDOP" when int.TryParse(value, out var maxdop):
                overrides.MaxParallelism = maxdop;
                break;
        }
    }
}

/// <summary>
/// Per-query overrides extracted from OPTION() hints and SQL comments.
/// Null values mean "no override — use profile/session default".
/// </summary>
public sealed class QueryHintOverrides
{
    public bool? UseTdsEndpoint { get; set; }
    public int? DmlBatchSize { get; set; }
    public int? MaxParallelism { get; set; }
    public int? MaxResultRows { get; set; }
    public bool? BypassPlugins { get; set; }
    public bool? BypassFlows { get; set; }
    public bool? NoLock { get; set; }
    public bool? ForceClientAggregation { get; set; }
}
