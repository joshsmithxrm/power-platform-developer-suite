using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Query.Transpilation;

/// <summary>
/// Generates FetchXML from ScriptDom AST (SelectStatement).
/// Replaces the old SqlToFetchXmlTranspiler with ScriptDom-based implementation.
/// </summary>
/// <remarks>
/// Business Rules:
/// - SQL comparison operators map to FetchXML operators
/// - LIKE patterns with wildcards map to like, begins-with, ends-with
/// - JOINs map to link-entity elements
/// - AND/OR map to filter type attribute
/// - String values are XML-escaped
/// - Entity/attribute names are normalized to lowercase
/// </remarks>
public sealed class FetchXmlGenerator
{
    private int _aliasCounter;
    private string _currentEntityName = "";

    /// <summary>
    /// Generates FetchXML from a ScriptDom SelectStatement.
    /// </summary>
    /// <param name="selectStatement">The parsed SELECT statement.</param>
    /// <returns>The transpile result with FetchXML and virtual columns.</returns>
    public TranspileResult Generate(SelectStatement selectStatement)
    {
        ArgumentNullException.ThrowIfNull(selectStatement);

        var querySpec = selectStatement.QueryExpression as QuerySpecification
            ?? throw new InvalidOperationException("Expected QuerySpecification in SelectStatement");

        return GenerateFromQuerySpec(querySpec);
    }

    /// <summary>
    /// Generates FetchXML from a QuerySpecification.
    /// </summary>
    public TranspileResult GenerateFromQuerySpec(QuerySpecification querySpec)
    {
        ArgumentNullException.ThrowIfNull(querySpec);

        _aliasCounter = 0;

        // Get the primary table reference
        var fromClause = querySpec.FromClause;
        if (fromClause == null || fromClause.TableReferences.Count == 0)
        {
            throw new InvalidOperationException("SELECT statement must have a FROM clause");
        }

        // Extract main table and joins
        var (mainTable, joins) = ExtractTableReferences(fromClause);
        _currentEntityName = NormalizeEntityName(GetTableName(mainTable));

        // Detect virtual columns
        var virtualColumns = DetectVirtualColumns(querySpec.SelectElements);

        var lines = new List<string>();

        // Build <fetch> element attributes
        var fetchAttrs = new List<string>();

        // TOP clause
        var topValue = GetTopValue(querySpec);
        if (topValue.HasValue)
        {
            fetchAttrs.Add($"top=\"{topValue.Value}\"");
        }

        // DISTINCT
        if (querySpec.UniqueRowFilter == UniqueRowFilter.Distinct)
        {
            fetchAttrs.Add("distinct=\"true\"");
        }

        // Check for aggregates
        var hasAggregates = HasAggregates(querySpec.SelectElements);
        if (hasAggregates)
        {
            fetchAttrs.Add("aggregate=\"true\"");
        }

        lines.Add(fetchAttrs.Count > 0
            ? $"<fetch {string.Join(" ", fetchAttrs)}>"
            : "<fetch>");

        // <entity> element
        lines.Add($"  <entity name=\"{_currentEntityName}\">");

        // Columns
        EmitColumns(querySpec, mainTable, virtualColumns, lines);

        // Link entities (JOINs)
        foreach (var join in joins)
        {
            EmitJoin(join, querySpec.SelectElements, lines);
        }

        // Filter (WHERE)
        if (querySpec.WhereClause != null)
        {
            EmitCondition(querySpec.WhereClause.SearchCondition, lines, "    ");
        }

        // ORDER BY
        if (querySpec.OrderByClause != null)
        {
            EmitOrderBy(querySpec.OrderByClause, querySpec.SelectElements, hasAggregates, lines);
        }

        lines.Add("  </entity>");
        lines.Add("</fetch>");

        return new TranspileResult
        {
            FetchXml = string.Join("\n", lines),
            VirtualColumns = virtualColumns
        };
    }

    #region Table Reference Extraction

    /// <summary>
    /// Extracts the main table and any JOINs from the FROM clause.
    /// </summary>
    private static (TableReference mainTable, List<QualifiedJoin> joins) ExtractTableReferences(FromClause fromClause)
    {
        var joins = new List<QualifiedJoin>();
        TableReference? mainTable = null;

        foreach (var tableRef in fromClause.TableReferences)
        {
            mainTable = ExtractFromTableReference(tableRef, joins);
        }

        return (mainTable!, joins);
    }

    private static TableReference ExtractFromTableReference(TableReference tableRef, List<QualifiedJoin> joins)
    {
        switch (tableRef)
        {
            case NamedTableReference namedTable:
                return namedTable;

            case QualifiedJoin qualifiedJoin:
                joins.Add(qualifiedJoin);
                // The first table is the main table, recursively extract from left side
                return ExtractFromTableReference(qualifiedJoin.FirstTableReference, joins);

            default:
                throw new NotSupportedException($"Table reference type {tableRef.GetType().Name} is not supported");
        }
    }

