using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Query.Planning.Rewrites;

/// <summary>
/// Rewrites EXISTS / NOT EXISTS correlated subquery conditions into JOINs
/// for server-side execution via FetchXML link-entity.
///
/// EXISTS (SELECT 1 FROM child WHERE child.parentid = parent.id)
///   → INNER JOIN child ON parent.id = child.parentid (+ DISTINCT)
///
/// NOT EXISTS (SELECT 1 FROM child WHERE child.parentid = parent.id)
///   → LEFT JOIN child ON parent.id = child.parentid
///     WHERE child.{primarykey} IS NULL
///
/// When the subquery is too complex or no correlated reference is found,
/// the rewrite is skipped and the condition passes through unchanged.
/// </summary>
public static class ExistsToJoinRewrite
{
    /// <summary>
    /// Attempts to rewrite all EXISTS/NOT EXISTS conditions in the statement as JOINs.
    /// Returns the rewritten statement, or the original if no EXISTS conditions exist
    /// or none can be rewritten.
    /// </summary>
    public static SqlSelectStatement TryRewrite(SqlSelectStatement statement)
    {
        if (statement.Where == null)
        {
            return statement;
        }

        var outerTableName = statement.From.GetEffectiveName();
        var (condition, joins, isNullConditions, didRewrite) =
            RewriteCondition(statement.Where, outerTableName, statement);

        if (!didRewrite)
        {
            return statement;
        }

        // Merge the IS NULL conditions (from NOT EXISTS) into the remaining WHERE
        ISqlCondition? finalWhere = MergeConditions(condition, isNullConditions);

        // Build new statement with JOIN(s) added
        var allJoins = new List<SqlJoin>(statement.Joins);
        allJoins.AddRange(joins);

        // DISTINCT is only needed for EXISTS (INNER JOIN) to prevent row multiplication.
        // NOT EXISTS (LEFT JOIN + IS NULL) doesn't multiply rows, so skip DISTINCT for pure NOT EXISTS.
        var hasExistsJoins = joins.Count > isNullConditions.Count;
        var needsDistinct = statement.Distinct || hasExistsJoins;

        var newStatement = new SqlSelectStatement(
            statement.Columns,
            statement.From,
            allJoins,
            finalWhere,
            statement.OrderBy,
            statement.Top,
            needsDistinct,
            statement.GroupBy,
            statement.Having,
            statement.SourcePosition,
            statement.GroupByExpressions);
        newStatement.LeadingComments.AddRange(statement.LeadingComments);

        return newStatement;
    }

