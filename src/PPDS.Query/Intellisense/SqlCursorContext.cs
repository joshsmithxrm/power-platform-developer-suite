using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Sql.Intellisense;

namespace PPDS.Query.Intellisense;

/// <summary>
/// Analyzes cursor position in SQL text using ScriptDom tokens and AST
/// to determine the appropriate completion context.
/// Falls back to token-based heuristics when the AST parse fails (partial SQL).
/// </summary>
public static class SqlCursorContext
{
    #region Keyword Groups

    private static readonly string[] StatementStartKeywords =
        { "SELECT", "INSERT", "UPDATE", "DELETE" };

    private static readonly string[] AfterSelectKeywords =
        { "DISTINCT", "TOP", "FROM", "COUNT", "SUM", "AVG", "MIN", "MAX" };

    private static readonly string[] AfterFromEntityKeywords =
        { "WHERE", "ORDER BY", "JOIN", "INNER JOIN", "LEFT JOIN", "GROUP BY", "AS" };

    private static readonly string[] AfterJoinEntityKeywords =
        { "ON", "AS" };

    private static readonly string[] AfterWhereConditionKeywords =
        { "AND", "OR", "ORDER BY", "GROUP BY" };

    private static readonly string[] WhereOperatorKeywords =
        { "IS", "IS NOT", "IN", "NOT IN", "LIKE", "NOT LIKE", "BETWEEN", "NULL" };

    private static readonly string[] AfterOrderByAttrKeywords =
        { "ASC", "DESC" };

    private static readonly string[] AfterOrderByCompleteKeywords =
        { "LIMIT" };

    #endregion