    private static string GetTableName(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference namedTable => GetSchemaObjectName(namedTable.SchemaObject),
            _ => throw new NotSupportedException($"Cannot get table name from {tableRef.GetType().Name}")
        };
    }

    private static string? GetTableAlias(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference namedTable => namedTable.Alias?.Value,
            _ => null
        };
    }

    private static string GetSchemaObjectName(SchemaObjectName schemaObject)
    {
        // Use the base identifier (last part of the name)
        return schemaObject.BaseIdentifier?.Value ?? schemaObject.Identifiers.Last().Value;
    }

    #endregion

    #region Column Emission

    private void EmitColumns(
        QuerySpecification querySpec,
        TableReference mainTable,
        Dictionary<string, VirtualColumnInfo> virtualColumns,
        List<string> lines)
    {
        var groupByColumns = ExtractGroupByColumns(querySpec.GroupByClause);
        var emittedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mainTableName = GetTableName(mainTable);
        var mainTableAlias = GetTableAlias(mainTable);

        foreach (var element in querySpec.SelectElements)
        {
            switch (element)
            {
                case SelectStarExpression star:
                    EmitSelectStar(star, mainTableName, mainTableAlias, lines);
                    break;

                case SelectScalarExpression scalar:
                    EmitScalarExpression(scalar, mainTableName, mainTableAlias, groupByColumns, virtualColumns, emittedColumns, lines);
                    break;
            }
        }

        // Emit GROUP BY columns not in SELECT
        EmitMissingGroupByColumns(querySpec.GroupByClause, querySpec.SelectElements, lines);

        // Emit GROUP BY expressions (date functions)
        EmitDateGroupingAttributes(querySpec.GroupByClause, querySpec.SelectElements, lines);
    }

    private void EmitSelectStar(
        SelectStarExpression star,
        string mainTableName,
        string? mainTableAlias,
        List<string> lines)
    {
        if (star.Qualifier == null)
        {
            // SELECT *
            lines.Add("    <all-attributes />");
        }
        else
        {
            // SELECT t.* - check if it's the main table
            var qualifierName = star.Qualifier.Identifiers.Last().Value;
            if (IsMainTable(qualifierName, mainTableName, mainTableAlias))
            {
                lines.Add("    <all-attributes />");
            }
            // For joined table wildcards, we'll handle in link-entity
        }
    }

    private void EmitScalarExpression(
        SelectScalarExpression scalar,
        string mainTableName,
        string? mainTableAlias,
        HashSet<string> groupByColumns,
        Dictionary<string, VirtualColumnInfo> virtualColumns,
        HashSet<string> emittedColumns,
        List<string> lines)
    {
        var alias = scalar.ColumnName?.Value;

        switch (scalar.Expression)
        {
            case ColumnReferenceExpression colRef:
                EmitColumnReference(colRef, alias, mainTableName, mainTableAlias, groupByColumns, virtualColumns, emittedColumns, lines);
                break;

            case FunctionCall funcCall when IsAggregateFunction(funcCall):
                EmitAggregateColumn(funcCall, alias, lines);
                break;

            case FunctionCall funcCall:
                // Non-aggregate function - emit referenced columns
                EmitReferencedColumnsFromExpression(scalar.Expression, emittedColumns, lines);
                break;

            case SearchedCaseExpression:
            case SimpleCaseExpression:
            case IIfCall:
                // Computed columns - emit referenced columns for client-side evaluation
                EmitReferencedColumnsFromExpression(scalar.Expression, emittedColumns, lines);
                break;

            default:
                // Other expressions - emit referenced columns
                EmitReferencedColumnsFromExpression(scalar.Expression, emittedColumns, lines);
                break;
        }
    }

    private void EmitColumnReference(
        ColumnReferenceExpression colRef,
        string? alias,
        string mainTableName,
        string? mainTableAlias,
        HashSet<string> groupByColumns,
        Dictionary<string, VirtualColumnInfo> virtualColumns,
        HashSet<string> emittedColumns,
        List<string> lines)
    {
        var (tableName, columnName) = ExtractColumnParts(colRef);

        // Check if this belongs to the main table
        if (tableName != null && !IsMainTable(tableName, mainTableName, mainTableAlias))
        {
            return; // Will be handled by link-entity
        }

        var attrName = NormalizeAttributeName(columnName);

        // Check if this is a virtual column
        if (virtualColumns.TryGetValue(attrName, out var virtualInfo))
        {
            // Emit base column instead
            if (emittedColumns.Add(virtualInfo.BaseColumnName))
            {
                var attrs = new List<string> { $"name=\"{virtualInfo.BaseColumnName}\"" };
                if (groupByColumns.Contains(virtualInfo.BaseColumnName))
                {
                    attrs.Add("groupby=\"true\"");
                }
                lines.Add($"    <attribute {string.Join(" ", attrs)} />");
            }
            return;
        }

        // Regular column
        if (emittedColumns.Add(attrName))
        {
            var attrs = new List<string> { $"name=\"{attrName}\"" };
            if (alias != null)
            {
                attrs.Add($"alias=\"{alias}\"");
            }
            if (groupByColumns.Contains(attrName))
            {
                attrs.Add("groupby=\"true\"");
            }
            lines.Add($"    <attribute {string.Join(" ", attrs)} />");
        }
    }

    private void EmitAggregateColumn(FunctionCall funcCall, string? alias, List<string> lines)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        var attrs = new List<string>();

        var isCountStar = funcName == "COUNT" &&
            funcCall.Parameters.Count == 1 &&
            funcCall.Parameters[0] is ColumnReferenceExpression cr &&
            cr.ColumnType == ColumnType.Wildcard;

        if (isCountStar)
        {
            // COUNT(*) - use primary key column
            var primaryKeyColumn = $"{_currentEntityName}id";
            attrs.Add($"name=\"{primaryKeyColumn}\"");
            attrs.Add("aggregate=\"count\"");
        }
        else if (funcCall.Parameters.Count > 0 && funcCall.Parameters[0] is ColumnReferenceExpression colRef)
        {
            var (_, columnName) = ExtractColumnParts(colRef);
            attrs.Add($"name=\"{NormalizeAttributeName(columnName)}\"");

            var aggregateType = MapAggregateFunction(funcName, hasColumn: true);
            attrs.Add($"aggregate=\"{aggregateType}\"");

            if (funcCall.UniqueRowFilter == UniqueRowFilter.Distinct)
            {
                attrs.Add("distinct=\"true\"");
            }
        }
        else
        {
            // Aggregate without column (COUNT(*) handled above)
            var aggregateType = MapAggregateFunction(funcName, hasColumn: false);
            attrs.Add($"aggregate=\"{aggregateType}\"");
        }

        // Alias is required for aggregates
        var effectiveAlias = alias ?? GenerateAlias(funcName);
        attrs.Add($"alias=\"{effectiveAlias}\"");

        lines.Add($"    <attribute {string.Join(" ", attrs)} />");
    }

    private void EmitReferencedColumnsFromExpression(
        ScalarExpression expression,
        HashSet<string> emittedColumns,
        List<string> lines)
    {
        var columnNames = new List<string>();
        CollectColumnReferences(expression, columnNames);

        foreach (var colName in columnNames)
        {
            var attrName = NormalizeAttributeName(colName);
            if (emittedColumns.Add(attrName))
            {
                lines.Add($"    <attribute name=\"{attrName}\" />");
            }
        }
    }

    private static void CollectColumnReferences(ScalarExpression expression, List<string> columnNames)
    {
        switch (expression)
        {
            case ColumnReferenceExpression colRef:
                var (_, columnName) = ExtractColumnParts(colRef);
                columnNames.Add(columnName);
                break;

            case BinaryExpression binary:
                CollectColumnReferences(binary.FirstExpression, columnNames);
                CollectColumnReferences(binary.SecondExpression, columnNames);
                break;

            case UnaryExpression unary:
                CollectColumnReferences(unary.Expression, columnNames);
                break;

            case FunctionCall func:
                foreach (var param in func.Parameters)
                {
                    if (param is ScalarExpression scalar)
                    {
                        CollectColumnReferences(scalar, columnNames);
                    }
                }
                break;

            case SearchedCaseExpression caseExpr:
                foreach (var when in caseExpr.WhenClauses)
                {
                    CollectColumnReferencesFromCondition(when.WhenExpression, columnNames);
                    CollectColumnReferences(when.ThenExpression, columnNames);
                }
                if (caseExpr.ElseExpression != null)
                {
                    CollectColumnReferences(caseExpr.ElseExpression, columnNames);
                }
                break;

            case SimpleCaseExpression simpleCase:
                CollectColumnReferences(simpleCase.InputExpression, columnNames);
                foreach (var when in simpleCase.WhenClauses)
                {
                    CollectColumnReferences(when.WhenExpression, columnNames);
                    CollectColumnReferences(when.ThenExpression, columnNames);
                }
                if (simpleCase.ElseExpression != null)
                {
                    CollectColumnReferences(simpleCase.ElseExpression, columnNames);
                }
                break;

            case IIfCall iif:
                CollectColumnReferencesFromCondition(iif.Predicate, columnNames);
                CollectColumnReferences(iif.ThenExpression, columnNames);
                CollectColumnReferences(iif.ElseExpression, columnNames);
                break;

            case CastCall cast:
                CollectColumnReferences(cast.Parameter, columnNames);
                break;

            case ConvertCall convert:
                CollectColumnReferences(convert.Parameter, columnNames);
                break;

            case ParenthesisExpression paren:
                CollectColumnReferences(paren.Expression, columnNames);
                break;
        }
    }

    private static void CollectColumnReferencesFromCondition(BooleanExpression condition, List<string> columnNames)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comp:
                if (comp.FirstExpression is ScalarExpression left)
                    CollectColumnReferences(left, columnNames);
                if (comp.SecondExpression is ScalarExpression right)
                    CollectColumnReferences(right, columnNames);
                break;

            case BooleanBinaryExpression binary:
                CollectColumnReferencesFromCondition(binary.FirstExpression, columnNames);
                CollectColumnReferencesFromCondition(binary.SecondExpression, columnNames);
                break;

            case BooleanNotExpression not:
                CollectColumnReferencesFromCondition(not.Expression, columnNames);
                break;

            case BooleanParenthesisExpression paren:
                CollectColumnReferencesFromCondition(paren.Expression, columnNames);
                break;

            case BooleanIsNullExpression isNull:
                if (isNull.Expression is ScalarExpression scalar)
                    CollectColumnReferences(scalar, columnNames);
                break;

            case LikePredicate like:
                if (like.FirstExpression is ScalarExpression likeExpr)
                    CollectColumnReferences(likeExpr, columnNames);
                break;

            case InPredicate inPred:
                if (inPred.Expression is ScalarExpression inExpr)
                    CollectColumnReferences(inExpr, columnNames);
                break;
        }
    }

    private void EmitMissingGroupByColumns(
        GroupByClause? groupByClause,
        IList<SelectElement> selectElements,
        List<string> lines)
    {
        if (groupByClause == null) return;

        var selectedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar &&
                scalar.Expression is ColumnReferenceExpression colRef)
            {
                var (_, columnName) = ExtractColumnParts(colRef);
                selectedColumns.Add(NormalizeAttributeName(columnName));
            }
        }

        foreach (var grouping in groupByClause.GroupingSpecifications)
        {
            if (grouping is ExpressionGroupingSpecification exprGroup &&
                exprGroup.Expression is ColumnReferenceExpression colRef)
            {
                var (_, columnName) = ExtractColumnParts(colRef);
                var attrName = NormalizeAttributeName(columnName);

                if (!selectedColumns.Contains(attrName))
                {
                    lines.Add($"    <attribute name=\"{attrName}\" groupby=\"true\" />");
                }
            }
        }
    }

    private void EmitDateGroupingAttributes(
        GroupByClause? groupByClause,
        IList<SelectElement> selectElements,
        List<string> lines)
    {
        if (groupByClause == null) return;

        foreach (var grouping in groupByClause.GroupingSpecifications)
        {
            if (grouping is ExpressionGroupingSpecification exprGroup &&
                exprGroup.Expression is FunctionCall func)
            {
                var funcName = func.FunctionName.Value.ToUpperInvariant();
                var dategrouping = funcName switch
                {
                    "YEAR" => "year",
                    "MONTH" => "month",
                    "DAY" => "day",
                    "QUARTER" => "quarter",
                    "WEEK" => "week",
                    _ => null
                };

                if (dategrouping == null) continue;
                if (func.Parameters.Count != 1) continue;

                if (func.Parameters[0] is ColumnReferenceExpression colRef)
                {
                    var (_, columnName) = ExtractColumnParts(colRef);
                    var attrName = NormalizeAttributeName(columnName);

                    var alias = FindDateGroupingAlias(func, selectElements)
                        ?? $"{dategrouping}_{attrName}";

                    lines.Add($"    <attribute name=\"{attrName}\" groupby=\"true\" dategrouping=\"{dategrouping}\" alias=\"{alias}\" />");
                }
            }
        }
    }

    private static string? FindDateGroupingAlias(FunctionCall groupByFunc, IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar &&
                scalar.ColumnName != null &&
                scalar.Expression is FunctionCall selectFunc)
            {
                if (string.Equals(selectFunc.FunctionName.Value, groupByFunc.FunctionName.Value, StringComparison.OrdinalIgnoreCase) &&
                    selectFunc.Parameters.Count == groupByFunc.Parameters.Count)
                {
                    // Check if arguments match
                    if (selectFunc.Parameters.Count == 1 &&
                        selectFunc.Parameters[0] is ColumnReferenceExpression selCol &&
                        groupByFunc.Parameters[0] is ColumnReferenceExpression grpCol)
                    {
                        var (_, selColName) = ExtractColumnParts(selCol);
                        var (_, grpColName) = ExtractColumnParts(grpCol);

                        if (string.Equals(selColName, grpColName, StringComparison.OrdinalIgnoreCase))
                        {
                            return scalar.ColumnName.Value;
                        }
                    }
                }
            }
        }
        return null;
    }

    #endregion

    #region Join Emission

    private void EmitJoin(QualifiedJoin join, IList<SelectElement> selectElements, List<string> lines)
    {
        var joinTable = join.SecondTableReference as NamedTableReference
            ?? throw new NotSupportedException("Only named table references are supported in JOINs");

        var linkType = join.QualifiedJoinType switch
        {
            QualifiedJoinType.Inner => "inner",
            QualifiedJoinType.LeftOuter => "outer",
            QualifiedJoinType.RightOuter => "outer",
            QualifiedJoinType.FullOuter => "outer",
            _ => "inner"
        };

        var tableName = NormalizeEntityName(GetSchemaObjectName(joinTable.SchemaObject));
        var tableAlias = joinTable.Alias?.Value;

        // Extract join columns from the ON condition
        var (fromColumn, toColumn) = ExtractJoinColumns(join.SearchCondition, tableName, tableAlias);

        var aliasAttr = tableAlias != null ? $" alias=\"{tableAlias}\"" : "";

        lines.Add($"    <link-entity name=\"{tableName}\" from=\"{fromColumn}\" to=\"{toColumn}\" link-type=\"{linkType}\"{aliasAttr}>");

        // Add columns that belong to this join table
        EmitJoinTableColumns(selectElements, tableName, tableAlias, lines);

        lines.Add("    </link-entity>");
    }

    private static (string fromColumn, string toColumn) ExtractJoinColumns(
        BooleanExpression condition,
        string joinTableName,
        string? joinTableAlias)
    {
        if (condition is BooleanComparisonExpression comparison &&
            comparison.ComparisonType == BooleanComparisonType.Equals)
        {
            string? leftTable = null, leftColumn = null;
            string? rightTable = null, rightColumn = null;

            if (comparison.FirstExpression is ColumnReferenceExpression leftCol)
            {
                (leftTable, leftColumn) = ExtractColumnParts(leftCol);
            }
            if (comparison.SecondExpression is ColumnReferenceExpression rightCol)
            {
                (rightTable, rightColumn) = ExtractColumnParts(rightCol);
            }

            if (leftColumn != null && rightColumn != null)
            {
                // Determine which is from the join table
                var leftIsJoin = IsJoinTable(leftTable, joinTableName, joinTableAlias);
                var rightIsJoin = IsJoinTable(rightTable, joinTableName, joinTableAlias);

                if (leftIsJoin && !rightIsJoin)
                {
                    return (NormalizeAttributeName(leftColumn), NormalizeAttributeName(rightColumn));
                }
                if (rightIsJoin && !leftIsJoin)
                {
                    return (NormalizeAttributeName(rightColumn), NormalizeAttributeName(leftColumn));
                }

                // Fallback
                return (NormalizeAttributeName(rightColumn), NormalizeAttributeName(leftColumn));
            }
        }

        throw new NotSupportedException("JOIN ON clause must be a simple equality comparison between columns");
    }

    private static bool IsJoinTable(string? tableName, string joinTableName, string? joinTableAlias)
    {
        if (tableName == null) return false;
        return string.Equals(tableName, joinTableName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tableName, joinTableAlias, StringComparison.OrdinalIgnoreCase);
    }

    private void EmitJoinTableColumns(
        IList<SelectElement> selectElements,
        string joinTableName,
        string? joinTableAlias,
        List<string> lines)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectStarExpression star && star.Qualifier != null)
            {
                var qualifierName = star.Qualifier.Identifiers.Last().Value;
                if (IsJoinTable(qualifierName, joinTableName, joinTableAlias))
                {
                    lines.Add("      <all-attributes />");
                }
            }
            else if (element is SelectScalarExpression scalar &&
                     scalar.Expression is ColumnReferenceExpression colRef)
            {
                var (tableName, columnName) = ExtractColumnParts(colRef);
                if (IsJoinTable(tableName, joinTableName, joinTableAlias))
                {
                    var attrName = NormalizeAttributeName(columnName);
                    var alias = scalar.ColumnName?.Value;
                    if (alias != null)
                    {
                        lines.Add($"      <attribute name=\"{attrName}\" alias=\"{alias}\" />");
                    }
                    else
                    {
                        lines.Add($"      <attribute name=\"{attrName}\" />");
                    }
                }
            }
        }
    }

    #endregion

    #region Condition Emission

    private void EmitCondition(BooleanExpression condition, List<string> lines, string indent)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
                EmitComparison(comparison, lines, indent);
                break;

            case LikePredicate like:
                EmitLike(like, lines, indent);
                break;

            case BooleanIsNullExpression isNull:
                EmitIsNull(isNull, lines, indent);
                break;

            case InPredicate inPred:
                EmitIn(inPred, lines, indent);
                break;

            case BooleanBinaryExpression binary:
                EmitLogical(binary, lines, indent);
                break;

            case BooleanParenthesisExpression paren:
                EmitCondition(paren.Expression, lines, indent);
                break;

            case BooleanNotExpression not:
                // NOT is handled by negating the inner condition
                EmitNegatedCondition(not.Expression, lines, indent);
                break;
        }
    }

    private void EmitComparison(BooleanComparisonExpression comparison, List<string> lines, string indent)
    {
        if (comparison.FirstExpression is not ColumnReferenceExpression colRef)
        {
            // Expression condition - skip, handled client-side
            return;
        }

        if (comparison.SecondExpression is not Literal literal)
        {
            // Column-to-column comparison - skip, handled client-side
            return;
        }

        var (_, columnName) = ExtractColumnParts(colRef);
        var op = MapComparisonOperator(comparison.ComparisonType);
        var value = FormatLiteralValue(literal);
        var attr = NormalizeAttributeName(columnName);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" value=\"{value}\" />");
        lines.Add($"{indent}</filter>");
    }

    private void EmitLike(LikePredicate like, List<string> lines, string indent)
    {
        if (like.FirstExpression is not ColumnReferenceExpression colRef)
        {
            return;
        }

        if (like.SecondExpression is not StringLiteral patternLiteral)
        {
            return;
        }

        var (_, columnName) = ExtractColumnParts(colRef);
        var pattern = patternLiteral.Value;
        var attr = NormalizeAttributeName(columnName);
        var isNegated = like.NotDefined;

        string op, value;

        if (pattern.StartsWith('%') && pattern.EndsWith('%'))
        {
            op = isNegated ? "not-like" : "like";
            value = pattern;
        }
        else if (pattern.StartsWith('%'))
        {
            op = isNegated ? "not-end-with" : "ends-with";
            value = pattern[1..];
        }
        else if (pattern.EndsWith('%'))
        {
            op = isNegated ? "not-begin-with" : "begins-with";
            value = pattern[..^1];
        }
        else
        {
            op = isNegated ? "not-like" : "like";
            value = pattern;
        }

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" value=\"{EscapeXml(value)}\" />");
        lines.Add($"{indent}</filter>");
    }

    private void EmitIsNull(BooleanIsNullExpression isNull, List<string> lines, string indent)
    {
        if (isNull.Expression is not ColumnReferenceExpression colRef)
        {
            return;
        }

        var (_, columnName) = ExtractColumnParts(colRef);
        var op = isNull.IsNot ? "not-null" : "null";
        var attr = NormalizeAttributeName(columnName);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" />");
        lines.Add($"{indent}</filter>");
    }

    private void EmitIn(InPredicate inPred, List<string> lines, string indent)
    {
        if (inPred.Expression is not ColumnReferenceExpression colRef)
        {
            return;
        }

        var (_, columnName) = ExtractColumnParts(colRef);
        var op = inPred.NotDefined ? "not-in" : "in";
        var attr = NormalizeAttributeName(columnName);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\">");

        foreach (var value in inPred.Values)
        {
            if (value is Literal literal)
            {
                lines.Add($"{indent}    <value>{FormatLiteralValue(literal)}</value>");
            }
        }

        lines.Add($"{indent}  </condition>");
        lines.Add($"{indent}</filter>");
    }

    private void EmitLogical(BooleanBinaryExpression binary, List<string> lines, string indent)
    {
        var filterType = binary.BinaryExpressionType == BooleanBinaryExpressionType.Or ? "or" : "and";

        lines.Add($"{indent}<filter type=\"{filterType}\">");
        EmitConditionInner(binary.FirstExpression, lines, indent + "  ");
        EmitConditionInner(binary.SecondExpression, lines, indent + "  ");
        lines.Add($"{indent}</filter>");
    }

    private void EmitNegatedCondition(BooleanExpression condition, List<string> lines, string indent)
    {
        // For NOT, we need to negate specific conditions
        switch (condition)
        {
            case BooleanIsNullExpression isNull:
                // NOT (IS NULL) -> IS NOT NULL, NOT (IS NOT NULL) -> IS NULL
                if (isNull.Expression is ColumnReferenceExpression colRef)
                {
                    var (_, columnName) = ExtractColumnParts(colRef);
                    var op = isNull.IsNot ? "null" : "not-null";
                    var attr = NormalizeAttributeName(columnName);
                    lines.Add($"{indent}<filter>");
                    lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" />");
                    lines.Add($"{indent}</filter>");
                }
                break;

            case LikePredicate like:
                // NOT LIKE
                if (like.FirstExpression is ColumnReferenceExpression likeCol &&
                    like.SecondExpression is StringLiteral patternLiteral)
                {
                    var (_, columnName) = ExtractColumnParts(likeCol);
                    var pattern = patternLiteral.Value;
                    var attr = NormalizeAttributeName(columnName);
                    var isNegated = !like.NotDefined; // Invert

                    string op, value;
                    if (pattern.StartsWith('%') && pattern.EndsWith('%'))
                    {
                        op = isNegated ? "not-like" : "like";
                        value = pattern;
                    }
                    else if (pattern.StartsWith('%'))
                    {
                        op = isNegated ? "not-end-with" : "ends-with";
                        value = pattern[1..];
                    }
                    else if (pattern.EndsWith('%'))
                    {
                        op = isNegated ? "not-begin-with" : "begins-with";
                        value = pattern[..^1];
                    }
                    else
                    {
                        op = isNegated ? "not-like" : "like";
                        value = pattern;
                    }

                    lines.Add($"{indent}<filter>");
                    lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" value=\"{EscapeXml(value)}\" />");
                    lines.Add($"{indent}</filter>");
                }
                break;

            case InPredicate inPred:
                // NOT IN
                if (inPred.Expression is ColumnReferenceExpression inCol)
                {
                    var (_, columnName) = ExtractColumnParts(inCol);
                    var op = inPred.NotDefined ? "in" : "not-in";
                    var attr = NormalizeAttributeName(columnName);

                    lines.Add($"{indent}<filter>");
                    lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\">");
                    foreach (var value in inPred.Values)
                    {
                        if (value is Literal literal)
                        {
                            lines.Add($"{indent}    <value>{FormatLiteralValue(literal)}</value>");
                        }
                    }
                    lines.Add($"{indent}  </condition>");
                    lines.Add($"{indent}</filter>");
                }
                break;

            default:
                // Other NOT expressions - skip (handled client-side)
                break;
        }
    }

    private void EmitConditionInner(BooleanExpression condition, List<string> lines, string indent)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
            {
                if (comparison.FirstExpression is ColumnReferenceExpression colRef &&
                    comparison.SecondExpression is Literal literal)
                {
                    var (_, columnName) = ExtractColumnParts(colRef);
                    var op = MapComparisonOperator(comparison.ComparisonType);
                    var value = FormatLiteralValue(literal);
                    var attr = NormalizeAttributeName(columnName);
                    lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" value=\"{value}\" />");
                }
                break;
            }

            case LikePredicate like:
            {
                if (like.FirstExpression is ColumnReferenceExpression colRef &&
                    like.SecondExpression is StringLiteral patternLiteral)
                {
                    var (_, columnName) = ExtractColumnParts(colRef);
                    var pattern = patternLiteral.Value;
                    var attr = NormalizeAttributeName(columnName);
                    var isNegated = like.NotDefined;

                    string op, val;
                    if (pattern.StartsWith('%') && pattern.EndsWith('%'))
                    {
                        op = isNegated ? "not-like" : "like";
                        val = pattern;
                    }
                    else if (pattern.StartsWith('%'))
                    {
                        op = isNegated ? "not-end-with" : "ends-with";
                        val = pattern[1..];
                    }
                    else if (pattern.EndsWith('%'))
                    {
                        op = isNegated ? "not-begin-with" : "begins-with";
                        val = pattern[..^1];
                    }
                    else
                    {
                        op = isNegated ? "not-like" : "like";
                        val = pattern;
                    }
                    lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" value=\"{EscapeXml(val)}\" />");
                }
                break;
            }

            case BooleanIsNullExpression isNull:
            {
                if (isNull.Expression is ColumnReferenceExpression colRef)
                {
                    var (_, columnName) = ExtractColumnParts(colRef);
                    var op = isNull.IsNot ? "not-null" : "null";
                    var attr = NormalizeAttributeName(columnName);
                    lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" />");
                }
                break;
            }

            case InPredicate inPred:
            {
                if (inPred.Expression is ColumnReferenceExpression colRef)
                {
                    var (_, columnName) = ExtractColumnParts(colRef);
                    var op = inPred.NotDefined ? "not-in" : "in";
                    var attr = NormalizeAttributeName(columnName);
                    lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\">");
                    foreach (var value in inPred.Values)
                    {
                        if (value is Literal literal)
                        {
                            lines.Add($"{indent}  <value>{FormatLiteralValue(literal)}</value>");
                        }
                    }
                    lines.Add($"{indent}</condition>");
                }
                break;
            }

            case BooleanBinaryExpression binary:
            {
                var type = binary.BinaryExpressionType == BooleanBinaryExpressionType.Or ? "or" : "and";
                lines.Add($"{indent}<filter type=\"{type}\">");
                EmitConditionInner(binary.FirstExpression, lines, indent + "  ");
                EmitConditionInner(binary.SecondExpression, lines, indent + "  ");
                lines.Add($"{indent}</filter>");
                break;
            }

            case BooleanParenthesisExpression paren:
                EmitConditionInner(paren.Expression, lines, indent);
                break;

            case BooleanNotExpression not:
                EmitNegatedCondition(not.Expression, lines, indent);
                break;
        }
    }

    #endregion

    #region ORDER BY Emission

    private void EmitOrderBy(
        OrderByClause orderByClause,
        IList<SelectElement> selectElements,
        bool isAggregateQuery,
        List<string> lines)
    {
        foreach (var element in orderByClause.OrderByElements)
        {
            if (element.Expression is not ColumnReferenceExpression colRef)
            {
                continue;
            }

            var (_, columnName) = ExtractColumnParts(colRef);
            var descending = element.SortOrder == SortOrder.Descending ? "true" : "false";
            var orderColumnName = columnName.ToLowerInvariant();

            // In aggregate queries, check if ORDER BY column matches an alias
            if (isAggregateQuery)
            {
                var matchingAlias = FindMatchingAlias(orderColumnName, selectElements);
                if (matchingAlias != null)
                {
                    lines.Add($"    <order alias=\"{matchingAlias}\" descending=\"{descending}\" />");
                    continue;
                }
            }

            var attr = NormalizeAttributeName(columnName);
            lines.Add($"    <order attribute=\"{attr}\" descending=\"{descending}\" />");
        }
    }

    private static string? FindMatchingAlias(string columnName, IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar && scalar.ColumnName != null)
            {
                if (scalar.ColumnName.Value.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return scalar.ColumnName.Value;
                }
            }
        }
        return null;
    }

    #endregion

    #region Virtual Column Detection

    private Dictionary<string, VirtualColumnInfo> DetectVirtualColumns(IList<SelectElement> selectElements)
    {
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>(StringComparer.OrdinalIgnoreCase);

        // Build set of all column names
        var allColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar &&
                scalar.Expression is ColumnReferenceExpression colRef)
            {
                var (_, columnName) = ExtractColumnParts(colRef);
                allColumnNames.Add(NormalizeAttributeName(columnName));
            }
        }

        foreach (var element in selectElements)
        {
            if (element is not SelectScalarExpression scalar) continue;
            if (scalar.Expression is not ColumnReferenceExpression colRef) continue;

            var (_, columnName) = ExtractColumnParts(colRef);
            var normalizedName = NormalizeAttributeName(columnName);

            if (IsVirtualNameColumn(normalizedName, out var baseColumnName))
            {
                virtualColumns[normalizedName] = new VirtualColumnInfo
                {
                    BaseColumnName = baseColumnName,
                    BaseColumnExplicitlyQueried = allColumnNames.Contains(baseColumnName),
                    Alias = scalar.ColumnName?.Value
                };
            }
        }

        return virtualColumns;
    }

    private static bool IsVirtualNameColumn(string columnName, out string baseColumnName)
    {
        baseColumnName = "";

        if (columnName.Length <= 4 || !columnName.EndsWith("name", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var potentialBase = columnName[..^4];

        if (potentialBase.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.Equals("statecode", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.Equals("statuscode", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.EndsWith("code", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.EndsWith("type", StringComparison.OrdinalIgnoreCase))
        {
            baseColumnName = potentialBase;
            return true;
        }

        if (potentialBase.StartsWith("is", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.StartsWith("do", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.StartsWith("has", StringComparison.OrdinalIgnoreCase))
        {
            baseColumnName = potentialBase;
            return true;
        }

        return false;
    }

    #endregion

    #region Helper Methods

    private static (string? tableName, string columnName) ExtractColumnParts(ColumnReferenceExpression colRef)
    {
        if (colRef.ColumnType == ColumnType.Wildcard)
        {
            return (null, "*");
        }

        var identifiers = colRef.MultiPartIdentifier.Identifiers;
        if (identifiers.Count == 1)
        {
            return (null, identifiers[0].Value);
        }
        if (identifiers.Count >= 2)
        {
            return (identifiers[^2].Value, identifiers[^1].Value);
        }
        return (null, identifiers[0].Value);
    }

    private static int? GetTopValue(QuerySpecification querySpec)
    {
        if (querySpec.TopRowFilter?.Expression is IntegerLiteral intLiteral)
        {
            return int.Parse(intLiteral.Value);
        }
        return null;
    }

    private static HashSet<string> ExtractGroupByColumns(GroupByClause? groupByClause)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (groupByClause == null) return columns;

        foreach (var grouping in groupByClause.GroupingSpecifications)
        {
            if (grouping is ExpressionGroupingSpecification exprGroup &&
                exprGroup.Expression is ColumnReferenceExpression colRef)
            {
                var (_, columnName) = ExtractColumnParts(colRef);
                columns.Add(NormalizeAttributeName(columnName));
            }
        }

        return columns;
    }

    private static bool HasAggregates(IList<SelectElement> selectElements)
    {
        foreach (var element in selectElements)
        {
            if (element is SelectScalarExpression scalar &&
                scalar.Expression is FunctionCall func &&
                IsAggregateFunction(func))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsAggregateFunction(FunctionCall func)
    {
        var name = func.FunctionName.Value.ToUpperInvariant();
        return name is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX";
    }

    private static bool IsMainTable(string name, string mainTableName, string? mainTableAlias)
    {
        return string.Equals(name, mainTableName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, mainTableAlias, StringComparison.OrdinalIgnoreCase);
    }

    private static string MapComparisonOperator(BooleanComparisonType type) => type switch
    {
        BooleanComparisonType.Equals => "eq",
        BooleanComparisonType.NotEqualToBrackets => "ne",
        BooleanComparisonType.NotEqualToExclamation => "ne",
        BooleanComparisonType.LessThan => "lt",
        BooleanComparisonType.GreaterThan => "gt",
        BooleanComparisonType.LessThanOrEqualTo => "le",
        BooleanComparisonType.GreaterThanOrEqualTo => "ge",
        _ => "eq"
    };

    private static string MapAggregateFunction(string funcName, bool hasColumn) => funcName switch
    {
        "COUNT" => hasColumn ? "countcolumn" : "count",
        "SUM" => "sum",
        "AVG" => "avg",
        "MIN" => "min",
        "MAX" => "max",
        _ => "count"
    };

    private string GenerateAlias(string funcName)
    {
        _aliasCounter++;
        return $"{funcName.ToLowerInvariant()}_{_aliasCounter}";
    }

    private static string FormatLiteralValue(Literal literal)
    {
        return literal switch
        {
            NullLiteral => "",
            StringLiteral str => EscapeXml(str.Value),
            IntegerLiteral intLit => intLit.Value,
            NumericLiteral numLit => numLit.Value,
            RealLiteral realLit => realLit.Value,
            _ => EscapeXml(literal.Value ?? "")
        };
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    private static string NormalizeAttributeName(string name) => name.ToLowerInvariant();

    private static string NormalizeEntityName(string name) => name.ToLowerInvariant();

    #endregion
}
