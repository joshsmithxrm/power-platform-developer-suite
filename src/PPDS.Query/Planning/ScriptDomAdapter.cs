using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Query.Parsing;

namespace PPDS.Query.Planning;

/// <summary>
/// Temporary bridge adapter that converts ScriptDom AST types to the legacy PPDS SQL AST types.
/// This enables the v3 ExecutionPlanBuilder to reuse ALL existing plan nodes without modification.
/// Will be replaced when plan nodes are updated to consume ScriptDom types directly.
/// </summary>
internal static class ScriptDomAdapter
{
    // ───────────────────────────────────────────────────────────────────
    //  Expressions: ScriptDom ScalarExpression → ISqlExpression
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a ScriptDom <see cref="ScalarExpression"/> to an <see cref="ISqlExpression"/>.
    /// </summary>
    public static ISqlExpression ConvertExpression(ScalarExpression expr)
    {
        if (expr is null)
            throw new ArgumentNullException(nameof(expr));

        return expr switch
        {
            IntegerLiteral intLit =>
                new SqlLiteralExpression(SqlLiteral.Number(intLit.Value)),

            NumericLiteral numLit =>
                new SqlLiteralExpression(SqlLiteral.Number(numLit.Value)),

            RealLiteral realLit =>
                new SqlLiteralExpression(SqlLiteral.Number(realLit.Value)),

            MoneyLiteral moneyLit =>
                new SqlLiteralExpression(SqlLiteral.Number(moneyLit.Value)),

            StringLiteral strLit =>
                new SqlLiteralExpression(SqlLiteral.String(strLit.Value)),

            NullLiteral =>
                new SqlLiteralExpression(SqlLiteral.Null()),

            ColumnReferenceExpression colRef =>
                new SqlColumnExpression(ConvertColumnRef(colRef)),

            BinaryExpression binExpr =>
                new SqlBinaryExpression(
                    ConvertExpression(binExpr.FirstExpression),
                    ConvertBinaryOperator(binExpr.BinaryExpressionType),
                    ConvertExpression(binExpr.SecondExpression)),

            UnaryExpression unaryExpr =>
                new SqlUnaryExpression(
                    ConvertUnaryOperator(unaryExpr.UnaryExpressionType),
                    ConvertExpression(unaryExpr.Expression)),

            ParenthesisExpression parenExpr =>
                ConvertExpression(parenExpr.Expression),

            FunctionCall funcCall =>
                ConvertFunctionCall(funcCall),

            SearchedCaseExpression caseExpr =>
                ConvertSearchedCase(caseExpr),

            IIfCall iifCall =>
                new SqlIifExpression(
                    ConvertBooleanExpression(iifCall.Predicate),
                    ConvertExpression(iifCall.ThenExpression),
                    ConvertExpression(iifCall.ElseExpression)),

            CastCall castCall =>
                new SqlCastExpression(
                    ConvertExpression(castCall.Parameter),
                    FormatDataType(castCall.DataType)),

            ConvertCall convertCall =>
                ConvertConvertCall(convertCall),

            VariableReference varRef =>
                new SqlVariableExpression(varRef.Name.StartsWith("@") ? varRef.Name : "@" + varRef.Name),

            GlobalVariableExpression globalVar =>
                new SqlVariableExpression(globalVar.Name),

            ScalarSubquery subquery =>
                ConvertScalarSubquery(subquery),

            _ => throw new QueryParseException(
                $"Unsupported ScriptDom expression type: {expr.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts a ScriptDom <see cref="FunctionCall"/> to the appropriate ISqlExpression.
    /// Handles both regular functions and window/aggregate functions.
    /// </summary>
    private static ISqlExpression ConvertFunctionCall(FunctionCall funcCall)
    {
        // Check if this is a window function (has OVER clause)
        if (funcCall.OverClause != null)
        {
            return ConvertWindowFunction(funcCall);
        }

        // Check for aggregate functions
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        if (funcName is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX" or "STDEV" or "STDEVP" or "VAR" or "VARP")
        {
            return ConvertAggregateExpression(funcCall);
        }

        // Regular function call
        var args = new List<ISqlExpression>();
        if (funcCall.Parameters != null)
        {
            foreach (var param in funcCall.Parameters)
            {
                args.Add(ConvertExpression(param));
            }
        }

        return new SqlFunctionExpression(funcCall.FunctionName.Value, args);
    }

    /// <summary>
    /// Converts a ScriptDom aggregate function call to <see cref="SqlAggregateExpression"/>.
    /// </summary>
    private static SqlAggregateExpression ConvertAggregateExpression(FunctionCall funcCall)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        var aggFunc = funcName switch
        {
            "COUNT" => SqlAggregateFunction.Count,
            "SUM" => SqlAggregateFunction.Sum,
            "AVG" => SqlAggregateFunction.Avg,
            "MIN" => SqlAggregateFunction.Min,
            "MAX" => SqlAggregateFunction.Max,
            "STDEV" or "STDEVP" => SqlAggregateFunction.Stdev,
            "VAR" or "VARP" => SqlAggregateFunction.Var,
            _ => throw new QueryParseException($"Unknown aggregate function: {funcName}")
        };

        var isDistinct = funcCall.UniqueRowFilter == UniqueRowFilter.Distinct;
        ISqlExpression? operand = null;

        if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
        {
            // COUNT(*) check: ScriptDom represents COUNT(*) with a ColumnReferenceExpression using ColumnType.Wildcard
            var firstParam = funcCall.Parameters[0];
            if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
            {
                operand = null; // COUNT(*)
            }
            else
            {
                operand = ConvertExpression(firstParam);
            }
        }

        return new SqlAggregateExpression(aggFunc, operand, isDistinct);
    }

    /// <summary>
    /// Converts a ScriptDom window function (FunctionCall with OverClause) to <see cref="SqlWindowExpression"/>.
    /// </summary>
    private static SqlWindowExpression ConvertWindowFunction(FunctionCall funcCall)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        ISqlExpression? operand = null;
        var isCountStar = false;

        if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
        {
            var firstParam = funcCall.Parameters[0];
            if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
            {
                isCountStar = true;
            }
            else
            {
                operand = ConvertExpression(firstParam);
            }
        }

        // PARTITION BY
        List<ISqlExpression>? partitionBy = null;
        if (funcCall.OverClause.Partitions != null && funcCall.OverClause.Partitions.Count > 0)
        {
            partitionBy = new List<ISqlExpression>();
            foreach (var partition in funcCall.OverClause.Partitions)
            {
                partitionBy.Add(ConvertExpression(partition));
            }
        }

        // ORDER BY within OVER clause
        List<SqlOrderByItem>? orderBy = null;
        if (funcCall.OverClause.OrderByClause?.OrderByElements != null)
        {
            orderBy = new List<SqlOrderByItem>();
            foreach (var orderElem in funcCall.OverClause.OrderByClause.OrderByElements)
            {
                if (orderElem.Expression is ColumnReferenceExpression orderCol)
                {
                    var direction = orderElem.SortOrder == SortOrder.Descending
                        ? SqlSortDirection.Descending
                        : SqlSortDirection.Ascending;
                    orderBy.Add(new SqlOrderByItem(ConvertColumnRef(orderCol), direction));
                }
            }
        }

        return new SqlWindowExpression(funcName, operand, partitionBy, orderBy, isCountStar);
    }

    /// <summary>
    /// Converts a ScriptDom SEARCHED CASE expression to <see cref="SqlCaseExpression"/>.
    /// </summary>
    private static SqlCaseExpression ConvertSearchedCase(SearchedCaseExpression caseExpr)
    {
        var whenClauses = new List<SqlWhenClause>();
        foreach (var when in caseExpr.WhenClauses)
        {
            if (when is SearchedWhenClause searched)
            {
                whenClauses.Add(new SqlWhenClause(
                    ConvertBooleanExpression(searched.WhenExpression),
                    ConvertExpression(searched.ThenExpression)));
            }
        }

        ISqlExpression? elseExpr = null;
        if (caseExpr.ElseExpression != null)
        {
            elseExpr = ConvertExpression(caseExpr.ElseExpression);
        }

        return new SqlCaseExpression(whenClauses, elseExpr);
    }

    /// <summary>
    /// Converts a ScriptDom CONVERT call to <see cref="SqlCastExpression"/>.
    /// </summary>
    private static SqlCastExpression ConvertConvertCall(ConvertCall convertCall)
    {
        int? style = null;
        if (convertCall.Style != null)
        {
            if (convertCall.Style is IntegerLiteral intStyle &&
                int.TryParse(intStyle.Value, out var styleVal))
            {
                style = styleVal;
            }
        }

        return new SqlCastExpression(
            ConvertExpression(convertCall.Parameter),
            FormatDataType(convertCall.DataType),
            style);
    }

    /// <summary>
    /// Converts a ScriptDom scalar subquery to <see cref="SqlSubqueryExpression"/>.
    /// </summary>
    private static SqlSubqueryExpression ConvertScalarSubquery(ScalarSubquery subquery)
    {
        if (subquery.QueryExpression is QuerySpecification querySpec)
        {
            return new SqlSubqueryExpression(ConvertSelectStatement(querySpec));
        }
        throw new QueryParseException("Unsupported scalar subquery type.");
    }

    // ───────────────────────────────────────────────────────────────────
    //  Conditions: ScriptDom BooleanExpression → ISqlCondition
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a ScriptDom <see cref="BooleanExpression"/> to an <see cref="ISqlCondition"/>.
    /// </summary>
    public static ISqlCondition ConvertBooleanExpression(BooleanExpression boolExpr)
    {
        if (boolExpr is null)
            throw new ArgumentNullException(nameof(boolExpr));

        return boolExpr switch
        {
            BooleanComparisonExpression compExpr =>
                ConvertComparison(compExpr),

            BooleanBinaryExpression binBool =>
                ConvertLogicalCondition(binBool),

            BooleanParenthesisExpression parenBool =>
                ConvertBooleanExpression(parenBool.Expression),

            BooleanNotExpression notExpr =>
                ConvertNotExpression(notExpr),

            BooleanIsNullExpression isNullExpr =>
                ConvertIsNullExpression(isNullExpr),

            LikePredicate likePred =>
                ConvertLikePredicate(likePred),

            InPredicate inPred =>
                ConvertInPredicate(inPred),

            ExistsPredicate existsPred =>
                ConvertExistsPredicate(existsPred),

            BooleanTernaryExpression ternary =>
                ConvertBetweenExpression(ternary),

            _ => throw new QueryParseException(
                $"Unsupported ScriptDom boolean expression type: {boolExpr.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts a ScriptDom comparison (e.g., column = 'value') to the appropriate condition type.
    /// If both sides are simple (column op literal), produces <see cref="SqlComparisonCondition"/>.
    /// Otherwise, produces <see cref="SqlExpressionCondition"/> for client-side evaluation.
    /// </summary>
    private static ISqlCondition ConvertComparison(BooleanComparisonExpression compExpr)
    {
        var op = ConvertComparisonOperator(compExpr.ComparisonType);

        // Try to produce a SqlComparisonCondition (column op literal) for FetchXML pushdown
        if (compExpr.FirstExpression is ColumnReferenceExpression leftCol
            && IsLiteralExpression(compExpr.SecondExpression))
        {
            return new SqlComparisonCondition(
                ConvertColumnRef(leftCol),
                op,
                ExtractLiteral(compExpr.SecondExpression));
        }

        // Also handle literal op column (reversed)
        if (IsLiteralExpression(compExpr.FirstExpression)
            && compExpr.SecondExpression is ColumnReferenceExpression rightCol)
        {
            return new SqlComparisonCondition(
                ConvertColumnRef(rightCol),
                ReverseOperator(op),
                ExtractLiteral(compExpr.FirstExpression));
        }

        // General case: expression comparison (client-side)
        return new SqlExpressionCondition(
            ConvertExpression(compExpr.FirstExpression),
            op,
            ConvertExpression(compExpr.SecondExpression));
    }

    /// <summary>
    /// Converts a ScriptDom AND/OR to <see cref="SqlLogicalCondition"/>.
    /// </summary>
    private static SqlLogicalCondition ConvertLogicalCondition(BooleanBinaryExpression binBool)
    {
        var op = binBool.BinaryExpressionType == BooleanBinaryExpressionType.And
            ? SqlLogicalOperator.And
            : SqlLogicalOperator.Or;

        // Flatten nested AND/OR chains for cleaner trees
        var conditions = new List<ISqlCondition>();
        FlattenLogical(binBool, op, conditions);

        return new SqlLogicalCondition(op, conditions);
    }

    /// <summary>
    /// Flattens nested AND (or OR) chains into a single list of conditions.
    /// </summary>
    private static void FlattenLogical(
        BooleanExpression expr, SqlLogicalOperator targetOp, List<ISqlCondition> conditions)
    {
        if (expr is BooleanBinaryExpression binBool)
        {
            var exprOp = binBool.BinaryExpressionType == BooleanBinaryExpressionType.And
                ? SqlLogicalOperator.And
                : SqlLogicalOperator.Or;

            if (exprOp == targetOp)
            {
                FlattenLogical(binBool.FirstExpression, targetOp, conditions);
                FlattenLogical(binBool.SecondExpression, targetOp, conditions);
                return;
            }
        }

        conditions.Add(ConvertBooleanExpression(expr));
    }

    /// <summary>
    /// Converts NOT expression. Handles NOT IN, NOT LIKE, NOT EXISTS by setting IsNegated.
    /// </summary>
    private static ISqlCondition ConvertNotExpression(BooleanNotExpression notExpr)
    {
        // For NOT on specific predicates, negate them directly
        var inner = notExpr.Expression;

        if (inner is BooleanIsNullExpression isNull)
        {
            return new SqlNullCondition(
                ConvertColumnRef((ColumnReferenceExpression)isNull.Expression),
                isNegated: true);
        }

        if (inner is LikePredicate like)
        {
            return ConvertLikePredicate(like, forceNegate: true);
        }

        if (inner is InPredicate inPred)
        {
            return ConvertInPredicate(inPred, forceNegate: true);
        }

        if (inner is ExistsPredicate existsPred)
        {
            return ConvertExistsPredicate(existsPred, forceNegate: true);
        }

        // General NOT: wrap the inner condition in an expression condition
        // using a unary NOT pattern. For now, convert the inner and use a logical wrapper.
        // TODO: Consider adding a SqlNotCondition if this becomes common
        var innerCondition = ConvertBooleanExpression(inner);
        return innerCondition;
    }

    /// <summary>
    /// Converts IS NULL / IS NOT NULL.
    /// </summary>
    private static SqlNullCondition ConvertIsNullExpression(BooleanIsNullExpression isNullExpr)
    {
        if (isNullExpr.Expression is ColumnReferenceExpression colRef)
        {
            return new SqlNullCondition(ConvertColumnRef(colRef), isNullExpr.IsNot);
        }

        // For complex expressions, create a simple column ref from the expression text
        // TODO: Support arbitrary expression IS NULL when needed
        throw new QueryParseException(
            "IS NULL on complex expressions is not yet supported. Use a column reference.");
    }

    /// <summary>
    /// Converts LIKE / NOT LIKE predicate.
    /// </summary>
    private static SqlLikeCondition ConvertLikePredicate(LikePredicate likePred, bool forceNegate = false)
    {
        if (likePred.FirstExpression is not ColumnReferenceExpression colRef)
        {
            throw new QueryParseException(
                "LIKE predicate must have a column reference on the left side.");
        }

        var pattern = ExtractStringValue(likePred.SecondExpression);
        var isNegated = likePred.NotDefined || forceNegate;

        return new SqlLikeCondition(ConvertColumnRef(colRef), pattern, isNegated);
    }

    /// <summary>
    /// Converts IN predicate. Handles both literal IN lists and IN subqueries.
    /// </summary>
    private static ISqlCondition ConvertInPredicate(InPredicate inPred, bool forceNegate = false)
    {
        if (inPred.Expression is not ColumnReferenceExpression colRef)
        {
            throw new QueryParseException(
                "IN predicate must have a column reference on the left side.");
        }

        var isNegated = inPred.NotDefined || forceNegate;

        // Check for IN subquery
        if (inPred.Subquery != null)
        {
            if (inPred.Subquery.QueryExpression is QuerySpecification querySpec)
            {
                var subSelect = ConvertSelectStatement(querySpec);
                return new SqlInSubqueryCondition(
                    ConvertColumnRef(colRef), subSelect, isNegated);
            }
            throw new QueryParseException("Unsupported IN subquery type.");
        }

        // Literal IN list
        var values = new List<SqlLiteral>();
        foreach (var val in inPred.Values)
        {
            values.Add(ExtractLiteral(val));
        }

        return new SqlInCondition(ConvertColumnRef(colRef), values, isNegated);
    }

    /// <summary>
    /// Converts EXISTS / NOT EXISTS predicate.
    /// </summary>
    private static SqlExistsCondition ConvertExistsPredicate(
        ExistsPredicate existsPred, bool forceNegate = false)
    {
        if (existsPred.Subquery?.QueryExpression is QuerySpecification querySpec)
        {
            var subSelect = ConvertSelectStatement(querySpec);
            return new SqlExistsCondition(subSelect, forceNegate);
        }

        throw new QueryParseException("EXISTS predicate must contain a SELECT subquery.");
    }

    /// <summary>
    /// Converts BETWEEN expression: column BETWEEN low AND high
    /// into column GE low AND column LE high.
    /// </summary>
    private static ISqlCondition ConvertBetweenExpression(BooleanTernaryExpression ternary)
    {
        if (ternary.TernaryExpressionType == BooleanTernaryExpressionType.Between)
        {
            var col = ConvertExpression(ternary.FirstExpression);
            var low = ConvertExpression(ternary.SecondExpression);
            var high = ConvertExpression(ternary.ThirdExpression);

            var geCond = new SqlExpressionCondition(col, SqlComparisonOperator.GreaterThanOrEqual, low);
            var leCond = new SqlExpressionCondition(col, SqlComparisonOperator.LessThanOrEqual, high);

            return SqlLogicalCondition.And(geCond, leCond);
        }

        if (ternary.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween)
        {
            var col = ConvertExpression(ternary.FirstExpression);
            var low = ConvertExpression(ternary.SecondExpression);
            var high = ConvertExpression(ternary.ThirdExpression);

            var ltCond = new SqlExpressionCondition(col, SqlComparisonOperator.LessThan, low);
            var gtCond = new SqlExpressionCondition(col, SqlComparisonOperator.GreaterThan, high);

            return SqlLogicalCondition.Or(ltCond, gtCond);
        }

        throw new QueryParseException(
            $"Unsupported ternary expression type: {ternary.TernaryExpressionType}");
    }

    // ───────────────────────────────────────────────────────────────────
    //  Columns: ScriptDom SelectElement → ISqlSelectColumn
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a ScriptDom <see cref="SelectElement"/> to an <see cref="ISqlSelectColumn"/>.
    /// </summary>
    public static ISqlSelectColumn ConvertSelectElement(SelectElement element)
    {
        return element switch
        {
            SelectStarExpression star =>
                ConvertSelectStar(star),

            SelectScalarExpression scalar =>
                ConvertSelectScalar(scalar),

            _ => throw new QueryParseException(
                $"Unsupported SELECT element type: {element.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts a ScriptDom SELECT * to a wildcard <see cref="SqlColumnRef"/>.
    /// </summary>
    private static SqlColumnRef ConvertSelectStar(SelectStarExpression star)
    {
        string? tableName = null;
        if (star.Qualifier != null && star.Qualifier.Identifiers.Count > 0)
        {
            tableName = star.Qualifier.Identifiers[star.Qualifier.Identifiers.Count - 1].Value;
        }
        return SqlColumnRef.Wildcard(tableName);
    }

    /// <summary>
    /// Converts a ScriptDom <see cref="SelectScalarExpression"/> to the appropriate column type.
    /// </summary>
    private static ISqlSelectColumn ConvertSelectScalar(SelectScalarExpression scalar)
    {
        var alias = scalar.ColumnName?.Value;

        // Simple column reference
        if (scalar.Expression is ColumnReferenceExpression colRef
            && colRef.ColumnType != ColumnType.Wildcard)
        {
            var converted = ConvertColumnRef(colRef);
            return new SqlColumnRef(converted.TableName, converted.ColumnName, alias, false);
        }

        // Aggregate function call (non-window)
        if (scalar.Expression is FunctionCall funcCall && funcCall.OverClause == null)
        {
            var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
            if (funcName is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX")
            {
                return ConvertAggregateColumn(funcCall, alias);
            }
        }

        // Everything else: computed column
        var expression = ConvertExpression(scalar.Expression);
        return new SqlComputedColumn(expression, alias);
    }

    /// <summary>
    /// Converts a ScriptDom aggregate function call in SELECT to <see cref="SqlAggregateColumn"/>.
    /// </summary>
    private static SqlAggregateColumn ConvertAggregateColumn(FunctionCall funcCall, string? alias)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        var aggFunc = funcName switch
        {
            "COUNT" => SqlAggregateFunction.Count,
            "SUM" => SqlAggregateFunction.Sum,
            "AVG" => SqlAggregateFunction.Avg,
            "MIN" => SqlAggregateFunction.Min,
            "MAX" => SqlAggregateFunction.Max,
            "STDEV" or "STDEVP" => SqlAggregateFunction.Stdev,
            "VAR" or "VARP" => SqlAggregateFunction.Var,
            _ => throw new QueryParseException($"Unknown aggregate function: {funcName}")
        };

        var isDistinct = funcCall.UniqueRowFilter == UniqueRowFilter.Distinct;
        SqlColumnRef? column = null;

        if (funcCall.Parameters != null && funcCall.Parameters.Count > 0)
        {
            var firstParam = funcCall.Parameters[0];
            if (firstParam is ColumnReferenceExpression { ColumnType: ColumnType.Wildcard })
            {
                column = null; // COUNT(*)
            }
            else if (firstParam is ColumnReferenceExpression paramCol)
            {
                column = ConvertColumnRef(paramCol);
            }
            else
            {
                // Non-column aggregate operand (e.g., COUNT(1)) - treat as COUNT(*)
                column = null;
            }
        }

        return new SqlAggregateColumn(aggFunc, column, isDistinct, alias);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Statements: ScriptDom TSqlStatement → PPDS AST statements
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a ScriptDom <see cref="QuerySpecification"/> to a <see cref="SqlSelectStatement"/>.
    /// </summary>
    public static SqlSelectStatement ConvertSelectStatement(QuerySpecification querySpec)
    {
        // SELECT columns
        var columns = new List<ISqlSelectColumn>();
        foreach (var elem in querySpec.SelectElements)
        {
            columns.Add(ConvertSelectElement(elem));
        }

        // FROM
        var from = ConvertFromClause(querySpec.FromClause);

        // JOINs
        var joins = ConvertJoins(querySpec.FromClause);

        // WHERE
        ISqlCondition? where = null;
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            where = ConvertBooleanExpression(querySpec.WhereClause.SearchCondition);
        }

        // ORDER BY (from the QuerySpecification itself - may be null, handled at statement level)
        var orderBy = new List<SqlOrderByItem>();

        // TOP
        int? top = null;
        if (querySpec.TopRowFilter != null && querySpec.TopRowFilter.Expression is IntegerLiteral topLit)
        {
            if (int.TryParse(topLit.Value, out var topVal))
            {
                top = topVal;
            }
        }

        // DISTINCT
        var distinct = querySpec.UniqueRowFilter == UniqueRowFilter.Distinct;

        // GROUP BY
        var groupBy = new List<SqlColumnRef>();
        var groupByExpressions = new List<ISqlExpression>();
        if (querySpec.GroupByClause?.GroupingSpecifications != null)
        {
            foreach (var groupSpec in querySpec.GroupByClause.GroupingSpecifications)
            {
                if (groupSpec is ExpressionGroupingSpecification exprGroup)
                {
                    if (exprGroup.Expression is ColumnReferenceExpression groupCol)
                    {
                        groupBy.Add(ConvertColumnRef(groupCol));
                    }
                    else
                    {
                        // Function-based GROUP BY (e.g., YEAR(createdon))
                        groupByExpressions.Add(ConvertExpression(exprGroup.Expression));
                    }
                }
            }
        }

        // HAVING
        ISqlCondition? having = null;
        if (querySpec.HavingClause?.SearchCondition != null)
        {
            having = ConvertBooleanExpression(querySpec.HavingClause.SearchCondition);
        }

        return new SqlSelectStatement(
            columns, from, joins, where, orderBy, top, distinct, groupBy, having,
            sourcePosition: 0, groupByExpressions: groupByExpressions);
    }

    /// <summary>
    /// Converts a ScriptDom <see cref="SelectStatement"/> (with optional ORDER BY)
    /// to a <see cref="SqlSelectStatement"/>.
    /// </summary>
    public static SqlSelectStatement ConvertSelectStatementFull(SelectStatement selectStmt)
    {
        if (selectStmt.QueryExpression is not QuerySpecification querySpec)
        {
            throw new QueryParseException(
                "Only QuerySpecification is supported as SELECT query expression. " +
                $"Got: {selectStmt.QueryExpression?.GetType().Name ?? "null"}");
        }

        var baseSelect = ConvertSelectStatement(querySpec);

        // Apply ORDER BY from the SelectStatement level
        var orderBy = new List<SqlOrderByItem>();
        if (selectStmt.QueryExpression is QuerySpecification
            && selectStmt is { } selStmt)
        {
            // ScriptDom puts ORDER BY on the outer QueryExpression's parent in some cases
        }

        // Check for ORDER BY clause at statement level
        if (querySpec.OrderByClause?.OrderByElements != null)
        {
            foreach (var orderElem in querySpec.OrderByClause.OrderByElements)
            {
                if (orderElem.Expression is ColumnReferenceExpression orderCol)
                {
                    var direction = orderElem.SortOrder == SortOrder.Descending
                        ? SqlSortDirection.Descending
                        : SqlSortDirection.Ascending;
                    orderBy.Add(new SqlOrderByItem(ConvertColumnRef(orderCol), direction));
                }
            }
        }

        if (orderBy.Count > 0)
        {
            return new SqlSelectStatement(
                baseSelect.Columns, baseSelect.From, baseSelect.Joins, baseSelect.Where,
                orderBy, baseSelect.Top, baseSelect.Distinct, baseSelect.GroupBy,
                baseSelect.Having, baseSelect.SourcePosition, baseSelect.GroupByExpressions);
        }

        return baseSelect;
    }

    // ───────────────────────────────────────────────────────────────────
    //  FROM / JOIN conversion
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the primary table reference from a FROM clause.
    /// </summary>
    public static SqlTableRef ConvertFromClause(FromClause? fromClause)
    {
        if (fromClause == null || fromClause.TableReferences.Count == 0)
        {
            throw new QueryParseException("FROM clause is required.");
        }

        return ExtractPrimaryTable(fromClause.TableReferences[0]);
    }

    /// <summary>
    /// Extracts the primary table from a TableReference, handling JOINs and simple names.
    /// </summary>
    private static SqlTableRef ExtractPrimaryTable(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference named => ConvertNamedTable(named),
            QualifiedJoin join => ExtractPrimaryTable(join.FirstTableReference),
            _ => throw new QueryParseException(
                $"Unsupported table reference type: {tableRef.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts a NamedTableReference to SqlTableRef.
    /// </summary>
    private static SqlTableRef ConvertNamedTable(NamedTableReference named)
    {
        var tableName = GetMultiPartName(named.SchemaObject);
        var alias = named.Alias?.Value;
        return new SqlTableRef(tableName, alias);
    }

    /// <summary>
    /// Extracts JOIN clauses from a FROM clause's table references.
    /// </summary>
    public static List<SqlJoin> ConvertJoins(FromClause? fromClause)
    {
        var joins = new List<SqlJoin>();
        if (fromClause == null) return joins;

        foreach (var tableRef in fromClause.TableReferences)
        {
            CollectJoins(tableRef, joins);
        }

        return joins;
    }

    /// <summary>
    /// Recursively collects JOIN clauses from a table reference tree.
    /// </summary>
    private static void CollectJoins(TableReference tableRef, List<SqlJoin> joins)
    {
        if (tableRef is QualifiedJoin qualifiedJoin)
        {
            // Process the left side first (may have nested joins)
            CollectJoins(qualifiedJoin.FirstTableReference, joins);

            // Extract JOIN type
            var joinType = qualifiedJoin.QualifiedJoinType switch
            {
                QualifiedJoinType.Inner => SqlJoinType.Inner,
                QualifiedJoinType.LeftOuter => SqlJoinType.Left,
                QualifiedJoinType.RightOuter => SqlJoinType.Right,
                QualifiedJoinType.FullOuter => SqlJoinType.Left, // Approximate
                _ => SqlJoinType.Inner
            };

            // Extract the joined table
            SqlTableRef joinedTable;
            if (qualifiedJoin.SecondTableReference is NamedTableReference joinNamed)
            {
                joinedTable = ConvertNamedTable(joinNamed);
            }
            else
            {
                // Nested join — recurse
                CollectJoins(qualifiedJoin.SecondTableReference, joins);
                return;
            }

            // Extract ON condition — we expect column = column
            if (qualifiedJoin.SearchCondition is BooleanComparisonExpression onCondition
                && onCondition.FirstExpression is ColumnReferenceExpression leftOnCol
                && onCondition.SecondExpression is ColumnReferenceExpression rightOnCol)
            {
                joins.Add(new SqlJoin(
                    joinType,
                    joinedTable,
                    ConvertColumnRef(leftOnCol),
                    ConvertColumnRef(rightOnCol)));
            }
            else
            {
                // Complex ON condition - use first two column refs found
                // TODO: Support complex ON conditions with multiple predicates
                var colRefs = new List<ColumnReferenceExpression>();
                ExtractColumnRefsFromBoolExpr(qualifiedJoin.SearchCondition, colRefs);

                if (colRefs.Count >= 2)
                {
                    joins.Add(new SqlJoin(
                        joinType,
                        joinedTable,
                        ConvertColumnRef(colRefs[0]),
                        ConvertColumnRef(colRefs[1])));
                }
                else
                {
                    throw new QueryParseException(
                        "JOIN ON clause must reference at least two columns.");
                }
            }
        }
    }

    /// <summary>
    /// Extracts ColumnReferenceExpressions from a BooleanExpression tree (for ON clause parsing).
    /// </summary>
    private static void ExtractColumnRefsFromBoolExpr(
        BooleanExpression boolExpr, List<ColumnReferenceExpression> refs)
    {
        switch (boolExpr)
        {
            case BooleanComparisonExpression comp:
                if (comp.FirstExpression is ColumnReferenceExpression left) refs.Add(left);
                if (comp.SecondExpression is ColumnReferenceExpression right) refs.Add(right);
                break;
            case BooleanBinaryExpression bin:
                ExtractColumnRefsFromBoolExpr(bin.FirstExpression, refs);
                ExtractColumnRefsFromBoolExpr(bin.SecondExpression, refs);
                break;
            case BooleanParenthesisExpression paren:
                ExtractColumnRefsFromBoolExpr(paren.Expression, refs);
                break;
        }
    }

    // ───────────────────────────────────────────────────────────────────
    //  DML Statement Conversion
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the target entity name from an INSERT statement.
    /// </summary>
    public static string GetInsertTargetEntity(InsertStatement insert)
    {
        if (insert.InsertSpecification.Target is NamedTableReference named)
        {
            return GetMultiPartName(named.SchemaObject);
        }
        throw new QueryParseException("INSERT target must be a named table.");
    }

    /// <summary>
    /// Extracts insert column names from an INSERT statement.
    /// </summary>
    public static List<string> GetInsertColumns(InsertStatement insert)
    {
        var columns = new List<string>();
        if (insert.InsertSpecification.Columns != null)
        {
            foreach (var col in insert.InsertSpecification.Columns)
            {
                columns.Add(GetColumnName(col));
            }
        }
        return columns;
    }

    /// <summary>
    /// Extracts value rows from INSERT ... VALUES statements.
    /// </summary>
    public static List<List<ISqlExpression>> GetInsertValueRows(InsertStatement insert)
    {
        var rows = new List<List<ISqlExpression>>();
        if (insert.InsertSpecification.InsertSource is ValuesInsertSource valuesSource)
        {
            foreach (var rowValue in valuesSource.RowValues)
            {
                var row = new List<ISqlExpression>();
                foreach (var colVal in rowValue.ColumnValues)
                {
                    row.Add(ConvertExpression(colVal));
                }
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>
    /// Gets the source SELECT from an INSERT ... SELECT statement.
    /// Returns null if the source is VALUES (not SELECT).
    /// </summary>
    public static QuerySpecification? GetInsertSelectSource(InsertStatement insert)
    {
        if (insert.InsertSpecification.InsertSource is SelectInsertSource selectSource
            && selectSource.Select is QuerySpecification querySpec)
        {
            return querySpec;
        }
        return null;
    }

    /// <summary>
    /// Extracts the target table from an UPDATE statement.
    /// </summary>
    public static SqlTableRef GetUpdateTargetTable(UpdateStatement update)
    {
        if (update.UpdateSpecification.Target is NamedTableReference named)
        {
            return ConvertNamedTable(named);
        }
        throw new QueryParseException("UPDATE target must be a named table.");
    }

    /// <summary>
    /// Extracts SET clauses from an UPDATE statement.
    /// </summary>
    public static List<SqlSetClause> GetUpdateSetClauses(UpdateStatement update)
    {
        var clauses = new List<SqlSetClause>();
        foreach (var setClause in update.UpdateSpecification.SetClauses)
        {
            if (setClause is AssignmentSetClause assignment)
            {
                var colName = GetColumnName(assignment.Column);
                var value = ConvertExpression(assignment.NewValue);
                clauses.Add(new SqlSetClause(colName, value));
            }
        }
        return clauses;
    }

    /// <summary>
    /// Extracts the WHERE condition from an UPDATE statement.
    /// </summary>
    public static ISqlCondition? GetUpdateWhere(UpdateStatement update)
    {
        return update.UpdateSpecification.WhereClause?.SearchCondition != null
            ? ConvertBooleanExpression(update.UpdateSpecification.WhereClause.SearchCondition)
            : null;
    }

    /// <summary>
    /// Extracts the FROM clause from an UPDATE statement for multi-table updates.
    /// </summary>
    public static FromClause? GetUpdateFromClause(UpdateStatement update)
    {
        return update.UpdateSpecification.FromClause;
    }

    /// <summary>
    /// Extracts the target table from a DELETE statement.
    /// </summary>
    public static SqlTableRef GetDeleteTargetTable(DeleteStatement delete)
    {
        if (delete.DeleteSpecification.Target is NamedTableReference named)
        {
            return ConvertNamedTable(named);
        }
        throw new QueryParseException("DELETE target must be a named table.");
    }

    /// <summary>
    /// Extracts the WHERE condition from a DELETE statement.
    /// </summary>
    public static ISqlCondition? GetDeleteWhere(DeleteStatement delete)
    {
        return delete.DeleteSpecification.WhereClause?.SearchCondition != null
            ? ConvertBooleanExpression(delete.DeleteSpecification.WhereClause.SearchCondition)
            : null;
    }

    /// <summary>
    /// Extracts the FROM clause from a DELETE statement for multi-table deletes.
    /// </summary>
    public static FromClause? GetDeleteFromClause(DeleteStatement delete)
    {
        return delete.DeleteSpecification.FromClause;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Helper: Column reference conversion
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts a ScriptDom <see cref="ColumnReferenceExpression"/> to a <see cref="SqlColumnRef"/>.
    /// </summary>
    public static SqlColumnRef ConvertColumnRef(ColumnReferenceExpression colRef)
    {
        if (colRef.ColumnType == ColumnType.Wildcard)
        {
            return SqlColumnRef.Wildcard();
        }

        var identifiers = colRef.MultiPartIdentifier?.Identifiers;
        if (identifiers == null || identifiers.Count == 0)
        {
            return SqlColumnRef.Simple("*");
        }

        if (identifiers.Count == 1)
        {
            return SqlColumnRef.Simple(identifiers[0].Value);
        }

        // table.column (or schema.table.column - take last two)
        var tableName = identifiers[identifiers.Count - 2].Value;
        var columnName = identifiers[identifiers.Count - 1].Value;
        return SqlColumnRef.Qualified(tableName, columnName);
    }

    /// <summary>
    /// Gets the column name from a <see cref="ColumnReferenceExpression"/> (single identifier).
    /// </summary>
    private static string GetColumnName(ColumnReferenceExpression colRef)
    {
        var ids = colRef.MultiPartIdentifier?.Identifiers;
        if (ids == null || ids.Count == 0)
            return "*";
        return ids[ids.Count - 1].Value;
    }

    // ───────────────────────────────────────────────────────────────────
    //  Helper: Literal & type utilities
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks if a ScriptDom expression is a literal value.
    /// </summary>
    private static bool IsLiteralExpression(ScalarExpression expr)
    {
        return expr is IntegerLiteral or NumericLiteral or RealLiteral or MoneyLiteral
            or StringLiteral or NullLiteral;
    }

    /// <summary>
    /// Extracts a <see cref="SqlLiteral"/> from a ScriptDom literal expression.
    /// </summary>
    private static SqlLiteral ExtractLiteral(ScalarExpression expr)
    {
        return expr switch
        {
            IntegerLiteral intLit => SqlLiteral.Number(intLit.Value),
            NumericLiteral numLit => SqlLiteral.Number(numLit.Value),
            RealLiteral realLit => SqlLiteral.Number(realLit.Value),
            MoneyLiteral moneyLit => SqlLiteral.Number(moneyLit.Value),
            StringLiteral strLit => SqlLiteral.String(strLit.Value),
            NullLiteral => SqlLiteral.Null(),
            _ => SqlLiteral.String(expr.ToString() ?? "")
        };
    }

    /// <summary>
    /// Extracts a string value from a ScriptDom expression (for LIKE patterns, etc.).
    /// </summary>
    private static string ExtractStringValue(ScalarExpression expr)
    {
        return expr switch
        {
            StringLiteral strLit => strLit.Value,
            IntegerLiteral intLit => intLit.Value,
            _ => expr.ToString() ?? ""
        };
    }

    /// <summary>
    /// Formats a ScriptDom DataTypeReference as a string (e.g., "nvarchar(100)", "int").
    /// </summary>
    private static string FormatDataType(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            var name = sqlType.SqlDataTypeOption.ToString().ToLowerInvariant();
            if (sqlType.Parameters.Count > 0)
            {
                var parms = string.Join(", ", sqlType.Parameters.Select(p => p.Value));
                return $"{name}({parms})";
            }
            return name;
        }

        if (dataType is XmlDataTypeReference)
            return "xml";

        // Generic fallback
        if (dataType.Name?.Identifiers != null && dataType.Name.Identifiers.Count > 0)
        {
            return string.Join(".", dataType.Name.Identifiers.Select(i => i.Value));
        }

        return "varchar";
    }

    /// <summary>
    /// Gets a table/schema name from a multi-part identifier.
    /// Joins with dots for schema-qualified names (e.g., "metadata.entity").
    /// </summary>
    private static string GetMultiPartName(SchemaObjectName schemaObject)
    {
        var parts = new List<string>();
        if (schemaObject.SchemaIdentifier != null)
        {
            parts.Add(schemaObject.SchemaIdentifier.Value);
        }
        if (schemaObject.BaseIdentifier != null)
        {
            parts.Add(schemaObject.BaseIdentifier.Value);
        }

        return parts.Count > 0 ? string.Join(".", parts) : "unknown";
    }

    // ───────────────────────────────────────────────────────────────────
    //  Helper: Operator conversion
    // ───────────────────────────────────────────────────────────────────

    private static SqlComparisonOperator ConvertComparisonOperator(BooleanComparisonType type)
    {
        return type switch
        {
            BooleanComparisonType.Equals => SqlComparisonOperator.Equal,
            BooleanComparisonType.NotEqualToBrackets => SqlComparisonOperator.NotEqual,
            BooleanComparisonType.NotEqualToExclamation => SqlComparisonOperator.NotEqual,
            BooleanComparisonType.LessThan => SqlComparisonOperator.LessThan,
            BooleanComparisonType.GreaterThan => SqlComparisonOperator.GreaterThan,
            BooleanComparisonType.LessThanOrEqualTo => SqlComparisonOperator.LessThanOrEqual,
            BooleanComparisonType.GreaterThanOrEqualTo => SqlComparisonOperator.GreaterThanOrEqual,
            _ => SqlComparisonOperator.Equal
        };
    }

    private static SqlComparisonOperator ReverseOperator(SqlComparisonOperator op)
    {
        return op switch
        {
            SqlComparisonOperator.LessThan => SqlComparisonOperator.GreaterThan,
            SqlComparisonOperator.GreaterThan => SqlComparisonOperator.LessThan,
            SqlComparisonOperator.LessThanOrEqual => SqlComparisonOperator.GreaterThanOrEqual,
            SqlComparisonOperator.GreaterThanOrEqual => SqlComparisonOperator.LessThanOrEqual,
            _ => op
        };
    }

    private static SqlBinaryOperator ConvertBinaryOperator(BinaryExpressionType type)
    {
        return type switch
        {
            BinaryExpressionType.Add => SqlBinaryOperator.Add,
            BinaryExpressionType.Subtract => SqlBinaryOperator.Subtract,
            BinaryExpressionType.Multiply => SqlBinaryOperator.Multiply,
            BinaryExpressionType.Divide => SqlBinaryOperator.Divide,
            BinaryExpressionType.Modulo => SqlBinaryOperator.Modulo,
            _ => SqlBinaryOperator.Add
        };
    }

    private static SqlUnaryOperator ConvertUnaryOperator(UnaryExpressionType type)
    {
        return type switch
        {
            UnaryExpressionType.Negative => SqlUnaryOperator.Negate,
            UnaryExpressionType.Positive => SqlUnaryOperator.Negate, // Pass through
            _ => SqlUnaryOperator.Negate
        };
    }

    // ───────────────────────────────────────────────────────────────────
    //  UNION conversion helpers
    // ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the list of QuerySpecifications and union types from a BinaryQueryExpression tree.
    /// </summary>
    public static void FlattenUnion(
        BinaryQueryExpression binaryQuery,
        List<QuerySpecification> queries,
        List<bool> isUnionAll)
    {
        // Recurse left
        if (binaryQuery.FirstQueryExpression is BinaryQueryExpression leftBinary)
        {
            FlattenUnion(leftBinary, queries, isUnionAll);
        }
        else if (binaryQuery.FirstQueryExpression is QuerySpecification leftSpec)
        {
            queries.Add(leftSpec);
        }

        // Record the union type
        isUnionAll.Add(binaryQuery.All);

        // Recurse right
        if (binaryQuery.SecondQueryExpression is BinaryQueryExpression rightBinary)
        {
            FlattenUnion(rightBinary, queries, isUnionAll);
        }
        else if (binaryQuery.SecondQueryExpression is QuerySpecification rightSpec)
        {
            queries.Add(rightSpec);
        }
    }
}
