using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Parsing;

namespace PPDS.Dataverse.Sql.Intellisense;

/// <summary>
/// Analyzes AST and cursor offset to determine the completion context.
/// Falls back to lexer-based heuristics when parsing fails (partial SQL).
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
            var parser = new SqlParser(sql);
            var statement = parser.ParseStatement();

            if (statement is SqlSelectStatement select)
            {
                return AnalyzeSelectStatement(sql, cursorOffset, select);
            }
        }
        catch
        {
            // Parse failed — fall through to lexer-based heuristic
        }

        // Lexer-based fallback for partial SQL
        return AnalyzeWithLexer(sql, cursorOffset);
    }

    #region AST-based Analysis

    /// <summary>
    /// Analyzes cursor position within a fully-parsed SELECT statement.
    /// </summary>
    private static SqlCursorContextResult AnalyzeSelectStatement(
        string sql, int cursorOffset, SqlSelectStatement select)
    {
        var aliasMap = BuildAliasMap(select);
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
        // We need to tokenize to find what keyword precedes the cursor
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

        var region = DetermineRegion(tokens, tokenIndex, cursorOffset);
        return BuildResultForRegion(region, aliasMap, prefix);
    }

    private static Dictionary<string, string> BuildAliasMap(SqlSelectStatement select)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var fromAlias = select.From.Alias ?? select.From.TableName;
        map[fromAlias] = select.From.TableName;

        foreach (var join in select.Joins)
        {
            var joinAlias = join.Table.Alias ?? join.Table.TableName;
            map[joinAlias] = join.Table.TableName;
        }

        return map;
    }

    #endregion

    #region Lexer-based Fallback

    /// <summary>
    /// Uses lexer tokens to heuristically determine the completion context
    /// when the SQL is incomplete and cannot be fully parsed.
    /// </summary>
    private static SqlCursorContextResult AnalyzeWithLexer(string sql, int cursorOffset)
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
        var region = DetermineRegion(tokens, tokenIndex, cursorOffset);
        return BuildResultForRegion(region, aliasMapFallback, prefix);
    }

    /// <summary>
    /// Builds a partial alias map by scanning tokens for FROM/JOIN patterns.
    /// </summary>
    private static Dictionary<string, string> BuildAliasMapFromTokens(IReadOnlyList<SqlToken> tokens)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            // Look for FROM identifier [AS] alias or JOIN identifier [AS] alias
            if (token.Type == SqlTokenType.From || token.Type == SqlTokenType.Join)
            {
                if (i + 1 < tokens.Count && tokens[i + 1].Type == SqlTokenType.Identifier)
                {
                    var tableName = tokens[i + 1].Value;
                    var alias = tableName;

                    // Check for AS alias or just alias
                    if (i + 2 < tokens.Count)
                    {
                        if (tokens[i + 2].Type == SqlTokenType.As && i + 3 < tokens.Count &&
                            (tokens[i + 3].Type == SqlTokenType.Identifier || tokens[i + 3].Type.IsKeyword()))
                        {
                            alias = tokens[i + 3].Value;
                        }
                        else if (tokens[i + 2].Type == SqlTokenType.Identifier &&
                                 !IsClauseKeyword(tokens[i + 2].Type))
                        {
                            alias = tokens[i + 2].Value;
                        }
                    }

                    map[alias] = tableName;
                }
            }
        }

        return map;
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
    /// Determines the cursor region by walking tokens backward from the cursor.
    /// </summary>
    private static CursorRegion DetermineRegion(
        IReadOnlyList<SqlToken> tokens, int tokenIndex, int cursorOffset)
    {
        // Walk backward to find the most relevant keyword
        for (var i = tokenIndex; i >= 0; i--)
        {
            var token = tokens[i];

            // If cursor is inside a string literal, no completions
            if (token.Type == SqlTokenType.String &&
                cursorOffset > token.Position &&
                cursorOffset < token.Position + token.Value.Length + 2) // +2 for quotes
            {
                return CursorRegion.InString;
            }

            switch (token.Type)
            {
                case SqlTokenType.Select:
                    // Is cursor immediately after SELECT (no columns yet)?
                    if (i == tokenIndex || (i == tokenIndex - 1 && IsPartialIdentifier(tokens, tokenIndex)))
                    {
                        return CursorRegion.AfterSelect;
                    }
                    // Otherwise we're in the column list
                    return CursorRegion.SelectColumnList;

                case SqlTokenType.From:
                    // Check if there's an entity after FROM
                    if (i + 1 < tokens.Count && tokens[i + 1].Type == SqlTokenType.Identifier)
                    {
                        var entityTokenEnd = tokens[i + 1].Position + tokens[i + 1].Value.Length;
                        if (cursorOffset > entityTokenEnd)
                        {
                            // Check for alias after entity
                            return CursorRegion.AfterFromEntity;
                        }
                    }
                    // Cursor is right after FROM or mid-entity name
                    return CursorRegion.AfterFrom;

                case SqlTokenType.Join:
                    // Check if there's an entity after JOIN
                    if (i + 1 < tokens.Count && tokens[i + 1].Type == SqlTokenType.Identifier)
                    {
                        var entityTokenEnd = tokens[i + 1].Position + tokens[i + 1].Value.Length;
                        if (cursorOffset > entityTokenEnd)
                        {
                            return CursorRegion.AfterJoinEntity;
                        }
                    }
                    return CursorRegion.AfterJoin;

                case SqlTokenType.Inner:
                case SqlTokenType.Left:
                case SqlTokenType.Right:
                    // These precede JOIN — if no JOIN has been seen yet, we're still before JOIN entity
                    if (i + 1 < tokens.Count && tokens[i + 1].Type == SqlTokenType.Join)
                    {
                        continue; // let the JOIN token handler deal with it
                    }
                    return CursorRegion.AfterJoin;

                case SqlTokenType.On:
                    return CursorRegion.AfterOn;

                case SqlTokenType.Where:
                    // Check if there's already a condition after WHERE
                    if (i < tokenIndex && HasConditionTokensBetween(tokens, i + 1, tokenIndex))
                    {
                        return CursorRegion.AfterWhereCondition;
                    }
                    return CursorRegion.AfterWhere;

                case SqlTokenType.And:
                case SqlTokenType.Or:
                    return CursorRegion.AfterWhere;

                case SqlTokenType.Group:
                    return CursorRegion.AfterGroupBy;

                case SqlTokenType.Order:
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

                case SqlTokenType.Asc:
                case SqlTokenType.Desc:
                    return CursorRegion.AfterOrderByComplete;

                case SqlTokenType.Comma:
                    // Walk further back to find what clause we're in
                    continue;
            }
        }

        return CursorRegion.StatementStart;
    }

    private static bool IsPartialIdentifier(IReadOnlyList<SqlToken> tokens, int index)
    {
        return index < tokens.Count &&
               (tokens[index].Type == SqlTokenType.Identifier ||
                tokens[index].Type == SqlTokenType.Eof);
    }

    private static bool HasConditionTokensBetween(
        IReadOnlyList<SqlToken> tokens, int start, int end)
    {
        for (var i = start; i <= end && i < tokens.Count; i++)
        {
            if (tokens[i].Type == SqlTokenType.Identifier ||
                tokens[i].Type == SqlTokenType.String ||
                tokens[i].Type == SqlTokenType.Number ||
                tokens[i].Type.IsComparisonOperator())
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasOrderByColumn(
        IReadOnlyList<SqlToken> tokens, int orderIndex, int tokenIndex)
    {
        // Skip ORDER, BY tokens
        var start = orderIndex + 1;
        if (start < tokens.Count && tokens[start].Type == SqlTokenType.By)
        {
            start++;
        }

        for (var i = start; i <= tokenIndex && i < tokens.Count; i++)
        {
            if (tokens[i].Type == SqlTokenType.Identifier)
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAscDescToken(SqlToken token)
    {
        return token.Type == SqlTokenType.Asc || token.Type == SqlTokenType.Desc;
    }

    private static bool IsClauseKeyword(SqlTokenType type)
    {
        return type == SqlTokenType.From ||
               type == SqlTokenType.Where ||
               type == SqlTokenType.Group ||
               type == SqlTokenType.Having ||
               type == SqlTokenType.Order ||
               type == SqlTokenType.Limit ||
               type == SqlTokenType.Join ||
               type == SqlTokenType.Inner ||
               type == SqlTokenType.Left ||
               type == SqlTokenType.Right ||
               type == SqlTokenType.On ||
               type == SqlTokenType.Set ||
               type == SqlTokenType.Values;
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
    /// Tokenizes SQL safely, returning empty list on error.
    /// </summary>
    private static IReadOnlyList<SqlToken> TokenizeSafe(string sql)
    {
        try
        {
            var lexer = new SqlLexer(sql);
            var result = lexer.Tokenize();
            return result.Tokens;
        }
        catch
        {
            return Array.Empty<SqlToken>();
        }
    }

    /// <summary>
    /// Finds the token index at or just before the cursor offset.
    /// </summary>
    private static int FindTokenAtOrBefore(IReadOnlyList<SqlToken> tokens, int cursorOffset)
    {
        var lastIndex = -1;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type == SqlTokenType.Eof)
                break;

            if (tokens[i].Position <= cursorOffset)
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
