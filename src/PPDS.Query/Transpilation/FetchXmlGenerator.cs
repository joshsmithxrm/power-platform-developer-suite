using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PPDS.Query.Transpilation;

/// <summary>
/// Generates FetchXML from a ScriptDom <see cref="TSqlFragment"/> AST.
/// Accepts <see cref="SelectStatement"/> or <see cref="QuerySpecification"/> nodes
/// and produces FetchXML equivalent to what Dataverse expects.
/// </summary>
/// <remarks>
/// Business rules:
/// <list type="bullet">
///   <item>SQL comparison operators map to FetchXML operators (eq, ne, gt, ge, lt, le)</item>
///   <item>LIKE patterns with wildcards map to like, begins-with, ends-with</item>
///   <item>JOINs map to link-entity elements</item>
///   <item>AND/OR map to filter type attribute</item>
///   <item>String values are XML-escaped</item>
///   <item>Entity/attribute names are normalized to lowercase</item>
///   <item>Virtual *name columns emit their base column instead</item>
/// </list>
/// </remarks>
public sealed class FetchXmlGenerator
{
    private int _aliasCounter;
    private string _primaryEntityName = "";

    /// <summary>
    /// Generates FetchXML from a ScriptDom AST node.
    /// </summary>
    /// <param name="fragment">A <see cref="SelectStatement"/> or <see cref="QuerySpecification"/>.</param>
    /// <returns>The FetchXML string.</returns>
    public string Generate(TSqlFragment fragment)
    {
        return GenerateWithVirtualColumns(fragment).FetchXml;
    }

    /// <summary>
    /// Generates FetchXML from a ScriptDom AST node, also returning virtual column metadata.
    /// </summary>
    /// <param name="fragment">A <see cref="SelectStatement"/> or <see cref="QuerySpecification"/>.</param>
    /// <returns>A <see cref="TranspileResult"/> containing FetchXML and virtual column info.</returns>
    public TranspileResult GenerateWithVirtualColumns(TSqlFragment fragment)
    {
        var querySpec = ExtractQuerySpecification(fragment);
        if (querySpec is null)
        {
            throw new ArgumentException(
                "Expected a SelectStatement or QuerySpecification node.", nameof(fragment));
        }

        _aliasCounter = 0;

        // Resolve the primary entity from the FROM clause
        var fromTable = ResolveFromTable(querySpec.FromClause);
        _primaryEntityName = NormalizeName(fromTable.tableName);
        var fromAlias = fromTable.alias;

        // Collect joined tables for column routing
        var joins = CollectJoins(querySpec.FromClause);

        // Detect virtual *name columns
        var selectElements = querySpec.SelectElements;
        var virtualColumns = DetectVirtualColumns(selectElements, fromTable, joins);

        // Detect aggregates
        var hasAggregates = HasAggregateColumns(selectElements);

        // Detect GROUP BY simple columns and date-grouping expressions
        var groupByColumns = CollectGroupByColumns(querySpec.GroupByClause);
        var groupByDateFunctions = CollectGroupByDateFunctions(querySpec.GroupByClause);

        var lines = new List<string>();

        // <fetch> element with optional attributes
        var fetchAttrs = new List<string>();

        if (querySpec.TopRowFilter is not null)
        {
            var topValue = ExtractTopValue(querySpec.TopRowFilter);
            if (topValue is not null)
            {
                fetchAttrs.Add($"top=\"{topValue}\"");
            }
        }

        if (querySpec.UniqueRowFilter == UniqueRowFilter.Distinct)
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
        lines.Add($"  <entity name=\"{_primaryEntityName}\">");

        // Attributes (columns)
        var groupBySet = new HashSet<string>(groupByColumns, StringComparer.OrdinalIgnoreCase);
        var emittedBaseColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EmitSelectColumns(selectElements, fromTable, joins, groupBySet, virtualColumns,
            emittedBaseColumns, hasAggregates, lines);

        // Emit GROUP BY columns not already in SELECT
        EmitMissingGroupByColumns(groupByColumns, selectElements, fromTable, joins, lines);

        // Emit date-grouping attributes for GROUP BY YEAR/MONTH/DAY expressions
        EmitDateGroupingAttributes(groupByDateFunctions, selectElements, lines);

        // Emit columns needed for expression conditions (client-side evaluation)
        EmitExpressionConditionColumns(querySpec.WhereClause, lines);

        // Link entities (JOINs)
        foreach (var join in joins)
        {
            EmitLinkEntity(join, selectElements, lines);
        }

        // Filter (WHERE)
        if (querySpec.WhereClause is not null)
        {
            EmitFilter(querySpec.WhereClause.SearchCondition, lines, "    ");
        }

        // Order (ORDER BY)
        if (querySpec.OrderByClause is not null)
        {
            foreach (var orderElement in querySpec.OrderByClause.OrderByElements)
            {
                EmitOrderBy(orderElement, selectElements, hasAggregates, lines);
            }
        }

        lines.Add("  </entity>");
        lines.Add("</fetch>");

        return new TranspileResult
        {
            FetchXml = string.Join("\n", lines),
            VirtualColumns = virtualColumns
        };
    }

    #region AST Extraction

    /// <summary>
    /// Extracts a <see cref="QuerySpecification"/> from a <see cref="SelectStatement"/>
    /// or returns the node directly if it already is one.
    /// </summary>
    private static QuerySpecification? ExtractQuerySpecification(TSqlFragment fragment)
    {
        if (fragment is QuerySpecification qs)
        {
            return qs;
        }

        if (fragment is SelectStatement selectStatement)
        {
            return selectStatement.QueryExpression as QuerySpecification;
        }

        return null;
    }

    /// <summary>
    /// Resolves the primary table from the FROM clause, handling aliasing.
    /// </summary>
    private static (string tableName, string? alias) ResolveFromTable(FromClause? fromClause)
    {
        if (fromClause is null || fromClause.TableReferences.Count == 0)
        {
            throw new InvalidOperationException("SELECT statement must have a FROM clause.");
        }

        var tableRef = fromClause.TableReferences[0];

        // If it is a qualified join, drill into the first table
        if (tableRef is QualifiedJoin qj)
        {
            tableRef = qj.FirstTableReference;
            while (tableRef is QualifiedJoin nested)
            {
                tableRef = nested.FirstTableReference;
            }
        }

        if (tableRef is NamedTableReference named)
        {
            var tableName = ExtractTableName(named.SchemaObject);
            var alias = named.Alias?.Value;
            return (tableName, alias);
        }

        throw new InvalidOperationException("Could not resolve primary table from FROM clause.");
    }

