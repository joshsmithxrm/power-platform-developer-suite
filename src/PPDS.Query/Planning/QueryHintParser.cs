using System;
using System.Collections.Generic;
using System.Globalization;
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
            if (token.TokenType is not (TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment))
                continue;

            var raw = token.Text?.Trim();
            if (raw == null)
                continue;
            // A hint-like mention is "ppds:" followed by a name, at the start or after whitespace (not a URL like org/ppds:).
            var body = raw.StartsWith("--") ? raw[2..] : raw.StartsWith("/*") ? raw[2..].TrimEnd('/').TrimEnd('*') : raw;
            if (!System.Text.RegularExpressions.Regex.IsMatch(body.Trim(), @"(^|\s)ppds:\s*[A-Za-z_]", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                continue;

            // Only "-- ppds:NAME [value]" is a hint. Anything else that mentions ppds: (a block comment, a misspelt
            // name, a malformed value) is recorded so DML can refuse it rather than ignore what the user wrote.
            var text = raw;
            if (token.TokenType == TSqlTokenType.SingleLineComment && text.StartsWith("--"))
                text = text[2..].TrimStart();

            if (token.TokenType != TSqlTokenType.SingleLineComment
                || !text.StartsWith(CommentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                overrides.UnrecognizedHints.Add(raw);
                continue;
            }

            // SELECT keeps the lenient parsing it always had (a flag followed by text, "+5"); DML requires the exact form.
            var hintText = text[CommentPrefix.Length..].Trim();
            ApplyLenientHint(hintText, overrides);
            if (!ApplyCommentHint(hintText, new QueryHintOverrides()))
                overrides.UnrecognizedHints.Add(raw);
        }
    }

    /// <summary>The parsing SELECT has always used: a flag may carry trailing text, a number may have a sign.</summary>
    private static void ApplyLenientHint(string hintText, QueryHintOverrides overrides)
    {
        var parts = hintText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var value = parts.Length > 1 ? parts[1].Trim() : null;
        switch (parts[0].ToUpperInvariant())
        {
            case "USE_TDS": overrides.UseTdsEndpoint = true; break;
            case "BYPASS_PLUGINS": overrides.BypassPlugins = true; break;
            case "BYPASS_FLOWS": overrides.BypassFlows = true; break;
            case "NOLOCK": overrides.NoLock = true; break;
            case "BATCH_SIZE" when int.TryParse(value, out var batchSize): overrides.DmlBatchSize = batchSize; break;
            case "MAX_ROWS" when int.TryParse(value, out var maxRows): overrides.MaxResultRows = maxRows; break;
            case "MAXDOP" when int.TryParse(value, out var maxdop): overrides.MaxParallelism = maxdop; break;
            case "HASH_GROUP" or "HASHGROUP": overrides.ForceClientAggregation = true; break;
        }
    }

    /// <summary>Applies one hint; false when the name is unknown, a value is malformed, or a flag carries a value.</summary>
    private static bool ApplyCommentHint(string hintText, QueryHintOverrides overrides)
    {
        // Split into name and optional value
        var parts = hintText.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;

        var name = parts[0].ToUpperInvariant();
        var value = parts.Length > 1 ? parts[1].Trim() : null;
        const NumberStyles integer = NumberStyles.None;

        switch (name)
        {
            case "USE_TDS" when value == null:
                overrides.UseTdsEndpoint = true;
                return true;
            case "BYPASS_PLUGINS" when value == null:
                overrides.BypassPlugins = true;
                return true;
            case "BYPASS_FLOWS" when value == null:
                overrides.BypassFlows = true;
                return true;
            case "NOLOCK" when value == null:
                overrides.NoLock = true;
                return true;
            case "BATCH_SIZE" when int.TryParse(value, integer, CultureInfo.InvariantCulture, out var batchSize):
                overrides.DmlBatchSize = batchSize;
                return true;
            case "MAX_ROWS" when int.TryParse(value, integer, CultureInfo.InvariantCulture, out var maxRows):
                overrides.MaxResultRows = maxRows;
                return true;
            case "MAXDOP" when int.TryParse(value, integer, CultureInfo.InvariantCulture, out var maxdop):
                overrides.MaxParallelism = maxdop;
                return true;
            case "HASH_GROUP" or "HASHGROUP" when value == null:
                overrides.ForceClientAggregation = true;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Per-query overrides extracted from OPTION() hints and SQL comments.
/// Null values mean "no override — use profile/session default".
/// </summary>
public sealed class QueryHintOverrides
{
    /// <summary>Override to force or disable TDS endpoint usage.</summary>
    public bool? UseTdsEndpoint { get; set; }
    /// <summary>Override batch size for DML operations.</summary>
    public int? DmlBatchSize { get; set; }
    /// <summary>Override maximum degree of parallelism.</summary>
    public int? MaxParallelism { get; set; }
    /// <summary>Override maximum number of result rows.</summary>
    public int? MaxResultRows { get; set; }
    /// <summary>Override to bypass synchronous plugins.</summary>
    public bool? BypassPlugins { get; set; }
    /// <summary>Override to bypass Power Automate flows.</summary>
    public bool? BypassFlows { get; set; }
    /// <summary>Override to use no-lock reads.</summary>
    public bool? NoLock { get; set; }
    /// <summary>Override to force client-side aggregation.</summary>
    public bool? ForceClientAggregation { get; set; }
    /// <summary>
    /// Comments that mention <c>ppds:</c> but are not an exact supported hint (block comments, unknown names,
    /// malformed values). Ignored for SELECT; a request containing DML is refused.
    /// </summary>
    public List<string> UnrecognizedHints { get; } = new();
}