    /// <summary>
    /// Determines the completion context at the given cursor offset in SQL text.
    /// Uses ScriptDom for both AST-based and token-based analysis.
    /// </summary>
    /// <param name="sql">The SQL text being edited.</param>
    /// <param name="cursorOffset">The 0-based character offset of the cursor.</param>
    /// <returns>A context result describing what completions are appropriate.</returns>
    public static SqlCursorContextResult Analyze(string sql, int cursorOffset)
    {
        if (string.IsNullOrEmpty(sql) || cursorOffset < 0)
        {
            return new SqlCursorContextResult
            {
                Kind = SqlCompletionContextKind.Keyword,
                KeywordSuggestions = StatementStartKeywords
            };
        }

        // Clamp cursor offset
        cursorOffset = Math.Min(cursorOffset, sql.Length);

        // Try AST-based analysis first
        try
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            var fragment = parser.Parse(new StringReader(sql), out IList<ParseError> errors);

            if (errors.Count == 0 && fragment is TSqlScript script)
            {
                var selectStatement = FindSelectAtOffset(script, cursorOffset);
                if (selectStatement != null)
                {
                    return AnalyzeSelectStatement(sql, cursorOffset, selectStatement);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw; // Never swallow cancellation
        }
        catch (Exception)
        {
            // Parse failed on partial SQL — fall through to token-based heuristic
        }

        // Token-based fallback for partial SQL
        return AnalyzeWithTokens(sql, cursorOffset);
    }

    #region AST-based Analysis

    /// <summary>
    /// Finds the SELECT query specification that contains the given cursor offset.
    /// </summary>
    private static QuerySpecification? FindSelectAtOffset(TSqlScript script, int cursorOffset)
    {
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                var querySpec = ExtractQuerySpecification(statement);
                if (querySpec != null)
                    return querySpec;
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts the QuerySpecification from a statement (handles SELECT and SELECT with CTE, etc.).
    /// </summary>
    private static QuerySpecification? ExtractQuerySpecification(TSqlStatement statement)
    {
        if (statement is SelectStatement selectStmt)
        {
            return selectStmt.QueryExpression as QuerySpecification;
        }
        return null;
    }

    /// <summary>
    /// Analyzes cursor position within a fully-parsed SELECT statement using ScriptDom AST.
    /// </summary>
    private static SqlCursorContextResult AnalyzeSelectStatement(
        string sql, int cursorOffset, QuerySpecification querySpec)
    {
        var aliasMap = BuildAliasMap(querySpec);
        var prefix = ExtractPrefix(sql, cursorOffset);

        // Check if cursor is in a "alias." context
        var dotContext = GetDotContext(sql, cursorOffset);
        if (dotContext != null)
        {
            var entity = ResolveAlias(dotContext, aliasMap);
            return new SqlCursorContextResult
            {
                Kind = SqlCompletionContextKind.Attribute,
                AliasMap = aliasMap,
                CurrentEntity = entity,
                Prefix = prefix
            };
        }

        // Use token positions to determine region
        var tokens = TokenizeSafe(sql);
        var tokenIndex = FindTokenAtOrBefore(tokens, cursorOffset);

        if (tokenIndex < 0)
        {
            return new SqlCursorContextResult
            {
                Kind = SqlCompletionContextKind.Keyword,
                KeywordSuggestions = StatementStartKeywords,
                AliasMap = aliasMap,
                Prefix = prefix
            };
        }

        var region = DetermineRegion(tokens, tokenIndex, cursorOffset, sql);
        return BuildResultForRegion(region, aliasMap, prefix);
    }

    /// <summary>
    /// Builds an alias map from a ScriptDom <see cref="QuerySpecification"/>.
    /// Extracts FROM and JOIN table references with their aliases.
    /// </summary>
    private static Dictionary<string, string> BuildAliasMap(QuerySpecification querySpec)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (querySpec.FromClause == null)
            return map;

        foreach (var tableRef in querySpec.FromClause.TableReferences)
        {
            CollectTableReferences(tableRef, map);
        }

        return map;
    }

    /// <summary>
    /// Recursively collects table references and their aliases from the AST.
    /// Handles NamedTableReference (simple tables) and JoinTableReference (joins).
    /// </summary>
    private static void CollectTableReferences(
        TableReference tableRef, Dictionary<string, string> map)
    {
        switch (tableRef)
        {
            case NamedTableReference namedTable:
            {
                var tableName = GetSchemaObjectName(namedTable.SchemaObject);
                if (tableName != null)
                {
                    var alias = namedTable.Alias?.Value ?? tableName;
                    map[alias] = tableName;
                }
                break;
            }

            case JoinTableReference joinTable:
            {
                CollectTableReferences(joinTable.FirstTableReference, map);
                CollectTableReferences(joinTable.SecondTableReference, map);
                break;
            }
        }
    }

    /// <summary>
    /// Gets the table name from a <see cref="SchemaObjectName"/>.
    /// For Dataverse queries, uses the base identifier (no schema prefix).
    /// </summary>
    private static string? GetSchemaObjectName(SchemaObjectName schemaObj)
    {
        if (schemaObj == null || schemaObj.Identifiers.Count == 0)
            return null;

        // Use the last identifier (table name without schema/database prefix)
        return schemaObj.Identifiers[schemaObj.Identifiers.Count - 1].Value;
    }

    #endregion

    #region Token-based Fallback

    /// <summary>
    /// Uses ScriptDom tokens to heuristically determine the completion context
    /// when the SQL is incomplete and cannot be fully parsed.
    /// </summary>
    private static SqlCursorContextResult AnalyzeWithTokens(string sql, int cursorOffset)
    {
        var tokens = TokenizeSafe(sql);
        if (tokens.Count == 0)
        {
            return new SqlCursorContextResult
            {
                Kind = SqlCompletionContextKind.Keyword,
                KeywordSuggestions = StatementStartKeywords,
                Prefix = ""
            };
        }

        var prefix = ExtractPrefix(sql, cursorOffset);

        // Check for alias.prefix dot context
        var dotContext = GetDotContext(sql, cursorOffset);
        if (dotContext != null)
        {
            var aliasMap = BuildAliasMapFromTokens(tokens);
            var entity = ResolveAlias(dotContext, aliasMap);
            return new SqlCursorContextResult
            {
                Kind = SqlCompletionContextKind.Attribute,
                AliasMap = aliasMap,
                CurrentEntity = entity,
                Prefix = prefix
            };
        }

        var tokenIndex = FindTokenAtOrBefore(tokens, cursorOffset);
        if (tokenIndex < 0)
        {
            return new SqlCursorContextResult
            {
                Kind = SqlCompletionContextKind.Keyword,
                KeywordSuggestions = StatementStartKeywords,
                Prefix = prefix
            };
        }

        var aliasMapFallback = BuildAliasMapFromTokens(tokens);
        var region = DetermineRegion(tokens, tokenIndex, cursorOffset, sql);
        return BuildResultForRegion(region, aliasMapFallback, prefix);
    }

    /// <summary>
    /// Builds a partial alias map by scanning ScriptDom tokens for FROM/JOIN patterns.
    /// </summary>
    private static Dictionary<string, string> BuildAliasMapFromTokens(
        IReadOnlyList<TSqlParserToken> tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Look for FROM identifier [AS] alias or JOIN identifier [AS] alias
            if (token.TokenType == TSqlTokenType.From ||
                token.TokenType == TSqlTokenType.Join)
            {
                var nextIdx = NextNonWhitespace(tokens, i);
                if (nextIdx >= 0 && tokens[nextIdx].TokenType == TSqlTokenType.Identifier)
                {
                    var tableName = tokens[nextIdx].Text;
                    var alias = tableName;

                    // Check for AS alias or just alias
                    var afterTableIdx = NextNonWhitespace(tokens, nextIdx);
                    if (afterTableIdx >= 0)
                    {
                        if (tokens[afterTableIdx].TokenType == TSqlTokenType.As)
                        {
                            var aliasIdx = NextNonWhitespace(tokens, afterTableIdx);
                            if (aliasIdx >= 0 && tokens[aliasIdx].TokenType == TSqlTokenType.Identifier)
                            {
                                alias = tokens[aliasIdx].Text;
                            }
                        }
                        else if (tokens[afterTableIdx].TokenType == TSqlTokenType.Identifier &&
                                 !IsClauseKeyword(tokens[afterTableIdx].TokenType))
                        {
                            alias = tokens[afterTableIdx].Text;
                        }
                    }

                    map[alias] = tableName;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Returns the index of the next non-whitespace token after the given index,
    /// or -1 if none exists.
    /// </summary>
    private static int NextNonWhitespace(IReadOnlyList<TSqlParserToken> tokens, int fromIndex)
    {
        for (var i = fromIndex + 1; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType != TSqlTokenType.WhiteSpace &&
                tokens[i].TokenType != TSqlTokenType.EndOfFile)
            {
                return i;
            }
        }
        return -1;
    }

    #endregion

    #region Region Detection

    private enum CursorRegion
    {
        StatementStart,
        AfterSelect,
        SelectColumnList,
        AfterFrom,
        AfterFromEntity,
        AfterJoin,
        AfterJoinEntity,
        AfterOn,
        AfterWhere,
        AfterWhereCondition,
        AfterGroupBy,
        AfterOrderBy,
        AfterOrderByAttr,
        AfterOrderByComplete,
        InString,
        Unknown
    }

    /// <summary>
    /// Determines the cursor region by walking ScriptDom tokens backward from the cursor.
    /// Skips whitespace tokens to find the relevant keyword context.
    /// </summary>
    private static CursorRegion DetermineRegion(
        IReadOnlyList<TSqlParserToken> tokens, int tokenIndex, int cursorOffset, string sql)
    {
        // Walk backward to find the most relevant keyword
        for (var i = tokenIndex; i >= 0; i--)
        {
            var token = tokens[i];

            // Skip whitespace
            if (token.TokenType == TSqlTokenType.WhiteSpace)
                continue;

            // If cursor is inside a string literal, no completions
            if ((token.TokenType == TSqlTokenType.AsciiStringLiteral ||
                 token.TokenType == TSqlTokenType.UnicodeStringLiteral) &&
                cursorOffset > token.Offset &&
                cursorOffset < token.Offset + token.Text.Length)
            {
                return CursorRegion.InString;
            }

            switch (token.TokenType)
            {
                case TSqlTokenType.Select:
                {
                    // Is cursor immediately after SELECT (no columns yet)?
                    var nextIdx = NextNonWhitespace(tokens, i);
                    if (nextIdx < 0 || nextIdx >= tokenIndex ||
                        (nextIdx == tokenIndex && IsPartialIdentifier(tokens, tokenIndex)))
                    {
                        return CursorRegion.AfterSelect;
                    }
                    // Otherwise we're in the column list
                    return CursorRegion.SelectColumnList;
                }

                case TSqlTokenType.From:
                {
                    // Check if there's an entity after FROM
                    var nextIdx = NextNonWhitespace(tokens, i);
                    if (nextIdx >= 0 && tokens[nextIdx].TokenType == TSqlTokenType.Identifier)
                    {
                        var entityTokenEnd = tokens[nextIdx].Offset + tokens[nextIdx].Text.Length;
                        if (cursorOffset > entityTokenEnd)
                        {
                            return CursorRegion.AfterFromEntity;
                        }
                    }
                    // Cursor is right after FROM or mid-entity name
                    return CursorRegion.AfterFrom;
                }

                case TSqlTokenType.Join:
                {
                    // Check if there's an entity after JOIN
                    var nextIdx = NextNonWhitespace(tokens, i);
                    if (nextIdx >= 0 && tokens[nextIdx].TokenType == TSqlTokenType.Identifier)
                    {
                        var entityTokenEnd = tokens[nextIdx].Offset + tokens[nextIdx].Text.Length;
                        if (cursorOffset > entityTokenEnd)
                        {
                            return CursorRegion.AfterJoinEntity;
                        }
                    }
                    return CursorRegion.AfterJoin;
                }

                case TSqlTokenType.Inner:
                case TSqlTokenType.Left:
                case TSqlTokenType.Right:
                {
                    // These precede JOIN — check if JOIN follows
                    var nextIdx = NextNonWhitespace(tokens, i);
                    if (nextIdx >= 0 && tokens[nextIdx].TokenType == TSqlTokenType.Join)
                    {
                        continue; // let the JOIN token handler deal with it
                    }
                    return CursorRegion.AfterJoin;
                }

                case TSqlTokenType.On:
                    return CursorRegion.AfterOn;

                case TSqlTokenType.Where:
                {
                    // Check if there's already a condition after WHERE
                    if (HasConditionTokensBetween(tokens, i + 1, tokenIndex))
                    {
                        return CursorRegion.AfterWhereCondition;
                    }
                    return CursorRegion.AfterWhere;
                }

                case TSqlTokenType.And:
                case TSqlTokenType.Or:
                    return CursorRegion.AfterWhere;

                case TSqlTokenType.Group:
                    return CursorRegion.AfterGroupBy;

                case TSqlTokenType.Order:
                {
                    // Check if there's already a column after ORDER BY
                    if (HasOrderByColumn(tokens, i, tokenIndex))
                    {
                        // Check if ASC/DESC was already specified
                        if (tokenIndex > i && IsAscDescToken(tokens[tokenIndex]))
                        {
                            return CursorRegion.AfterOrderByComplete;
                        }
                        return CursorRegion.AfterOrderByAttr;
                    }
                    return CursorRegion.AfterOrderBy;
                }

                case TSqlTokenType.Asc:
                case TSqlTokenType.Desc:
                    return CursorRegion.AfterOrderByComplete;

                case TSqlTokenType.Comma:
                    // Walk further back to find what clause we're in
                    continue;
            }
        }

        return CursorRegion.StatementStart;
    }

    private static bool IsPartialIdentifier(IReadOnlyList<TSqlParserToken> tokens, int index)
    {
        if (index >= tokens.Count) return false;
        var tokenType = tokens[index].TokenType;
        return tokenType == TSqlTokenType.Identifier || tokenType == TSqlTokenType.EndOfFile;
    }

    private static bool HasConditionTokensBetween(
        IReadOnlyList<TSqlParserToken> tokens, int start, int end)
    {
        for (var i = start; i <= end && i < tokens.Count; i++)
        {
            var tt = tokens[i].TokenType;
            if (tt == TSqlTokenType.WhiteSpace)
                continue;

            if (tt == TSqlTokenType.Identifier ||
                tt == TSqlTokenType.AsciiStringLiteral ||
                tt == TSqlTokenType.UnicodeStringLiteral ||
                tt == TSqlTokenType.Integer ||
                tt == TSqlTokenType.Real ||
                tt == TSqlTokenType.Numeric ||
                IsComparisonOperator(tt))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsComparisonOperator(TSqlTokenType tt)
    {
        return tt == TSqlTokenType.EqualsSign ||
               tt == TSqlTokenType.LessThan ||
               tt == TSqlTokenType.GreaterThan;
    }

    private static bool HasOrderByColumn(
        IReadOnlyList<TSqlParserToken> tokens, int orderIndex, int tokenIndex)
    {
        // Skip ORDER, BY tokens
        var start = orderIndex + 1;

        // Skip whitespace after ORDER
        while (start < tokens.Count && tokens[start].TokenType == TSqlTokenType.WhiteSpace)
            start++;

        if (start < tokens.Count && tokens[start].TokenType == TSqlTokenType.By)
        {
            start++;
        }

        for (var i = start; i <= tokenIndex && i < tokens.Count; i++)
        {
            if (tokens[i].TokenType == TSqlTokenType.Identifier)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAscDescToken(TSqlParserToken token)
    {
        return token.TokenType == TSqlTokenType.Asc ||
               token.TokenType == TSqlTokenType.Desc;
    }

    private static bool IsClauseKeyword(TSqlTokenType type)
    {
        return type == TSqlTokenType.From ||
               type == TSqlTokenType.Where ||
               type == TSqlTokenType.Group ||
               type == TSqlTokenType.Having ||
               type == TSqlTokenType.Order ||
               type == TSqlTokenType.Join ||
               type == TSqlTokenType.Inner ||
               type == TSqlTokenType.Left ||
               type == TSqlTokenType.Right ||
               type == TSqlTokenType.On ||
               type == TSqlTokenType.Set ||
               type == TSqlTokenType.Values;
    }

    #endregion

    #region Result Building

    private static SqlCursorContextResult BuildResultForRegion(
        CursorRegion region,
        Dictionary<string, string> aliasMap,
        string prefix)
    {
        switch (region)
        {
            case CursorRegion.StatementStart:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = StatementStartKeywords,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.AfterSelect:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = AfterSelectKeywords,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.SelectColumnList:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Attribute,
                    AliasMap = aliasMap,
                    CurrentEntity = null, // all in-scope tables
                    Prefix = prefix
                };

            case CursorRegion.AfterFrom:
            case CursorRegion.AfterJoin:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Entity,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.AfterFromEntity:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = AfterFromEntityKeywords,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.AfterJoinEntity:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = AfterJoinEntityKeywords,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.AfterOn:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Attribute,
                    AliasMap = aliasMap,
                    CurrentEntity = null, // both tables
                    Prefix = prefix
                };

            case CursorRegion.AfterWhere:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Attribute,
                    AliasMap = aliasMap,
                    CurrentEntity = null,
                    Prefix = prefix
                };

            case CursorRegion.AfterWhereCondition:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = AfterWhereConditionKeywords
                        .Concat(WhereOperatorKeywords).ToArray(),
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.AfterGroupBy:
            case CursorRegion.AfterOrderBy:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Attribute,
                    AliasMap = aliasMap,
                    CurrentEntity = null,
                    Prefix = prefix
                };

            case CursorRegion.AfterOrderByAttr:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = AfterOrderByAttrKeywords,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.AfterOrderByComplete:
                return new SqlCursorContextResult
                {
                    Kind = SqlCompletionContextKind.Keyword,
                    KeywordSuggestions = AfterOrderByCompleteKeywords,
                    AliasMap = aliasMap,
                    Prefix = prefix
                };

            case CursorRegion.InString:
                return SqlCursorContextResult.None();

            default:
                return SqlCursorContextResult.None();
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Extracts the text prefix at the cursor position (partial identifier being typed).
    /// </summary>
    private static string ExtractPrefix(string sql, int cursorOffset)
    {
        if (cursorOffset <= 0 || cursorOffset > sql.Length)
            return "";

        var end = cursorOffset;
        var start = end;

        while (start > 0)
        {
            var ch = sql[start - 1];
            if (IsIdentifierChar(ch))
            {
                start--;
            }
            else
            {
                break;
            }
        }

        return start < end ? sql[start..end] : "";
    }

    /// <summary>
    /// Checks if the cursor position is preceded by "alias." and returns the alias.
    /// </summary>
    private static string? GetDotContext(string sql, int cursorOffset)
    {
        if (cursorOffset <= 1 || cursorOffset > sql.Length)
            return null;

        // Find where the current partial identifier starts
        var identEnd = cursorOffset;
        var identStart = identEnd;
        while (identStart > 0 && IsIdentifierChar(sql[identStart - 1]))
        {
            identStart--;
        }

        // Check if there's a dot before the identifier
        var dotPos = identStart - 1;
        if (dotPos < 0 || sql[dotPos] != '.')
            return null;

        // Extract the alias before the dot
        var aliasEnd = dotPos;
        var aliasStart = aliasEnd;
        while (aliasStart > 0 && IsIdentifierChar(sql[aliasStart - 1]))
        {
            aliasStart--;
        }

        if (aliasStart >= aliasEnd)
            return null;

        return sql[aliasStart..aliasEnd];
    }

    /// <summary>
    /// Resolves an alias to its entity name using the alias map.
    /// Returns the alias itself if not found in the map (might be the entity name directly).
    /// </summary>
    private static string? ResolveAlias(string alias, Dictionary<string, string> aliasMap)
    {
        if (aliasMap.TryGetValue(alias, out var entity))
        {
            return entity;
        }

        // Check if it matches a table name directly
        foreach (var kvp in aliasMap)
        {
            if (kvp.Value.Equals(alias, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return alias;
    }

    /// <summary>
    /// Tokenizes SQL safely using ScriptDom, returning empty list on error.
    /// Filters out EndOfFile tokens.
    /// </summary>
    private static IReadOnlyList<TSqlParserToken> TokenizeSafe(string sql)
    {
        try
        {
            var parser = new TSql170Parser(initialQuotedIdentifiers: true);
            var tokens = parser.GetTokenStream(new StringReader(sql), out IList<ParseError> _);

            if (tokens == null)
                return Array.Empty<TSqlParserToken>();

            // Return tokens as-is (including whitespace) for region detection.
            // Filter out EndOfFile.
            var result = new List<TSqlParserToken>(tokens.Count);
            foreach (var token in tokens)
            {
                if (token.TokenType != TSqlTokenType.EndOfFile)
                    result.Add(token);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return Array.Empty<TSqlParserToken>();
        }
    }

    /// <summary>
    /// Finds the token index at or just before the cursor offset.
    /// Skips whitespace tokens to find the most relevant token.
    /// </summary>
    private static int FindTokenAtOrBefore(IReadOnlyList<TSqlParserToken> tokens, int cursorOffset)
    {
        var lastIndex = -1;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Offset <= cursorOffset)
            {
                lastIndex = i;
            }
            else
            {
                break;
            }
        }

        return lastIndex;
    }

    private static bool IsIdentifierChar(char ch)
    {
        return ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_' or (>= '0' and <= '9');
    }

    #endregion
}
