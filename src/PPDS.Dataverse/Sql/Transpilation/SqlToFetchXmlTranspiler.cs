using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PPDS.Dataverse.Sql.Ast;

namespace PPDS.Dataverse.Sql.Transpilation;

/// <summary>
/// Converts a parsed SQL AST to FetchXML format for Dataverse execution.
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
public sealed class SqlToFetchXmlTranspiler
{
    private int _aliasCounter;
    private string _currentEntityName = "";

    /// <summary>
    /// Transpiles a SQL AST to FetchXML string.
    /// </summary>
    public string Transpile(SqlSelectStatement statement)
    {
        _aliasCounter = 0;
        _currentEntityName = NormalizeEntityName(statement.From.TableName);

        var lines = new List<string>();

        // Output leading comments first
        foreach (var comment in statement.LeadingComments)
        {
            lines.Add($"<!-- {EscapeXmlComment(comment)} -->");
        }

        var hasAggregates = statement.HasAggregates();

        // <fetch> element with optional attributes
        var fetchAttrs = new List<string>();
        if (statement.Top.HasValue)
        {
            fetchAttrs.Add($"top=\"{statement.Top.Value}\"");
        }
        if (statement.Distinct)
        {
            fetchAttrs.Add("distinct=\"true\"");
        }
        if (hasAggregates)
        {
            fetchAttrs.Add("aggregate=\"true\"");
        }

        lines.Add(fetchAttrs.Count > 0
            ? $"<fetch {string.Join(" ", fetchAttrs)}>"
            : "<fetch>");

        // <entity> element
        lines.Add($"  <entity name=\"{_currentEntityName}\">");

        // Attributes (columns) - handles both regular and aggregate columns
        TranspileColumns(statement.Columns, statement.From, statement.GroupBy, lines);

        // Link entities (JOINs)
        foreach (var join in statement.Joins)
        {
            TranspileJoin(join, statement.Columns, lines);
        }

        // Filter (WHERE)
        if (statement.Where != null)
        {
            TranspileCondition(statement.Where, lines, "    ");
        }

        // Order (ORDER BY)
        foreach (var orderItem in statement.OrderBy)
        {
            TranspileOrderBy(orderItem, statement.Columns, hasAggregates, lines);
        }

        lines.Add("  </entity>");
        lines.Add("</fetch>");

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Static convenience method to transpile SQL to FetchXML.
    /// </summary>
    public static string TranspileSql(string sql)
    {
        var statement = Parsing.SqlParser.Parse(sql);
        var transpiler = new SqlToFetchXmlTranspiler();
        return transpiler.Transpile(statement);
    }

    #region Column Transpilation

    /// <summary>
    /// Transpiles SELECT columns to FetchXML attributes.
    /// </summary>
    private void TranspileColumns(
        IReadOnlyList<ISqlSelectColumn> columns,
        SqlTableRef mainEntity,
        IReadOnlyList<SqlColumnRef> groupBy,
        List<string> lines)
    {
        var groupByColumns = new HashSet<string>(
            groupBy.Select(col => NormalizeAttributeName(col.ColumnName)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            switch (column)
            {
                case SqlAggregateColumn aggregate:
                    TranspileAggregateColumn(aggregate, lines);
                    break;
                case SqlColumnRef columnRef:
                    TranspileRegularColumn(columnRef, mainEntity, groupByColumns, lines);
                    break;
            }
        }

        // Emit GROUP BY columns that aren't already in SELECT
        foreach (var groupByCol in groupBy)
        {
            var attrName = NormalizeAttributeName(groupByCol.ColumnName);
            var isInSelect = columns.Any(col =>
                col is SqlColumnRef cr && NormalizeAttributeName(cr.ColumnName) == attrName);

            if (!isInSelect)
            {
                lines.Add($"    <attribute name=\"{attrName}\" groupby=\"true\" />");
                OutputTrailingComment(groupByCol.TrailingComment, lines, "    ");
            }
            else
            {
                OutputTrailingComment(groupByCol.TrailingComment, lines, "    ");
            }
        }
    }

    /// <summary>
    /// Transpiles a regular column reference to FetchXML attribute.
    /// </summary>
    private void TranspileRegularColumn(
        SqlColumnRef column,
        SqlTableRef mainEntity,
        HashSet<string> groupByColumns,
        List<string> lines)
    {
        if (column.IsWildcard && column.TableName == null)
        {
            // SELECT *
            lines.Add("    <all-attributes />");
            OutputTrailingComment(column.TrailingComment, lines, "    ");
        }
        else if (column.IsWildcard && IsMainEntityColumn(column.TableName, mainEntity))
        {
            // SELECT c.* where c is main entity alias
            lines.Add("    <all-attributes />");
            OutputTrailingComment(column.TrailingComment, lines, "    ");
        }
        else if (!column.IsWildcard)
        {
            if (IsMainEntityColumn(column.TableName, mainEntity))
            {
                var attrName = NormalizeAttributeName(column.ColumnName);
                var isGroupBy = groupByColumns.Contains(attrName);
                var attrs = new List<string> { $"name=\"{attrName}\"" };

                if (column.Alias != null)
                {
                    attrs.Add($"alias=\"{column.Alias}\"");
                }
                if (isGroupBy)
                {
                    attrs.Add("groupby=\"true\"");
                }

                lines.Add($"    <attribute {string.Join(" ", attrs)} />");
                OutputTrailingComment(column.TrailingComment, lines, "    ");
            }
        }
    }

    /// <summary>
    /// Transpiles an aggregate column to FetchXML attribute.
    /// </summary>
    private void TranspileAggregateColumn(SqlAggregateColumn column, List<string> lines)
    {
        var attrs = new List<string>();

        if (column.IsCountAll)
        {
            // COUNT(*) - FetchXML requires an actual attribute name
            var primaryKeyColumn = $"{_currentEntityName}id";
            attrs.Add($"name=\"{primaryKeyColumn}\"");
            attrs.Add("aggregate=\"count\"");
        }
        else
        {
            var columnName = column.GetColumnName();
            if (columnName != null)
            {
                attrs.Add($"name=\"{NormalizeAttributeName(columnName)}\"");
            }

            var aggregateType = MapAggregateFunction(column.Function, column.Column != null);
            attrs.Add($"aggregate=\"{aggregateType}\"");

            if (column.IsDistinct)
            {
                attrs.Add("distinct=\"true\"");
            }
        }

        // Alias is required for aggregates in FetchXML
        var alias = column.Alias ?? GenerateAlias(column.Function);
        attrs.Add($"alias=\"{alias}\"");

        lines.Add($"    <attribute {string.Join(" ", attrs)} />");
        OutputTrailingComment(column.TrailingComment, lines, "    ");
    }

    /// <summary>
    /// Maps SQL aggregate function to FetchXML aggregate type.
    /// </summary>
    private static string MapAggregateFunction(SqlAggregateFunction func, bool hasColumn) => func switch
    {
        SqlAggregateFunction.Count => hasColumn ? "countcolumn" : "count",
        SqlAggregateFunction.Sum => "sum",
        SqlAggregateFunction.Avg => "avg",
        SqlAggregateFunction.Min => "min",
        SqlAggregateFunction.Max => "max",
        _ => "count"
    };

    /// <summary>
    /// Generates a unique alias for aggregate columns without one.
    /// </summary>
    private string GenerateAlias(SqlAggregateFunction func)
    {
        _aliasCounter++;
        return $"{func.ToString().ToLowerInvariant()}_{_aliasCounter}";
    }

    #endregion

    #region Join Transpilation

    /// <summary>
    /// Transpiles a JOIN to FetchXML link-entity.
    /// </summary>
    private void TranspileJoin(SqlJoin join, IReadOnlyList<ISqlSelectColumn> columns, List<string> lines)
    {
        var linkType = join.Type.ToFetchXmlLinkType();

        // Determine which column is from the link-entity vs parent entity
        string fromColumn, toColumn;

        var leftIsLinkEntity = IsJoinTableColumn(join.LeftColumn.TableName, join.Table);
        var rightIsLinkEntity = IsJoinTableColumn(join.RightColumn.TableName, join.Table);

        if (leftIsLinkEntity && !rightIsLinkEntity)
        {
            fromColumn = join.LeftColumn.ColumnName;
            toColumn = join.RightColumn.ColumnName;
        }
        else if (rightIsLinkEntity && !leftIsLinkEntity)
        {
            fromColumn = join.RightColumn.ColumnName;
            toColumn = join.LeftColumn.ColumnName;
        }
        else
        {
            // Fallback
            fromColumn = join.RightColumn.ColumnName;
            toColumn = join.LeftColumn.ColumnName;
        }

        var from = NormalizeAttributeName(fromColumn);
        var to = NormalizeAttributeName(toColumn);
        var aliasAttr = join.Table.Alias != null ? $" alias=\"{join.Table.Alias}\"" : "";
        var linkEntityName = NormalizeEntityName(join.Table.TableName);

        lines.Add($"    <link-entity name=\"{linkEntityName}\" from=\"{from}\" to=\"{to}\" link-type=\"{linkType}\"{aliasAttr}>");

        // Add columns that belong to this link-entity
        foreach (var column in columns)
        {
            if (column is SqlColumnRef cr && IsJoinTableColumn(cr.TableName, join.Table))
            {
                if (cr.IsWildcard)
                {
                    lines.Add("      <all-attributes />");
                }
                else
                {
                    var attrName = NormalizeAttributeName(cr.ColumnName);
                    if (cr.Alias != null)
                    {
                        lines.Add($"      <attribute name=\"{attrName}\" alias=\"{cr.Alias}\" />");
                    }
                    else
                    {
                        lines.Add($"      <attribute name=\"{attrName}\" />");
                    }
                }
            }
        }

        lines.Add("    </link-entity>");
    }

    #endregion

    #region Condition Transpilation

    /// <summary>
    /// Transpiles a WHERE condition to FetchXML filter.
    /// </summary>
    private void TranspileCondition(ISqlCondition condition, List<string> lines, string indent)
    {
        switch (condition)
        {
            case SqlComparisonCondition comp:
                TranspileComparison(comp, lines, indent);
                break;
            case SqlLikeCondition like:
                TranspileLike(like, lines, indent);
                break;
            case SqlNullCondition nullCond:
                TranspileNull(nullCond, lines, indent);
                break;
            case SqlInCondition inCond:
                TranspileIn(inCond, lines, indent);
                break;
            case SqlLogicalCondition logical:
                TranspileLogical(logical, lines, indent);
                break;
        }
    }

    private void TranspileComparison(SqlComparisonCondition condition, List<string> lines, string indent)
    {
        var op = condition.Operator.ToFetchXmlOperator();
        var value = FormatValue(condition.Value);
        var attr = NormalizeAttributeName(condition.Column.ColumnName);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" value=\"{value}\" />");
        lines.Add($"{indent}</filter>");
        OutputTrailingComment(condition.TrailingComment, lines, indent);
    }

    private void TranspileLike(SqlLikeCondition condition, List<string> lines, string indent)
    {
        var pattern = condition.Pattern;
        var attr = NormalizeAttributeName(condition.Column.ColumnName);

        string op, value;

        if (pattern.StartsWith('%') && pattern.EndsWith('%'))
        {
            op = condition.IsNegated ? "not-like" : "like";
            value = pattern;
        }
        else if (pattern.StartsWith('%'))
        {
            op = condition.IsNegated ? "not-end-with" : "ends-with";
            value = pattern[1..];
        }
        else if (pattern.EndsWith('%'))
        {
            op = condition.IsNegated ? "not-begin-with" : "begins-with";
            value = pattern[..^1];
        }
        else
        {
            op = condition.IsNegated ? "not-like" : "like";
            value = pattern;
        }

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" value=\"{EscapeXml(value)}\" />");
        lines.Add($"{indent}</filter>");
        OutputTrailingComment(condition.TrailingComment, lines, indent);
    }

