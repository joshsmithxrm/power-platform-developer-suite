using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Rewrites;

/// <summary>
/// Rewrites IN (SELECT ...) subquery conditions into INNER JOINs for
/// server-side execution. When the subquery is too complex for a JOIN
/// rewrite, returns null to signal that the caller should fall back to
/// two-phase execution (execute subquery first, inject values as IN list).
/// </summary>
public static class InSubqueryToJoinRewrite
{
    /// <summary>
    /// Result of attempting an IN subquery rewrite.
    /// </summary>
    public sealed class RewriteResult
    {
        /// <summary>
        /// The rewritten statement with the IN subquery replaced by a JOIN.
        /// Null when the subquery is too complex for a JOIN rewrite.
        /// </summary>
        public SqlSelectStatement? RewrittenStatement { get; }

        /// <summary>
        /// The original IN subquery condition that could not be rewritten.
        /// Null when the rewrite succeeded.
        /// </summary>
        public SqlInSubqueryCondition? FallbackCondition { get; }

        /// <summary>Whether the rewrite produced a JOIN.</summary>
        public bool IsRewritten => RewrittenStatement != null;

        private RewriteResult(SqlSelectStatement? rewritten, SqlInSubqueryCondition? fallback)
        {
            RewrittenStatement = rewritten;
            FallbackCondition = fallback;
        }

        internal static RewriteResult Rewritten(SqlSelectStatement stmt) => new(stmt, null);
        internal static RewriteResult Fallback(SqlInSubqueryCondition cond) => new(null, cond);
    }

    /// <summary>
    /// Attempts to rewrite all IN subquery conditions in the statement as JOINs.
    /// Returns a <see cref="RewriteResult"/> with either the rewritten statement
    /// or the first condition that needs fallback execution.
    /// </summary>
    /// <param name="statement">The statement to rewrite.</param>
    /// <returns>
    /// A rewrite result. If <see cref="RewriteResult.IsRewritten"/> is true, the
    /// <see cref="RewriteResult.RewrittenStatement"/> contains the modified AST.
    /// Otherwise, <see cref="RewriteResult.FallbackCondition"/> contains the condition
    /// that needs two-phase execution.
    /// </returns>
    public static RewriteResult TryRewrite(SqlSelectStatement statement)
    {
        if (statement.Where == null)
        {
            return RewriteResult.Rewritten(statement);
        }

        var (condition, joins, fallback) = RewriteCondition(statement.Where, statement);
        if (fallback != null)
        {
            return RewriteResult.Fallback(fallback);
        }

        // Build new statement with JOIN added and condition updated
        var allJoins = new List<SqlJoin>(statement.Joins);
        allJoins.AddRange(joins);

        var newStatement = new SqlSelectStatement(
            statement.Columns,
            statement.From,
            allJoins,
            condition,
            statement.OrderBy,
            statement.Top,
            distinct: true, // DISTINCT to prevent row multiplication from JOIN
            statement.GroupBy,
            statement.Having,
            statement.SourcePosition,
            statement.GroupByExpressions);
        newStatement.LeadingComments.AddRange(statement.LeadingComments);

        return RewriteResult.Rewritten(newStatement);
    }

    /// <summary>
    /// Recursively processes a condition tree, replacing IN subquery conditions
    /// with equality conditions and collecting the corresponding JOINs.
    /// </summary>
    /// <returns>
    /// A tuple of (remaining condition or null, list of joins to add, fallback condition or null).
    /// </returns>
    private static (ISqlCondition? condition, List<SqlJoin> joins, SqlInSubqueryCondition? fallback)
        RewriteCondition(ISqlCondition condition, SqlSelectStatement outerStatement)
    {
        switch (condition)
        {
            case SqlInSubqueryCondition inSub:
                return RewriteInSubquery(inSub, outerStatement);

            case SqlLogicalCondition { Operator: SqlLogicalOperator.And } logical:
            {
                var allJoins = new List<SqlJoin>();
                var remainingConditions = new List<ISqlCondition>();

                foreach (var child in logical.Conditions)
                {
                    var (childCond, childJoins, childFallback) =
                        RewriteCondition(child, outerStatement);

                    if (childFallback != null)
                    {
                        return (null, new List<SqlJoin>(), childFallback);
                    }

                    allJoins.AddRange(childJoins);
                    if (childCond != null)
                    {
                        remainingConditions.Add(childCond);
                    }
                }

                ISqlCondition? remaining = remainingConditions.Count switch
                {
                    0 => null,
                    1 => remainingConditions[0],
                    _ => new SqlLogicalCondition(SqlLogicalOperator.And, remainingConditions)
                };

                return (remaining, allJoins, null);
            }

            default:
                // Non-IN-subquery condition: pass through unchanged
                return (condition, new List<SqlJoin>(), null);
        }
    }