    /// <summary>
    /// Extracts a simple table name from a <see cref="SchemaObjectName"/>.
    /// Uses the last identifier (the table name part).
    /// </summary>
    private static string ExtractTableName(SchemaObjectName schemaObject)
    {
        // SchemaObjectName identifiers: [Server].[Database].[Schema].[BaseIdentifier]
        // We want the last one (the actual table name).
        return schemaObject.BaseIdentifier.Value;
    }

    /// <summary>
    /// Extracts the integer value from a TOP row filter expression.
    /// </summary>
    private static int? ExtractTopValue(TopRowFilter topRowFilter)
    {
        if (topRowFilter.Expression is IntegerLiteral intLiteral)
        {
            if (int.TryParse(intLiteral.Value, out var value))
            {
                return value;
            }
        }

        return null;
    }

    #endregion

    #region Join Collection

    /// <summary>
    /// Holds information about a parsed JOIN.
    /// </summary>
    private sealed class JoinInfo
    {
        public required string TableName { get; init; }
        public required string? Alias { get; init; }
        public required string FromColumn { get; init; }
        public required string ToColumn { get; init; }
        public required string LinkType { get; init; }
    }

    /// <summary>
    /// Collects all JOINs from the FROM clause.
    /// </summary>
    private static List<JoinInfo> CollectJoins(FromClause? fromClause)
    {
        var joins = new List<JoinInfo>();
        if (fromClause is null) return joins;

        foreach (var tableRef in fromClause.TableReferences)
        {
            CollectJoinsRecursive(tableRef, joins);
        }

        return joins;
    }

    /// <summary>
    /// Recursively walks the table reference tree to find QualifiedJoin nodes.
    /// </summary>
    private static void CollectJoinsRecursive(TableReference tableRef, List<JoinInfo> joins)
    {
        if (tableRef is QualifiedJoin qualifiedJoin)
        {
            // Recurse into the left side first (handles chained joins)
            CollectJoinsRecursive(qualifiedJoin.FirstTableReference, joins);

            // Process this join - the second table reference is the joined table
            if (qualifiedJoin.SecondTableReference is NamedTableReference joinedTable)
            {
                var joinTableName = ExtractTableName(joinedTable.SchemaObject);
                var joinAlias = joinedTable.Alias?.Value;
                var linkType = MapJoinType(qualifiedJoin.QualifiedJoinType);

                // Extract ON condition columns
                var (fromCol, toCol) = ExtractJoinColumns(
                    qualifiedJoin.SearchCondition, joinTableName, joinAlias);

                joins.Add(new JoinInfo
                {
                    TableName = joinTableName,
                    Alias = joinAlias,
                    FromColumn = fromCol,
                    ToColumn = toCol,
                    LinkType = linkType
                });
            }

            // Recurse into the right side (for nested joins on the right)
            if (qualifiedJoin.SecondTableReference is QualifiedJoin)
            {
                CollectJoinsRecursive(qualifiedJoin.SecondTableReference, joins);
            }
        }
        // NamedTableReference at the root level is the primary table, not a join - skip it.
    }

    /// <summary>
    /// Maps ScriptDom join type to FetchXML link-type string.
    /// </summary>
    private static string MapJoinType(QualifiedJoinType joinType) => joinType switch
    {
        QualifiedJoinType.Inner => "inner",
        QualifiedJoinType.LeftOuter => "outer",
        QualifiedJoinType.RightOuter => "outer",
        QualifiedJoinType.FullOuter => "outer",
        _ => "inner"
    };

    /// <summary>
    /// Extracts the from/to columns from a join ON condition.
    /// The "from" column is the one belonging to the joined table; "to" is the parent.
    /// </summary>
    private static (string fromColumn, string toColumn) ExtractJoinColumns(
        BooleanExpression? searchCondition,
        string joinTableName,
        string? joinAlias)
    {
        if (searchCondition is BooleanComparisonExpression comparison)
        {
            var leftCol = ExtractColumnReference(comparison.FirstExpression);
            var rightCol = ExtractColumnReference(comparison.SecondExpression);

            if (leftCol is not null && rightCol is not null)
            {
                var leftIsJoinTable = IsColumnFromTable(leftCol, joinTableName, joinAlias);
                var rightIsJoinTable = IsColumnFromTable(rightCol, joinTableName, joinAlias);

                if (leftIsJoinTable && !rightIsJoinTable)
                {
                    return (GetColumnName(leftCol), GetColumnName(rightCol));
                }
                if (rightIsJoinTable && !leftIsJoinTable)
                {
                    return (GetColumnName(rightCol), GetColumnName(leftCol));
                }

                // Fallback: right is from, left is to
                return (GetColumnName(rightCol), GetColumnName(leftCol));
            }
        }

        return ("", "");
    }