    private void TranspileNull(SqlNullCondition condition, List<string> lines, string indent)
    {
        var op = condition.IsNegated ? "not-null" : "null";
        var attr = NormalizeAttributeName(condition.Column.ColumnName);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" />");
        lines.Add($"{indent}</filter>");
        OutputTrailingComment(condition.TrailingComment, lines, indent);
    }

    private void TranspileIn(SqlInCondition condition, List<string> lines, string indent)
    {
        var op = condition.IsNegated ? "not-in" : "in";
        var attr = NormalizeAttributeName(condition.Column.ColumnName);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\">");

        foreach (var value in condition.Values)
        {
            lines.Add($"{indent}    <value>{FormatValue(value)}</value>");
        }

        lines.Add($"{indent}  </condition>");
        lines.Add($"{indent}</filter>");
        OutputTrailingComment(condition.TrailingComment, lines, indent);
    }

    private void TranspileLogical(SqlLogicalCondition condition, List<string> lines, string indent)
    {
        var filterType = condition.Operator == SqlLogicalOperator.Or ? "or" : "and";

        lines.Add($"{indent}<filter type=\"{filterType}\">");

        foreach (var child in condition.Conditions)
        {
            TranspileConditionInner(child, lines, indent + "  ");
        }

        lines.Add($"{indent}</filter>");
    }

    /// <summary>
    /// Transpiles a condition without wrapping filter (for nested conditions).
    /// </summary>
    private void TranspileConditionInner(ISqlCondition condition, List<string> lines, string indent)
    {
        switch (condition)
        {
            case SqlComparisonCondition comp:
            {
                var op = comp.Operator.ToFetchXmlOperator();
                var value = FormatValue(comp.Value);
                var attr = NormalizeAttributeName(comp.Column.ColumnName);
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" value=\"{value}\" />");
                break;
            }
            case SqlLikeCondition like:
            {
                var pattern = like.Pattern;
                var attr = NormalizeAttributeName(like.Column.ColumnName);
                string op, val;

                if (pattern.StartsWith('%') && pattern.EndsWith('%'))
                {
                    op = like.IsNegated ? "not-like" : "like";
                    val = pattern;
                }
                else if (pattern.StartsWith('%'))
                {
                    op = like.IsNegated ? "not-end-with" : "ends-with";
                    val = pattern[1..];
                }
                else if (pattern.EndsWith('%'))
                {
                    op = like.IsNegated ? "not-begin-with" : "begins-with";
                    val = pattern[..^1];
                }
                else
                {
                    op = like.IsNegated ? "not-like" : "like";
                    val = pattern;
                }
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" value=\"{EscapeXml(val)}\" />");
                break;
            }
            case SqlNullCondition nullCond:
            {
                var op = nullCond.IsNegated ? "not-null" : "null";
                var attr = NormalizeAttributeName(nullCond.Column.ColumnName);
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" />");
                break;
            }
            case SqlInCondition inCond:
            {
                var op = inCond.IsNegated ? "not-in" : "in";
                var attr = NormalizeAttributeName(inCond.Column.ColumnName);
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\">");
                foreach (var v in inCond.Values)
                {
                    lines.Add($"{indent}  <value>{FormatValue(v)}</value>");
                }
                lines.Add($"{indent}</condition>");
                break;
            }
            case SqlLogicalCondition logical:
            {
                var type = logical.Operator == SqlLogicalOperator.Or ? "or" : "and";
                lines.Add($"{indent}<filter type=\"{type}\">");
                foreach (var child in logical.Conditions)
                {
                    TranspileConditionInner(child, lines, indent + "  ");
                }
                lines.Add($"{indent}</filter>");
                break;
            }
        }
    }

    #endregion

    #region Order By Transpilation

    /// <summary>
    /// Transpiles an ORDER BY item to FetchXML order element.
    /// </summary>
    private void TranspileOrderBy(
        SqlOrderByItem orderItem,
        IReadOnlyList<ISqlSelectColumn> columns,
        bool isAggregateQuery,
        List<string> lines)
    {
        var descending = orderItem.Direction == SqlSortDirection.Descending ? "true" : "false";
        var orderColumnName = orderItem.Column.ColumnName.ToLowerInvariant();

        // In aggregate queries, check if ORDER BY column matches an alias
        if (isAggregateQuery)
        {
            var matchingAlias = FindMatchingAlias(orderColumnName, columns);
            if (matchingAlias != null)
            {
                lines.Add($"    <order alias=\"{matchingAlias}\" descending=\"{descending}\" />");
                OutputTrailingComment(orderItem.TrailingComment, lines, "    ");
                return;
            }
        }

        var attr = NormalizeAttributeName(orderItem.Column.ColumnName);
        lines.Add($"    <order attribute=\"{attr}\" descending=\"{descending}\" />");
        OutputTrailingComment(orderItem.TrailingComment, lines, "    ");
    }

    /// <summary>
    /// Finds a matching alias from columns for the given column name.
    /// </summary>
    private static string? FindMatchingAlias(string columnName, IReadOnlyList<ISqlSelectColumn> columns)
    {
        foreach (var column in columns)
        {
            var alias = column.Alias;
            if (alias != null && alias.Equals(columnName, StringComparison.OrdinalIgnoreCase))
            {
                return alias;
            }
        }
        return null;
    }

    #endregion

    #region Helper Methods

    private bool IsMainEntityColumn(string? tableName, SqlTableRef mainEntity)
    {
        if (tableName == null) return true;
        if (mainEntity.Alias != null &&
            tableName.Equals(mainEntity.Alias, StringComparison.OrdinalIgnoreCase))
            return true;
        return tableName.Equals(mainEntity.TableName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsJoinTableColumn(string? tableName, SqlTableRef joinTable)
    {
        if (tableName == null) return false;
        if (joinTable.Alias != null &&
            tableName.Equals(joinTable.Alias, StringComparison.OrdinalIgnoreCase))
            return true;
        return tableName.Equals(joinTable.TableName, StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatValue(SqlLiteral literal)
    {
        if (literal.Type == SqlLiteralType.Null) return "";
        if (literal.Type == SqlLiteralType.String) return EscapeXml(literal.Value ?? "");
        return literal.Value ?? "";
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

    private static string EscapeXmlComment(string text) => text.Replace("--", "- -");

    private static void OutputTrailingComment(string? comment, List<string> lines, string indent)
    {
        if (!string.IsNullOrEmpty(comment))
        {
            lines.Add($"{indent}<!-- {EscapeXmlComment(comment)} -->");
        }
    }

    #endregion
}