    /// <summary>
    /// Recursively processes a condition tree, replacing EXISTS conditions
    /// with JOINs. Returns remaining conditions, joins to add, IS NULL
    /// conditions for NOT EXISTS, and whether any rewrite occurred.
    /// </summary>
    private static (ISqlCondition? condition, List<SqlJoin> joins,
        List<ISqlCondition> isNullConditions, bool didRewrite)
        RewriteCondition(ISqlCondition condition, string outerTableName,
            SqlSelectStatement outerStatement)
    {
        switch (condition)
        {
            case SqlExistsCondition exists:
                return RewriteExists(exists, outerTableName, outerStatement);

            case SqlLogicalCondition { Operator: SqlLogicalOperator.And } logical:
            {
                var allJoins = new List<SqlJoin>();
                var allIsNull = new List<ISqlCondition>();
                var remainingConditions = new List<ISqlCondition>();
                var anyRewrite = false;

                foreach (var child in logical.Conditions)
                {
                    var (childCond, childJoins, childIsNull, childDidRewrite) =
                        RewriteCondition(child, outerTableName, outerStatement);

                    if (childDidRewrite)
                    {
                        anyRewrite = true;
                    }

                    allJoins.AddRange(childJoins);
                    allIsNull.AddRange(childIsNull);
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

                return (remaining, allJoins, allIsNull, anyRewrite);
            }

            default:
                // Non-EXISTS condition: pass through unchanged
                return (condition, new List<SqlJoin>(), new List<ISqlCondition>(), false);
        }
    }

    /// <summary>
    /// Rewrites a single EXISTS or NOT EXISTS condition.
    /// </summary>
    private static (ISqlCondition? condition, List<SqlJoin> joins,
        List<ISqlCondition> isNullConditions, bool didRewrite)
        RewriteExists(SqlExistsCondition exists, string outerTableName,
            SqlSelectStatement outerStatement)
    {
        var subquery = exists.Subquery;

        // Only rewrite simple subqueries: single table, no joins, no aggregates, no GROUP BY
        if (!IsSimpleSubquery(subquery))
        {
            // Cannot rewrite, pass through unchanged
            return (exists, new List<SqlJoin>(), new List<ISqlCondition>(), false);
        }

        if (subquery.Where == null)
        {
            // No WHERE = no correlation = cannot rewrite to JOIN
            return (exists, new List<SqlJoin>(), new List<ISqlCondition>(), false);
        }

        // Find the correlated reference: a comparison in the subquery WHERE
        // where one side references the outer table and the other references
        // the subquery table.
        var subqueryTableName = subquery.From.GetEffectiveName();
        var correlation = FindCorrelation(subquery.Where, outerTableName, subqueryTableName);

        if (correlation == null)
        {
            // No correlated reference found, cannot rewrite
            return (exists, new List<SqlJoin>(), new List<ISqlCondition>(), false);
        }

        // Generate a unique alias for the subquery table
        var subqueryAlias = GenerateUniqueAlias(subquery.From.TableName, outerStatement);

        // Build the JOIN
        var joinTable = new SqlTableRef(subquery.From.TableName, subqueryAlias);
        var outerColumn = correlation.Value.outerColumn;
        var innerColumn = SqlColumnRef.Qualified(subqueryAlias, correlation.Value.innerColumnName);

        var joinType = exists.IsNegated ? SqlJoinType.Left : SqlJoinType.Inner;
        var join = new SqlJoin(joinType, joinTable, outerColumn, innerColumn);

        var joins = new List<SqlJoin> { join };
        var isNullConditions = new List<ISqlCondition>();

        // For NOT EXISTS: add IS NULL condition on the joined entity's column
        // (the inner join column works as a proxy for "no matching row")
        if (exists.IsNegated)
        {
            var isNullColumn = SqlColumnRef.Qualified(subqueryAlias, correlation.Value.innerColumnName);
            isNullConditions.Add(new SqlNullCondition(isNullColumn, false));
        }

        // Merge remaining subquery WHERE conditions (after removing the correlated
        // comparison) into the outer query, re-qualifying references.
        ISqlCondition? mergedCondition = null;
        var remainingSubqueryWhere = RemoveCorrelation(
            subquery.Where, correlation.Value.correlatedCondition);
        if (remainingSubqueryWhere != null)
        {
            mergedCondition = RequalifyCondition(
                remainingSubqueryWhere, subqueryTableName, subqueryAlias);
        }

        return (mergedCondition, joins, isNullConditions, true);
    }

    /// <summary>
    /// Represents a correlated reference found in a subquery WHERE clause.
    /// </summary>
    private readonly record struct CorrelationInfo(
        SqlColumnRef outerColumn,
        string innerColumnName,
        ISqlCondition correlatedCondition);

    /// <summary>
    /// Finds a correlated comparison in the condition tree: a comparison where
    /// one side references the outer table and the other references the inner table.
    /// Supports both SqlComparisonCondition (col = literal is not correlation) and
    /// SqlExpressionCondition (col = col).
    /// </summary>
    private static CorrelationInfo? FindCorrelation(
        ISqlCondition condition, string outerTableName, string innerTableName)
    {
        switch (condition)
        {
            case SqlExpressionCondition expr
                when expr.Operator == SqlComparisonOperator.Equal:
            {
                // Both sides must be column references
                if (expr.Left is SqlColumnExpression leftCol &&
                    expr.Right is SqlColumnExpression rightCol)
                {
                    return MatchCorrelation(leftCol.Column, rightCol.Column,
                        outerTableName, innerTableName, expr);
                }
                return null;
            }

            case SqlComparisonCondition comp
                when comp.Operator == SqlComparisonOperator.Equal:
            {
                // SqlComparisonCondition has Column and Value.
                // This won't be a correlation (Value is a literal), skip.
                return null;
            }

            case SqlLogicalCondition { Operator: SqlLogicalOperator.And } logical:
            {
                // Search each child for a correlation
                foreach (var child in logical.Conditions)
                {
                    var result = FindCorrelation(child, outerTableName, innerTableName);
                    if (result != null)
                    {
                        return result;
                    }
                }
                return null;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Tries to match two column references as a correlated pair (outer.col = inner.col).
    /// </summary>
    private static CorrelationInfo? MatchCorrelation(
        SqlColumnRef left, SqlColumnRef right,
        string outerTableName, string innerTableName,
        ISqlCondition correlatedCondition)
    {
        // Case 1: left is outer, right is inner
        if (IsOuterRef(left, outerTableName) && IsInnerRef(right, innerTableName))
        {
            return new CorrelationInfo(left, right.ColumnName, correlatedCondition);
        }

        // Case 2: left is inner, right is outer
        if (IsInnerRef(left, innerTableName) && IsOuterRef(right, outerTableName))
        {
            return new CorrelationInfo(right, left.ColumnName, correlatedCondition);
        }

        return null;
    }

    /// <summary>
    /// Checks if a column reference refers to the outer table.
    /// </summary>
    private static bool IsOuterRef(SqlColumnRef col, string outerTableName)
    {
        return col.TableName != null &&
               string.Equals(col.TableName, outerTableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if a column reference refers to the inner (subquery) table.
    /// A column with no table qualifier is assumed to reference the inner table.
    /// </summary>
    private static bool IsInnerRef(SqlColumnRef col, string innerTableName)
    {
        return col.TableName == null ||
               string.Equals(col.TableName, innerTableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Removes the correlated condition from a condition tree.
    /// Returns null if the entire tree was the correlation.
    /// </summary>
    private static ISqlCondition? RemoveCorrelation(
        ISqlCondition condition, ISqlCondition correlatedCondition)
    {
        if (ReferenceEquals(condition, correlatedCondition))
        {
            return null;
        }

        if (condition is SqlLogicalCondition { Operator: SqlLogicalOperator.And } logical)
        {
            var remaining = logical.Conditions
                .Where(c => !ReferenceEquals(c, correlatedCondition))
                .ToList();

            return remaining.Count switch
            {
                0 => null,
                1 => remaining[0],
                _ => new SqlLogicalCondition(SqlLogicalOperator.And, remaining)
            };
        }

        return condition;
    }

    /// <summary>
    /// Merges remaining WHERE conditions and IS NULL conditions into a single condition.
    /// </summary>
    private static ISqlCondition? MergeConditions(
        ISqlCondition? remaining, List<ISqlCondition> isNullConditions)
    {
        var all = new List<ISqlCondition>();
        if (remaining != null)
        {
            all.Add(remaining);
        }
        all.AddRange(isNullConditions);

        return all.Count switch
        {
            0 => null,
            1 => all[0],
            _ => new SqlLogicalCondition(SqlLogicalOperator.And, all)
        };
    }

    /// <summary>
    /// Checks whether a subquery is simple enough for JOIN rewrite:
    /// single table, no joins, no GROUP BY, no aggregates.
    /// </summary>
    private static bool IsSimpleSubquery(SqlSelectStatement subquery)
    {
        return subquery.Joins.Count == 0
            && subquery.GroupBy.Count == 0
            && subquery.Having == null
            && !subquery.HasAggregates();
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

        var baseAlias = tableName + "_exists";
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

            case SqlExpressionCondition exprCond:
                var requalifiedLeft = RequalifyExpression(exprCond.Left, originalName, newAlias);
                var requalifiedRight = RequalifyExpression(exprCond.Right, originalName, newAlias);
                return new SqlExpressionCondition(requalifiedLeft, exprCond.Operator, requalifiedRight);

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
    /// Re-qualifies column references in an expression to use a new table alias.
    /// </summary>
    private static ISqlExpression RequalifyExpression(
        ISqlExpression expression, string originalName, string newAlias)
    {
        if (expression is SqlColumnExpression colExpr)
        {
            var requalified = RequalifyColumn(colExpr.Column, originalName, newAlias);
            return new SqlColumnExpression(requalified);
        }
        return expression;
    }

    /// <summary>
    /// Re-qualifies a column reference to use a new table alias.
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