    /// <summary>
    /// Checks if a column reference belongs to the specified table.
    /// </summary>
    private static bool IsColumnFromTable(
        ColumnReferenceExpression column, string tableName, string? alias)
    {
        var parts = column.MultiPartIdentifier?.Identifiers;
        if (parts is null || parts.Count < 2) return false;

        var tableQualifier = parts[parts.Count - 2].Value;

        if (alias is not null && tableQualifier.Equals(alias, StringComparison.OrdinalIgnoreCase))
            return true;

        return tableQualifier.Equals(tableName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets the unqualified column name from a column reference.
    /// </summary>
    private static string GetColumnName(ColumnReferenceExpression column)
    {
        var parts = column.MultiPartIdentifier?.Identifiers;
        if (parts is null || parts.Count == 0) return "";
        return parts[parts.Count - 1].Value;
    }

    /// <summary>
    /// Gets the table qualifier from a column reference, if present.
    /// </summary>
    private static string? GetTableQualifier(ColumnReferenceExpression column)
    {
        var parts = column.MultiPartIdentifier?.Identifiers;
        if (parts is null || parts.Count < 2) return null;
        return parts[parts.Count - 2].Value;
    }

    /// <summary>
    /// Extracts a <see cref="ColumnReferenceExpression"/> from a scalar expression.
    /// </summary>
    private static ColumnReferenceExpression? ExtractColumnReference(ScalarExpression expression)
    {
        if (expression is ColumnReferenceExpression colRef)
            return colRef;

        return null;
    }

    #endregion

    #region Virtual Column Detection

    /// <summary>
    /// Detects virtual *name columns in the SELECT list and returns metadata about them.
    /// </summary>
    private Dictionary<string, VirtualColumnInfo> DetectVirtualColumns(
        IList<SelectElement> selectElements,
        (string tableName, string? alias) fromTable,
        List<JoinInfo> joins)
    {
        var virtualColumns = new Dictionary<string, VirtualColumnInfo>(StringComparer.OrdinalIgnoreCase);

        // Build a set of all column names for checking if base column is explicitly queried
        var allColumnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var elem in selectElements)
        {
            if (elem is SelectScalarExpression scalar
                && scalar.Expression is ColumnReferenceExpression colRef
                && colRef.ColumnType != ColumnType.Wildcard)
            {
                allColumnNames.Add(NormalizeName(GetColumnName(colRef)));
            }
        }

        foreach (var elem in selectElements)
        {
            if (elem is not SelectScalarExpression scalar) continue;
            if (scalar.Expression is not ColumnReferenceExpression colRef) continue;
            if (colRef.ColumnType == ColumnType.Wildcard) continue;

            // Only process columns belonging to the main entity
            var tableQualifier = GetTableQualifier(colRef);
            if (!IsMainEntityQualifier(tableQualifier, fromTable, joins)) continue;

            var columnName = NormalizeName(GetColumnName(colRef));
            if (IsVirtualNameColumn(columnName, out var baseColumnName))
            {
                var alias = scalar.ColumnName?.Value;
                virtualColumns[columnName] = new VirtualColumnInfo
                {
                    BaseColumnName = baseColumnName,
                    BaseColumnExplicitlyQueried = allColumnNames.Contains(baseColumnName),
                    Alias = alias
                };
            }
        }

        return virtualColumns;
    }

    /// <summary>
    /// Checks if a column name is a virtual *name column and extracts the base column name.
    /// Matches patterns like: owneridname, statecodename, statuscodename, ismanagedname.
    /// </summary>
    private static bool IsVirtualNameColumn(string columnName, out string baseColumnName)
    {
        baseColumnName = "";

        // Must end with "name" and have at least one character before it
        if (columnName.Length <= 4 ||
            !columnName.EndsWith("name", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Extract potential base column name (everything before "name" suffix)
        var potentialBase = columnName[..^4];

        // Check if base column looks like a lookup (ends with "id")
        // or optionset/state/status/type pattern
        if (potentialBase.EndsWith("id", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.Equals("statecode", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.Equals("statuscode", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.EndsWith("code", StringComparison.OrdinalIgnoreCase) ||
            potentialBase.EndsWith("type", StringComparison.OrdinalIgnoreCase))
        {
            baseColumnName = potentialBase;
            return true;
        }

        // Check for boolean patterns (ismanaged, isdisabled, etc.)
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

    #region Aggregate Detection

    /// <summary>
    /// Checks if any select element contains an aggregate function call.
    /// </summary>
    private static bool HasAggregateColumns(IList<SelectElement> selectElements)
    {
        foreach (var elem in selectElements)
        {
            if (elem is SelectScalarExpression scalar && IsAggregateFunctionCall(scalar.Expression))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if an expression is an aggregate function call (COUNT, SUM, AVG, MIN, MAX).
    /// </summary>
    private static bool IsAggregateFunctionCall(ScalarExpression expression)
    {
        return expression is FunctionCall fc && IsAggregateFunction(fc.FunctionName?.Value);
    }

    /// <summary>
    /// Checks if a function name is a recognized aggregate function.
    /// </summary>
    private static bool IsAggregateFunction(string? functionName)
    {
        if (functionName is null) return false;
        return functionName.Equals("COUNT", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("SUM", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("AVG", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("MIN", StringComparison.OrdinalIgnoreCase)
            || functionName.Equals("MAX", StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region GROUP BY Collection

    /// <summary>
    /// Collects simple column names from the GROUP BY clause.
    /// </summary>
    private static List<string> CollectGroupByColumns(GroupByClause? groupByClause)
    {
        var columns = new List<string>();
        if (groupByClause is null) return columns;

        foreach (var groupingSpec in groupByClause.GroupingSpecifications)
        {
            if (groupingSpec is ExpressionGroupingSpecification exprSpec
                && exprSpec.Expression is ColumnReferenceExpression colRef)
            {
                columns.Add(NormalizeName(GetColumnName(colRef)));
            }
        }

        return columns;
    }

    /// <summary>
    /// Collects date-grouping function calls from the GROUP BY clause.
    /// Returns tuples of (functionName, columnName) for YEAR/MONTH/DAY/QUARTER/WEEK functions.
    /// </summary>
    private static List<(string functionName, string columnName)> CollectGroupByDateFunctions(
        GroupByClause? groupByClause)
    {
        var dateFunctions = new List<(string functionName, string columnName)>();
        if (groupByClause is null) return dateFunctions;

        foreach (var groupingSpec in groupByClause.GroupingSpecifications)
        {
            if (groupingSpec is not ExpressionGroupingSpecification exprSpec) continue;
            if (exprSpec.Expression is not FunctionCall funcCall) continue;

            var funcName = funcCall.FunctionName?.Value;
            if (funcName is null) continue;

            var dategrouping = funcName.ToUpperInvariant() switch
            {
                "YEAR" => "year",
                "MONTH" => "month",
                "DAY" => "day",
                "QUARTER" => "quarter",
                "WEEK" => "week",
                _ => (string?)null
            };

            if (dategrouping is null) continue;
            if (funcCall.Parameters.Count != 1) continue;

            if (funcCall.Parameters[0] is ColumnReferenceExpression colRef)
            {
                var columnName = GetColumnName(colRef);
                dateFunctions.Add((funcName, columnName));
            }
        }

        return dateFunctions;
    }

    #endregion

    #region Column Emission

    /// <summary>
    /// Emits FetchXML attribute elements for SELECT columns.
    /// </summary>
    private void EmitSelectColumns(
        IList<SelectElement> selectElements,
        (string tableName, string? alias) fromTable,
        List<JoinInfo> joins,
        HashSet<string> groupBySet,
        Dictionary<string, VirtualColumnInfo> virtualColumns,
        HashSet<string> emittedBaseColumns,
        bool hasAggregates,
        List<string> lines)
    {
        foreach (var elem in selectElements)
        {
            switch (elem)
            {
                case SelectStarExpression star:
                    EmitWildcard(star, fromTable, joins, lines);
                    break;

                case SelectScalarExpression scalar:
                    EmitScalarSelectElement(scalar, fromTable, joins, groupBySet,
                        virtualColumns, emittedBaseColumns, lines);
                    break;
            }
        }
    }

    /// <summary>
    /// Emits a wildcard (SELECT * or SELECT t.*) as an all-attributes element.
    /// </summary>
    private void EmitWildcard(
        SelectStarExpression star,
        (string tableName, string? alias) fromTable,
        List<JoinInfo> joins,
        List<string> lines)
    {
        // Unqualified * always maps to main entity
        if (star.Qualifier is null)
        {
            lines.Add("    <all-attributes />");
            return;
        }

        var qualifier = GetLastIdentifier(star.Qualifier);

        // Check if qualifier matches the main entity
        if (IsMainEntityQualifier(qualifier, fromTable, joins))
        {
            lines.Add("    <all-attributes />");
        }
        // Qualified wildcard for join tables is handled inside EmitLinkEntity
    }

    /// <summary>
    /// Emits a scalar select element (column, aggregate, or computed expression).
    /// </summary>
    private void EmitScalarSelectElement(
        SelectScalarExpression scalar,
        (string tableName, string? alias) fromTable,
        List<JoinInfo> joins,
        HashSet<string> groupBySet,
        Dictionary<string, VirtualColumnInfo> virtualColumns,
        HashSet<string> emittedBaseColumns,
        List<string> lines)
    {
        var expression = scalar.Expression;

        // Aggregate function call
        if (expression is FunctionCall funcCall && IsAggregateFunction(funcCall.FunctionName?.Value))
        {
            EmitAggregateColumn(funcCall, scalar.ColumnName?.Value, lines);
            return;
        }

        // Regular column reference
        if (expression is ColumnReferenceExpression colRef)
        {
            if (colRef.ColumnType == ColumnType.Wildcard)
            {
                // This case (ColumnType.Wildcard) can appear as SELECT *
                lines.Add("    <all-attributes />");
                return;
            }

            var tableQualifier = GetTableQualifier(colRef);

            // Skip columns belonging to joined tables (they are emitted inside link-entity)
            if (!IsMainEntityQualifier(tableQualifier, fromTable, joins))
            {
                return;
            }

            var columnName = NormalizeName(GetColumnName(colRef));
            var alias = scalar.ColumnName?.Value;

            // Check if this is a virtual column
            if (virtualColumns.TryGetValue(columnName, out var virtualInfo))
            {
                // For virtual columns, emit the base column instead (if not already emitted)
                if (!emittedBaseColumns.Contains(virtualInfo.BaseColumnName))
                {
                    var attrs = new List<string> { $"name=\"{virtualInfo.BaseColumnName}\"" };
                    if (groupBySet.Contains(virtualInfo.BaseColumnName))
                    {
                        attrs.Add("groupby=\"true\"");
                    }
                    lines.Add($"    <attribute {string.Join(" ", attrs)} />");
                    emittedBaseColumns.Add(virtualInfo.BaseColumnName);
                }
                return;
            }

            // Regular column
            var attrsList = new List<string> { $"name=\"{columnName}\"" };
            if (alias is not null)
            {
                attrsList.Add($"alias=\"{alias}\"");
            }
            if (groupBySet.Contains(columnName))
            {
                attrsList.Add("groupby=\"true\"");
            }

            emittedBaseColumns.Add(columnName);
            lines.Add($"    <attribute {string.Join(" ", attrsList)} />");
            return;
        }

        // Computed expression (CASE, IIF, arithmetic, etc.) - emit referenced columns
        EmitReferencedColumnsFromExpression(expression, emittedBaseColumns, lines);
    }

    /// <summary>
    /// Emits a FetchXML aggregate attribute.
    /// </summary>
    private void EmitAggregateColumn(FunctionCall funcCall, string? explicitAlias, List<string> lines)
    {
        var funcName = funcCall.FunctionName.Value.ToUpperInvariant();
        var attrs = new List<string>();

        // Check for COUNT(*) - indicated by CallTarget or by having no parameters
        var isCountStar = funcName == "COUNT"
            && (funcCall.Parameters.Count == 0
                || (funcCall.Parameters.Count == 1
                    && funcCall.Parameters[0] is ColumnReferenceExpression cr
                    && cr.ColumnType == ColumnType.Wildcard));

        if (isCountStar)
        {
            // COUNT(*) - FetchXML requires an actual attribute name
            var primaryKeyColumn = $"{_primaryEntityName}id";
            attrs.Add($"name=\"{primaryKeyColumn}\"");
            attrs.Add("aggregate=\"count\"");
        }
        else
        {
            // Extract column name from the first parameter
            if (funcCall.Parameters.Count > 0
                && funcCall.Parameters[0] is ColumnReferenceExpression paramColRef)
            {
                var colName = NormalizeName(GetColumnName(paramColRef));
                attrs.Add($"name=\"{colName}\"");
            }

            // Map aggregate type: COUNT(column) => countcolumn, others use function name
            var hasColumn = funcCall.Parameters.Count > 0
                && funcCall.Parameters[0] is ColumnReferenceExpression;
            var aggregateType = MapAggregateType(funcName, hasColumn);
            attrs.Add($"aggregate=\"{aggregateType}\"");

            // DISTINCT
            if (funcCall.UniqueRowFilter == UniqueRowFilter.Distinct)
            {
                attrs.Add("distinct=\"true\"");
            }
        }

        // Alias is required for aggregates in FetchXML
        var alias = explicitAlias ?? GenerateAlias(funcName);
        attrs.Add($"alias=\"{alias}\"");

        lines.Add($"    <attribute {string.Join(" ", attrs)} />");
    }

    /// <summary>
    /// Maps a SQL aggregate function name to its FetchXML aggregate type.
    /// </summary>
    private static string MapAggregateType(string funcName, bool hasColumn) => funcName switch
    {
        "COUNT" => hasColumn ? "countcolumn" : "count",
        "SUM" => "sum",
        "AVG" => "avg",
        "MIN" => "min",
        "MAX" => "max",
        _ => "count"
    };

    /// <summary>
    /// Generates a unique alias for an aggregate column without an explicit one.
    /// </summary>
    private string GenerateAlias(string funcName)
    {
        _aliasCounter++;
        return $"{funcName.ToLowerInvariant()}_{_aliasCounter}";
    }

    /// <summary>
    /// Emits GROUP BY columns that are not already in the SELECT list.
    /// </summary>
    private void EmitMissingGroupByColumns(
        List<string> groupByColumns,
        IList<SelectElement> selectElements,
        (string tableName, string? alias) fromTable,
        List<JoinInfo> joins,
        List<string> lines)
    {
        foreach (var groupByCol in groupByColumns)
        {
            var isInSelect = selectElements.Any(elem =>
                elem is SelectScalarExpression scalar
                && scalar.Expression is ColumnReferenceExpression colRef
                && colRef.ColumnType != ColumnType.Wildcard
                && NormalizeName(GetColumnName(colRef)).Equals(groupByCol, StringComparison.OrdinalIgnoreCase));

            if (!isInSelect)
            {
                lines.Add($"    <attribute name=\"{groupByCol}\" groupby=\"true\" />");
            }
        }
    }

    /// <summary>
    /// Emits FetchXML dategrouping attributes for GROUP BY date function expressions.
    /// </summary>
    private void EmitDateGroupingAttributes(
        List<(string functionName, string columnName)> dateFunctions,
        IList<SelectElement> selectElements,
        List<string> lines)
    {
        foreach (var (funcName, columnName) in dateFunctions)
        {
            var dategrouping = funcName.ToUpperInvariant() switch
            {
                "YEAR" => "year",
                "MONTH" => "month",
                "DAY" => "day",
                "QUARTER" => "quarter",
                "WEEK" => "week",
                _ => (string?)null
            };

            if (dategrouping is null) continue;

            var attrName = NormalizeName(columnName);

            // Find alias from the SELECT list if there is a matching function column
            var alias = FindDateGroupingAlias(funcName, columnName, selectElements)
                     ?? $"{dategrouping}_{attrName}";

            lines.Add($"    <attribute name=\"{attrName}\" groupby=\"true\" dategrouping=\"{dategrouping}\" alias=\"{alias}\" />");
        }
    }

    /// <summary>
    /// Finds the alias for a date-grouping function by matching it against SELECT elements.
    /// </summary>
    private static string? FindDateGroupingAlias(
        string funcName, string columnName, IList<SelectElement> selectElements)
    {
        foreach (var elem in selectElements)
        {
            if (elem is not SelectScalarExpression scalar) continue;
            if (scalar.ColumnName is null) continue;
            if (scalar.Expression is not FunctionCall selectFunc) continue;

            if (!string.Equals(selectFunc.FunctionName?.Value, funcName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (selectFunc.Parameters.Count != 1) continue;

            if (selectFunc.Parameters[0] is ColumnReferenceExpression selCol)
            {
                if (string.Equals(GetColumnName(selCol), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return scalar.ColumnName.Value;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Emits attribute elements for columns referenced by expression conditions in WHERE.
    /// These conditions cannot be represented in FetchXML and are evaluated client-side,
    /// but their columns must still be fetched from Dataverse.
    /// </summary>
    private void EmitExpressionConditionColumns(WhereClause? whereClause, List<string> lines)
    {
        if (whereClause is null) return;

        var columnNames = new List<string>();
        CollectExpressionConditionColumns(whereClause.SearchCondition, columnNames);

        if (columnNames.Count == 0) return;

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var colName in columnNames)
        {
            var attrName = NormalizeName(colName);
            if (emitted.Add(attrName))
            {
                lines.Add($"    <attribute name=\"{attrName}\" />");
            }
        }
    }

    /// <summary>
    /// Recursively collects column names from expression conditions (conditions that
    /// compare two expressions rather than column-to-literal).
    /// </summary>
    private static void CollectExpressionConditionColumns(
        BooleanExpression condition, List<string> columnNames)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
                // Only collect if both sides are non-literal (column-to-column or computed)
                if (!IsSimpleLiteral(comparison.FirstExpression)
                    && !IsSimpleLiteral(comparison.SecondExpression))
                {
                    // Both sides are expressions - this is a client-side condition
                    CollectColumnsFromScalar(comparison.FirstExpression, columnNames);
                    CollectColumnsFromScalar(comparison.SecondExpression, columnNames);
                }
                else if (comparison.FirstExpression is not ColumnReferenceExpression
                    && !IsSimpleLiteral(comparison.FirstExpression))
                {
                    // Left side is a computed expression (not a simple column)
                    CollectColumnsFromScalar(comparison.FirstExpression, columnNames);
                    CollectColumnsFromScalar(comparison.SecondExpression, columnNames);
                }
                else if (comparison.SecondExpression is not ColumnReferenceExpression
                    && !IsSimpleLiteral(comparison.SecondExpression)
                    && comparison.SecondExpression is not VariableReference)
                {
                    // Right side is a computed expression
                    CollectColumnsFromScalar(comparison.FirstExpression, columnNames);
                    CollectColumnsFromScalar(comparison.SecondExpression, columnNames);
                }
                break;

            case BooleanBinaryExpression binaryBool:
                CollectExpressionConditionColumns(binaryBool.FirstExpression, columnNames);
                CollectExpressionConditionColumns(binaryBool.SecondExpression, columnNames);
                break;

            case BooleanParenthesisExpression paren:
                CollectExpressionConditionColumns(paren.Expression, columnNames);
                break;

            case BooleanNotExpression notExpr:
                CollectExpressionConditionColumns(notExpr.Expression, columnNames);
                break;
        }
    }

    /// <summary>
    /// Checks if a scalar expression is a simple literal value (string, number, etc.).
    /// </summary>
    private static bool IsSimpleLiteral(ScalarExpression expression)
    {
        return expression is Literal
            || expression is VariableReference
            || expression is UnaryExpression ue && ue.Expression is Literal;
    }

    /// <summary>
    /// Recursively collects column references from a scalar expression.
    /// </summary>
    private static void CollectColumnsFromScalar(ScalarExpression expression, List<string> columnNames)
    {
        switch (expression)
        {
            case ColumnReferenceExpression colRef when colRef.ColumnType != ColumnType.Wildcard:
                columnNames.Add(GetColumnName(colRef));
                break;

            case FunctionCall funcCall:
                foreach (var param in funcCall.Parameters)
                {
                    CollectColumnsFromScalar(param, columnNames);
                }
                break;

            case BinaryExpression binExpr:
                CollectColumnsFromScalar(binExpr.FirstExpression, columnNames);
                CollectColumnsFromScalar(binExpr.SecondExpression, columnNames);
                break;

            case UnaryExpression unaryExpr:
                CollectColumnsFromScalar(unaryExpr.Expression, columnNames);
                break;

            case SearchedCaseExpression caseExpr:
                foreach (var whenClause in caseExpr.WhenClauses)
                {
                    CollectExpressionConditionColumns(whenClause.WhenExpression, columnNames);
                    CollectColumnsFromScalar(whenClause.ThenExpression, columnNames);
                }
                if (caseExpr.ElseExpression is not null)
                {
                    CollectColumnsFromScalar(caseExpr.ElseExpression, columnNames);
                }
                break;

            case IIfCall iifCall:
                CollectExpressionConditionColumns(iifCall.Predicate, columnNames);
                CollectColumnsFromScalar(iifCall.ThenExpression, columnNames);
                CollectColumnsFromScalar(iifCall.ElseExpression, columnNames);
                break;

            case CastCall castCall:
                CollectColumnsFromScalar(castCall.Parameter, columnNames);
                break;

            case ConvertCall convertCall:
                CollectColumnsFromScalar(convertCall.Parameter, columnNames);
                break;

            case ParenthesisExpression parenExpr:
                CollectColumnsFromScalar(parenExpr.Expression, columnNames);
                break;
        }
    }

    /// <summary>
    /// Emits attribute elements for all columns referenced in a scalar expression.
    /// Used for computed columns (CASE/IIF) so the base data is retrieved from Dataverse.
    /// </summary>
    private void EmitReferencedColumnsFromExpression(
        ScalarExpression expression, HashSet<string> emitted, List<string> lines)
    {
        var columnNames = new List<string>();
        CollectColumnsFromScalar(expression, columnNames);

        foreach (var colName in columnNames)
        {
            var attrName = NormalizeName(colName);
            if (emitted.Add(attrName))
            {
                lines.Add($"    <attribute name=\"{attrName}\" />");
            }
        }
    }

    #endregion

    #region Link Entity (JOIN) Emission

    /// <summary>
    /// Emits a FetchXML link-entity element for a JOIN, including its columns.
    /// </summary>
    private void EmitLinkEntity(
        JoinInfo join,
        IList<SelectElement> selectElements,
        List<string> lines)
    {
        var from = NormalizeName(join.FromColumn);
        var to = NormalizeName(join.ToColumn);
        var aliasAttr = join.Alias is not null ? $" alias=\"{join.Alias}\"" : "";
        var linkEntityName = NormalizeName(join.TableName);

        lines.Add($"    <link-entity name=\"{linkEntityName}\" from=\"{from}\" to=\"{to}\" link-type=\"{join.LinkType}\"{aliasAttr}>");

        // Add columns that belong to this link-entity
        foreach (var elem in selectElements)
        {
            switch (elem)
            {
                case SelectStarExpression star when star.Qualifier is not null:
                {
                    var qualifier = GetLastIdentifier(star.Qualifier);
                    if (IsJoinTableQualifier(qualifier, join))
                    {
                        lines.Add("      <all-attributes />");
                    }
                    break;
                }

                case SelectScalarExpression scalar
                    when scalar.Expression is ColumnReferenceExpression colRef
                        && colRef.ColumnType != ColumnType.Wildcard:
                {
                    var tableQualifier = GetTableQualifier(colRef);
                    if (IsJoinTableQualifier(tableQualifier, join))
                    {
                        var attrName = NormalizeName(GetColumnName(colRef));
                        var alias = scalar.ColumnName?.Value;
                        if (alias is not null)
                        {
                            lines.Add($"      <attribute name=\"{attrName}\" alias=\"{alias}\" />");
                        }
                        else
                        {
                            lines.Add($"      <attribute name=\"{attrName}\" />");
                        }
                    }
                    break;
                }
            }
        }

        lines.Add("    </link-entity>");
    }

    /// <summary>
    /// Checks if a table qualifier matches a joined table.
    /// </summary>
    private static bool IsJoinTableQualifier(string? qualifier, JoinInfo join)
    {
        if (qualifier is null) return false;

        if (join.Alias is not null
            && qualifier.Equals(join.Alias, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return qualifier.Equals(join.TableName, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Filter (WHERE) Emission

    /// <summary>
    /// Emits a FetchXML filter element from a boolean expression.
    /// </summary>
    private void EmitFilter(BooleanExpression condition, List<string> lines, string indent)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
                EmitComparisonFilter(comparison, lines, indent);
                break;

            case LikePredicate like:
                EmitLikeFilter(like, lines, indent);
                break;

            case BooleanIsNullExpression isNull:
                EmitNullFilter(isNull, lines, indent);
                break;

            case InPredicate inPred:
                EmitInFilter(inPred, lines, indent);
                break;

            case BooleanBinaryExpression binaryBool:
                EmitLogicalFilter(binaryBool, lines, indent);
                break;

            case BooleanParenthesisExpression paren:
                EmitFilter(paren.Expression, lines, indent);
                break;

            case BooleanNotExpression notExpr:
                EmitNotFilter(notExpr, lines, indent);
                break;

            case BooleanTernaryExpression ternary:
                EmitBetweenFilter(ternary, lines, indent);
                break;
        }
    }

    /// <summary>
    /// Emits a comparison condition (column op value).
    /// </summary>
    private void EmitComparisonFilter(
        BooleanComparisonExpression comparison, List<string> lines, string indent)
    {
        // Determine which side is the column and which is the value
        var (columnExpr, valueExpr, flipped) = ResolveColumnAndValue(comparison);

        if (columnExpr is null)
        {
            // This is an expression condition (column-to-column or computed).
            // Cannot be represented in FetchXML - skip for client-side evaluation.
            return;
        }

        var attr = NormalizeName(GetColumnName(columnExpr));
        var op = MapComparisonOperator(comparison.ComparisonType, flipped);
        var value = FormatLiteralValue(valueExpr);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" value=\"{value}\" />");
        lines.Add($"{indent}</filter>");
    }

    /// <summary>
    /// Emits a LIKE condition.
    /// </summary>
    private void EmitLikeFilter(LikePredicate like, List<string> lines, string indent)
    {
        if (like.FirstExpression is not ColumnReferenceExpression colRef) return;
        if (like.SecondExpression is not StringLiteral patternLiteral) return;

        var attr = NormalizeName(GetColumnName(colRef));
        var pattern = patternLiteral.Value;
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

    /// <summary>
    /// Emits an IS NULL / IS NOT NULL condition.
    /// </summary>
    private void EmitNullFilter(BooleanIsNullExpression isNull, List<string> lines, string indent)
    {
        if (isNull.Expression is not ColumnReferenceExpression colRef) return;

        var attr = NormalizeName(GetColumnName(colRef));
        var op = isNull.IsNot ? "not-null" : "null";

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\" />");
        lines.Add($"{indent}</filter>");
    }

    /// <summary>
    /// Emits an IN / NOT IN condition.
    /// </summary>
    private void EmitInFilter(InPredicate inPred, List<string> lines, string indent)
    {
        if (inPred.Expression is not ColumnReferenceExpression colRef) return;

        var attr = NormalizeName(GetColumnName(colRef));
        var op = inPred.NotDefined ? "not-in" : "in";

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\">");

        foreach (var val in inPred.Values)
        {
            var formatted = FormatLiteralValue(val);
            lines.Add($"{indent}    <value>{formatted}</value>");
        }

        lines.Add($"{indent}  </condition>");
        lines.Add($"{indent}</filter>");
    }

    /// <summary>
    /// Emits a logical AND/OR filter combining child conditions.
    /// </summary>
    private void EmitLogicalFilter(
        BooleanBinaryExpression binaryBool, List<string> lines, string indent)
    {
        var filterType = binaryBool.BinaryExpressionType == BooleanBinaryExpressionType.Or
            ? "or"
            : "and";

        lines.Add($"{indent}<filter type=\"{filterType}\">");

        EmitFilterInner(binaryBool.FirstExpression, lines, indent + "  ");
        EmitFilterInner(binaryBool.SecondExpression, lines, indent + "  ");

        lines.Add($"{indent}</filter>");
    }

    /// <summary>
    /// Emits a NOT filter by wrapping the inner condition with negation.
    /// </summary>
    private void EmitNotFilter(BooleanNotExpression notExpr, List<string> lines, string indent)
    {
        // For NOT, we need to handle specific inner condition types
        switch (notExpr.Expression)
        {
            case BooleanIsNullExpression isNull:
                // NOT IS NULL => IS NOT NULL (invert)
                var syntheticIsNull = new BooleanIsNullExpression
                {
                    Expression = isNull.Expression,
                    IsNot = !isNull.IsNot
                };
                EmitNullFilter(syntheticIsNull, lines, indent);
                break;

            case InPredicate inPred:
                // NOT IN
                var syntheticIn = new InPredicate
                {
                    Expression = inPred.Expression,
                    NotDefined = !inPred.NotDefined
                };
                foreach (var v in inPred.Values) syntheticIn.Values.Add(v);
                EmitInFilter(syntheticIn, lines, indent);
                break;

            default:
                // For other cases, just emit inner
                EmitFilter(notExpr.Expression, lines, indent);
                break;
        }
    }

    /// <summary>
    /// Emits a BETWEEN / NOT BETWEEN condition.
    /// This is a Dataverse extension: FetchXML supports between/not-between natively.
    /// </summary>
    private void EmitBetweenFilter(
        BooleanTernaryExpression ternary, List<string> lines, string indent)
    {
        if (ternary.FirstExpression is not ColumnReferenceExpression colRef) return;

        var attr = NormalizeName(GetColumnName(colRef));
        var op = ternary.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween
            ? "not-between"
            : "between";

        var lowValue = FormatLiteralValue(ternary.SecondExpression);
        var highValue = FormatLiteralValue(ternary.ThirdExpression);

        lines.Add($"{indent}<filter>");
        lines.Add($"{indent}  <condition attribute=\"{attr}\" operator=\"{op}\">");
        lines.Add($"{indent}    <value>{lowValue}</value>");
        lines.Add($"{indent}    <value>{highValue}</value>");
        lines.Add($"{indent}  </condition>");
        lines.Add($"{indent}</filter>");
    }

    /// <summary>
    /// Emits a condition inside a logical filter (without wrapping in its own filter element).
    /// </summary>
    private void EmitFilterInner(BooleanExpression condition, List<string> lines, string indent)
    {
        switch (condition)
        {
            case BooleanComparisonExpression comparison:
            {
                var (columnExpr, valueExpr, flipped) = ResolveColumnAndValue(comparison);
                if (columnExpr is null) break; // expression condition, skip

                var attr = NormalizeName(GetColumnName(columnExpr));
                var op = MapComparisonOperator(comparison.ComparisonType, flipped);
                var value = FormatLiteralValue(valueExpr);
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" value=\"{value}\" />");
                break;
            }

            case LikePredicate like:
            {
                if (like.FirstExpression is not ColumnReferenceExpression colRef) break;
                if (like.SecondExpression is not StringLiteral patternLiteral) break;

                var attr = NormalizeName(GetColumnName(colRef));
                var pattern = patternLiteral.Value;
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
                break;
            }

            case BooleanIsNullExpression isNull:
            {
                if (isNull.Expression is not ColumnReferenceExpression colRef) break;

                var attr = NormalizeName(GetColumnName(colRef));
                var op = isNull.IsNot ? "not-null" : "null";
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\" />");
                break;
            }

            case InPredicate inPred:
            {
                if (inPred.Expression is not ColumnReferenceExpression colRef) break;

                var attr = NormalizeName(GetColumnName(colRef));
                var op = inPred.NotDefined ? "not-in" : "in";
                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\">");
                foreach (var v in inPred.Values)
                {
                    lines.Add($"{indent}  <value>{FormatLiteralValue(v)}</value>");
                }
                lines.Add($"{indent}</condition>");
                break;
            }

            case BooleanBinaryExpression binaryBool:
            {
                var type = binaryBool.BinaryExpressionType == BooleanBinaryExpressionType.Or
                    ? "or"
                    : "and";
                lines.Add($"{indent}<filter type=\"{type}\">");
                EmitFilterInner(binaryBool.FirstExpression, lines, indent + "  ");
                EmitFilterInner(binaryBool.SecondExpression, lines, indent + "  ");
                lines.Add($"{indent}</filter>");
                break;
            }

            case BooleanParenthesisExpression paren:
                EmitFilterInner(paren.Expression, lines, indent);
                break;

            case BooleanTernaryExpression ternary:
            {
                if (ternary.FirstExpression is not ColumnReferenceExpression colRef) break;

                var attr = NormalizeName(GetColumnName(colRef));
                var op = ternary.TernaryExpressionType == BooleanTernaryExpressionType.NotBetween
                    ? "not-between"
                    : "between";
                var lowValue = FormatLiteralValue(ternary.SecondExpression);
                var highValue = FormatLiteralValue(ternary.ThirdExpression);

                lines.Add($"{indent}<condition attribute=\"{attr}\" operator=\"{op}\">");
                lines.Add($"{indent}  <value>{lowValue}</value>");
                lines.Add($"{indent}  <value>{highValue}</value>");
                lines.Add($"{indent}</condition>");
                break;
            }

            case BooleanNotExpression notExpr:
                EmitNotFilter(notExpr, lines, indent);
                break;
        }
    }

    /// <summary>
    /// Resolves which side of a comparison is the column and which is the literal value.
    /// Returns null for the column if this is an expression condition (column-to-column).
    /// The flipped flag indicates if the operator should be reversed (e.g., value > column => column &lt; value).
    /// </summary>
    private static (ColumnReferenceExpression? column, ScalarExpression value, bool flipped) ResolveColumnAndValue(
        BooleanComparisonExpression comparison)
    {
        var left = comparison.FirstExpression;
        var right = comparison.SecondExpression;

        if (left is ColumnReferenceExpression leftCol && IsLiteralOrVariable(right))
        {
            return (leftCol, right, false);
        }

        if (right is ColumnReferenceExpression rightCol && IsLiteralOrVariable(left))
        {
            return (rightCol, left, true);
        }

        // Expression condition - cannot push to FetchXML
        return (null, right, false);
    }

    /// <summary>
    /// Checks if a scalar expression is a literal value, variable reference,
    /// or negated literal.
    /// </summary>
    private static bool IsLiteralOrVariable(ScalarExpression expression)
    {
        return expression is Literal
            || expression is VariableReference
            || (expression is UnaryExpression ue
                && ue.UnaryExpressionType == UnaryExpressionType.Negative
                && ue.Expression is Literal);
    }

    /// <summary>
    /// Maps a ScriptDom comparison type to a FetchXML operator string.
    /// If flipped is true, the operator direction is reversed.
    /// </summary>
    private static string MapComparisonOperator(BooleanComparisonType compType, bool flipped)
    {
        if (!flipped)
        {
            return compType switch
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
        }

        // Flipped: reverse the direction
        return compType switch
        {
            BooleanComparisonType.Equals => "eq",
            BooleanComparisonType.NotEqualToBrackets => "ne",
            BooleanComparisonType.NotEqualToExclamation => "ne",
            BooleanComparisonType.LessThan => "gt",
            BooleanComparisonType.GreaterThan => "lt",
            BooleanComparisonType.LessThanOrEqualTo => "ge",
            BooleanComparisonType.GreaterThanOrEqualTo => "le",
            _ => "eq"
        };
    }

    #endregion

    #region Order By Emission

    /// <summary>
    /// Emits a FetchXML order element from an ORDER BY expression.
    /// </summary>
    private void EmitOrderBy(
        ExpressionWithSortOrder orderElement,
        IList<SelectElement> selectElements,
        bool isAggregateQuery,
        List<string> lines)
    {
        var descending = orderElement.SortOrder == SortOrder.Descending ? "true" : "false";

        if (orderElement.Expression is not ColumnReferenceExpression colRef) return;

        var orderColumnName = NormalizeName(GetColumnName(colRef));

        // In aggregate queries, check if ORDER BY column matches an alias
        if (isAggregateQuery)
        {
            var matchingAlias = FindMatchingAlias(orderColumnName, selectElements);
            if (matchingAlias is not null)
            {
                lines.Add($"    <order alias=\"{matchingAlias}\" descending=\"{descending}\" />");
                return;
            }
        }

        lines.Add($"    <order attribute=\"{orderColumnName}\" descending=\"{descending}\" />");
    }

    /// <summary>
    /// Finds a matching alias from select elements for the given column name.
    /// </summary>
    private static string? FindMatchingAlias(string columnName, IList<SelectElement> selectElements)
    {
        foreach (var elem in selectElements)
        {
            if (elem is SelectScalarExpression scalar && scalar.ColumnName is not null)
            {
                var alias = scalar.ColumnName.Value;
                if (alias.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return alias;
                }
            }
        }
        return null;
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Checks if a table qualifier refers to the main (primary) entity.
    /// Returns true if qualifier is null (unqualified column), matches the
    /// main entity name, or matches the main entity alias.
    /// </summary>
    private static bool IsMainEntityQualifier(
        string? qualifier,
        (string tableName, string? alias) fromTable,
        List<JoinInfo> joins)
    {
        if (qualifier is null) return true;

        if (fromTable.alias is not null
            && qualifier.Equals(fromTable.alias, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (qualifier.Equals(fromTable.tableName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check that it is NOT a join table qualifier (default to main entity if unknown)
        foreach (var join in joins)
        {
            if (IsJoinTableQualifier(qualifier, join))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Gets the last identifier from a multi-part identifier (used for star qualifier).
    /// </summary>
    private static string GetLastIdentifier(MultiPartIdentifier identifier)
    {
        if (identifier.Identifiers.Count == 0) return "";
        return identifier.Identifiers[identifier.Identifiers.Count - 1].Value;
    }

    /// <summary>
    /// Formats a scalar expression as a literal value string for FetchXML.
    /// Handles string escaping and null values.
    /// </summary>
    private static string FormatLiteralValue(ScalarExpression expression)
    {
        switch (expression)
        {
            case StringLiteral strLit:
                return EscapeXml(strLit.Value);

            case IntegerLiteral intLit:
                return intLit.Value;

            case NumericLiteral numLit:
                return numLit.Value;

            case RealLiteral realLit:
                return realLit.Value;

            case NullLiteral:
                return "";

            case UnaryExpression unary when unary.UnaryExpressionType == UnaryExpressionType.Negative:
                return $"-{FormatLiteralValue(unary.Expression)}";

            case VariableReference varRef:
                // Variables are typically replaced before reaching here,
                // but emit the variable name as a placeholder.
                return varRef.Name;

            default:
                return "";
        }
    }

    /// <summary>
    /// Escapes special XML characters in a string value.
    /// </summary>
    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
    }

    /// <summary>
    /// Normalizes an entity or attribute name to lowercase.
    /// </summary>
    private static string NormalizeName(string name) => name.ToLowerInvariant();

    #endregion
}