    /// <summary>
    /// Attempts to rewrite a single IN subquery condition as a JOIN.
    /// </summary>
    private static (ISqlCondition? condition, List<SqlJoin> joins, SqlInSubqueryCondition? fallback)
        RewriteInSubquery(SqlInSubqueryCondition inSub, SqlSelectStatement outerStatement)
    {
        // Cannot rewrite NOT IN as INNER JOIN (would need LEFT JOIN + IS NULL pattern)
        if (inSub.IsNegated)
        {
            return (null, new List<SqlJoin>(), inSub);
        }

        var subquery = inSub.Subquery;

        // Only rewrite simple subqueries: single column, single table, no joins, no aggregates
        if (!IsSimpleSubquery(subquery))
        {
            return (null, new List<SqlJoin>(), inSub);
        }

        // Get the single column from the subquery
        var subqueryColumn = GetSingleColumnName(subquery);
        if (subqueryColumn == null)
        {
            return (null, new List<SqlJoin>(), inSub);
        }

        // Generate a unique alias for the subquery table to avoid collisions
        var subqueryTable = subquery.From.TableName;
        var subqueryAlias = GenerateUniqueAlias(subqueryTable, outerStatement);

        // Build the JOIN: INNER JOIN subquery_table AS alias ON outer.column = alias.subquery_column
        var joinTable = new SqlTableRef(subqueryTable, subqueryAlias);
        var outerColumn = inSub.Column;
        var joinRightColumn = SqlColumnRef.Qualified(subqueryAlias, subqueryColumn);
        var join = new SqlJoin(SqlJoinType.Inner, joinTable, outerColumn, joinRightColumn);

        var joins = new List<SqlJoin> { join };

        // Merge subquery WHERE conditions into the outer query, re-qualifying
        // column references to use the new alias.
        ISqlCondition? mergedCondition = null;
        if (subquery.Where != null)
        {
            mergedCondition = RequalifyCondition(subquery.Where, subquery.From.GetEffectiveName(), subqueryAlias);
        }

        return (mergedCondition, joins, null);
    }

    /// <summary>
    /// Checks whether a subquery is simple enough for JOIN rewrite:
    /// single column, single table, no joins, no GROUP BY, no aggregates.
    /// </summary>
    private static bool IsSimpleSubquery(SqlSelectStatement subquery)
    {
        return subquery.Columns.Count == 1
            && subquery.Joins.Count == 0
            && subquery.GroupBy.Count == 0
            && subquery.Having == null
            && !subquery.HasAggregates();
    }

    /// <summary>
    /// Gets the single column name from a subquery's SELECT list.
    /// Returns null if the column is not a simple column reference.
    /// </summary>
    private static string? GetSingleColumnName(SqlSelectStatement subquery)
    {
        if (subquery.Columns[0] is SqlColumnRef { IsWildcard: false } colRef)
        {
            return colRef.ColumnName;
        }
        return null;
    }

    /// <summary>
    /// Generates a unique alias for a subquery table that doesn't conflict
    /// with existing table names/aliases in the outer query.
    /// </summary>
    private static string GenerateUniqueAlias(string tableName, SqlSelectStatement outerStatement)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        existing.Add(outerStatement.From.GetEffectiveName());
        foreach (var join in outerStatement.Joins)
        {
            existing.Add(join.Table.GetEffectiveName());
        }

        // Try _sub0, _sub1, etc.
        var baseAlias = tableName + "_sub";
        for (var i = 0; ; i++)
        {
            var candidate = baseAlias + i;
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Re-qualifies column references in a condition tree to use a new table alias.
    /// Used when merging subquery WHERE conditions into the outer query.
    /// </summary>
    private static ISqlCondition RequalifyCondition(
        ISqlCondition condition, string originalName, string newAlias)
    {
        switch (condition)
        {
            case SqlComparisonCondition comp:
                var requalifiedCol = RequalifyColumn(comp.Column, originalName, newAlias);
                return new SqlComparisonCondition(requalifiedCol, comp.Operator, comp.Value);

            case SqlLikeCondition like:
                var requalifiedLikeCol = RequalifyColumn(like.Column, originalName, newAlias);
                return new SqlLikeCondition(requalifiedLikeCol, like.Pattern, like.IsNegated);

            case SqlNullCondition nullCond:
                var requalifiedNullCol = RequalifyColumn(nullCond.Column, originalName, newAlias);
                return new SqlNullCondition(requalifiedNullCol, nullCond.IsNegated);

            case SqlInCondition inCond:
                var requalifiedInCol = RequalifyColumn(inCond.Column, originalName, newAlias);
                return new SqlInCondition(requalifiedInCol, inCond.Values, inCond.IsNegated);

            case SqlLogicalCondition logical:
                var requalifiedChildren = logical.Conditions
                    .Select(c => RequalifyCondition(c, originalName, newAlias))
                    .ToList();
                return new SqlLogicalCondition(logical.Operator, requalifiedChildren);

            default:
                return condition;
        }
    }

    /// <summary>
    /// Re-qualifies a column reference to use a new table alias.
    /// Columns with no table qualifier or matching the original name get the new alias.
    /// </summary>
    private static SqlColumnRef RequalifyColumn(
        SqlColumnRef column, string originalName, string newAlias)
    {
        if (column.TableName == null ||
            string.Equals(column.TableName, originalName, StringComparison.OrdinalIgnoreCase))
        {
            return SqlColumnRef.Qualified(newAlias, column.ColumnName, column.Alias);
        }
        return column;
    }
}
