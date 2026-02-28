using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Query.Planning.Partitioning;
using PPDS.Dataverse.Sql.Transpilation;
using PPDS.Query.Execution;
using PPDS.Query.Parsing;
using PPDS.Query.Planning.Nodes;

namespace PPDS.Query.Planning;

public sealed partial class ExecutionPlanBuilder
{
    // ═══════════════════════════════════════════════════════════════════
    //  Client-side JOIN planning (fallback when FetchXML can't handle the join)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a query with joins that cannot be transpiled to FetchXML.
    /// Each table is scanned independently and joined client-side.
    /// </summary>
    private QueryPlanResult PlanClientSideJoin(
        SelectStatement selectStmt,
        QuerySpecification querySpec,
        QueryPlanOptions options)
    {
        var fromClause = querySpec.FromClause;
        if (fromClause?.TableReferences.Count != 1)
            throw new QueryParseException("Expected exactly one table reference in FROM clause for client-side join planning.");

        // Guard: GROUP BY, HAVING, and aggregate functions are not supported in client-side joins
        if (querySpec.GroupByClause?.GroupingSpecifications?.Count > 0)
            throw new QueryParseException("GROUP BY is not supported in client-side join queries.");
        if (querySpec.HavingClause != null)
            throw new QueryParseException("HAVING is not supported in client-side join queries.");
        if (HasAggregateSelectElements(querySpec))
            throw new QueryParseException("Aggregate functions are not supported in client-side join queries.");

        // Recursively build the join tree (handles QualifiedJoin, UnqualifiedJoin, and NamedTableReference)
        var tableRef = fromClause.TableReferences[0];
        var (node, entityName) = PlanTableReference(tableRef, options);

        IQueryPlanNode rootNode = node;

        // Apply WHERE filter (client-side)
        if (querySpec.WhereClause?.SearchCondition != null)
        {
            var predicate = _expressionCompiler.CompilePredicate(querySpec.WhereClause.SearchCondition);
            var description = querySpec.WhereClause.SearchCondition.ToString() ?? "WHERE (client)";
            rootNode = new ClientFilterNode(rootNode, predicate, description);
        }

        // ── Post-join pipeline (mirrors PlanSelect post-scan processing) ──

        // SELECT list projection — client-side joins fetch all-attributes from each table,
        // so we need to project down to only the requested columns unless SELECT *.
        // BuildProjectNodeFromScriptDom handles computed columns (CASE/IIF) AND simple columns,
        // so when computed columns are present it also serves as the projection.
        if (HasComputedColumnsInQuerySpec(querySpec))
        {
            rootNode = BuildProjectNodeFromScriptDom(rootNode, querySpec);
        }
        else if (!IsSelectStar(querySpec))
        {
            rootNode = BuildSelectListProjection(rootNode, querySpec);
        }

        // ORDER BY — FetchXML handles sorting server-side, but client-side joins need
        // an explicit sort node.
        if (querySpec.OrderByClause?.OrderByElements?.Count > 0)
        {
            rootNode = BuildClientSortNode(rootNode, querySpec.OrderByClause);
        }

        // TOP N — limit the number of rows returned.
        var top = ExtractTopFromQuerySpec(querySpec);
        if (top.HasValue)
        {
            rootNode = new OffsetFetchNode(rootNode, 0, top.Value);
        }

        // OFFSET/FETCH paging
        if (querySpec.OffsetClause != null)
        {
            rootNode = BuildOffsetFetchNode(rootNode, querySpec.OffsetClause);
        }

        return new QueryPlanResult
        {
            RootNode = rootNode,
            FetchXml = "<!-- client-side join -->",
            VirtualColumns = new Dictionary<string, VirtualColumnInfo>(),
            EntityLogicalName = entityName
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  IN (subquery) / NOT IN (subquery) planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a SELECT whose WHERE clause contains one or more IN (subquery) or NOT IN (subquery)
    /// predicates. Strips the IN predicates from the WHERE, plans the outer query normally
    /// (which may generate FetchXML for pushable conditions), then plans each inner subquery
    /// separately and wraps the outer scan in <see cref="HashSemiJoinNode"/> instances.
    /// </summary>
    private QueryPlanResult PlanInSubquery(
        SelectStatement selectStmt,
        QuerySpecification querySpec,
        QueryPlanOptions options)
    {
        // 1. Extract IN subquery predicates from WHERE, collecting them and building a cleaned expression
        var inPredicates = new List<InPredicate>();
        var cleanedWhere = ExtractInSubqueryPredicates(querySpec.WhereClause?.SearchCondition, inPredicates);

        // 1.5. Attempt anti-join rewrite: simple NOT IN → LEFT OUTER JOIN + IS NULL
        // This pushes the anti-semi-join to FetchXML instead of running it client-side.
        var rewritableAntiJoins = new List<(InPredicate Pred, AntiJoinRewriteInfo Info)>();
        var remainingPredicates = new List<InPredicate>();
        foreach (var inPred in inPredicates)
        {
            if (inPred.NotDefined
                && TryExtractAntiJoinRewriteInfo(inPred, out var rewriteInfo))
            {
                rewritableAntiJoins.Add((inPred, rewriteInfo));
            }
            else
            {
                remainingPredicates.Add(inPred);
            }
        }

        // 2. Temporarily modify the WHERE to exclude subquery predicates so FetchXML generation works
        var origWhereClause = querySpec.WhereClause;
        if (cleanedWhere != null)
        {
            querySpec.WhereClause = new WhereClause { SearchCondition = cleanedWhere };
        }
        else
        {
            querySpec.WhereClause = null;
        }

        QueryPlanResult outerResult;
        try
        {
            // Re-enter PlanSelect for the outer query (without IN subquery predicates).
            // This will generate FetchXML for pushable conditions and apply the normal pipeline.
            outerResult = PlanSelect(selectStmt, querySpec, options);
        }
        finally
        {
            // Restore the original AST so callers see no mutation
            querySpec.WhereClause = origWhereClause;
        }

        // 2.5. Apply anti-join rewrites by injecting link-entity elements into FetchXML
        var fetchXml = outerResult.FetchXml;
        foreach (var (_, info) in rewritableAntiJoins)
        {
            fetchXml = InjectAntiJoinLinkEntity(fetchXml, info);
        }

        // 3. For each remaining IN subquery (non-rewritable), plan the inner query and wrap in HashSemiJoinNode
        var currentNode = outerResult.RootNode;
        foreach (var inPred in remainingPredicates)
        {
            // Get the outer column name from the left-hand side of the IN predicate
            var outerCol = inPred.Expression is ColumnReferenceExpression colRef
                ? colRef.MultiPartIdentifier.Identifiers.Last().Value
                : throw new QueryParseException("IN subquery requires a column reference on the left side.");

            // Plan the inner subquery
            var innerResult = PlanQueryExpressionAsSelect(inPred.Subquery.QueryExpression, options);

            // Get the inner column name (first column in subquery SELECT)
            var innerQuery = inPred.Subquery.QueryExpression as QuerySpecification;
            var innerCol = ExtractFirstSelectColumnName(innerQuery)
                ?? throw new QueryParseException("Cannot determine column name from IN subquery SELECT list.");

            // Wrap in HashSemiJoinNode (antiSemiJoin = true for NOT IN)
            currentNode = new HashSemiJoinNode(
                currentNode, innerResult.RootNode,
                outerCol, innerCol,
                antiSemiJoin: inPred.NotDefined);
        }

        return new QueryPlanResult
        {
            RootNode = currentNode,
            FetchXml = fetchXml,
            VirtualColumns = outerResult.VirtualColumns,
            EntityLogicalName = outerResult.EntityLogicalName
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NOT IN → LEFT OUTER JOIN anti-join rewrite
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Information extracted from a simple NOT IN subquery that can be rewritten
    /// as a LEFT OUTER JOIN + IS NULL in FetchXML.
    /// </summary>
    private sealed class AntiJoinRewriteInfo
    {
        /// <summary>The outer column name (left side of NOT IN).</summary>
        public required string OuterColumn { get; init; }

        /// <summary>The inner table name (FROM clause of subquery).</summary>
        public required string InnerTableName { get; init; }

        /// <summary>The inner column name (SELECT column of subquery).</summary>
        public required string InnerColumn { get; init; }

        /// <summary>
        /// Optional WHERE conditions from the subquery, as FetchXML filter elements.
        /// These become conditions inside the link-entity.
        /// </summary>
        public IReadOnlyList<InnerFilterCondition>? InnerFilters { get; init; }
    }

    /// <summary>
    /// A simple filter condition from a NOT IN subquery's WHERE clause.
    /// </summary>
    private sealed class InnerFilterCondition
    {
        public required string Attribute { get; init; }
        public required string Operator { get; init; }
        public required string Value { get; init; }
    }

    /// <summary>
    /// Determines whether a NOT IN predicate has a simple subquery that can be rewritten
    /// as a LEFT OUTER JOIN + IS NULL for FetchXML pushdown.
    /// </summary>
    /// <remarks>
    /// A subquery is "simple" when:
    /// <list type="bullet">
    ///   <item>It is a <see cref="QuerySpecification"/> (not a UNION/binary query)</item>
    ///   <item>It selects a single column (no expressions, no *)</item>
    ///   <item>It references a single table (no JOINs)</item>
    ///   <item>It has no GROUP BY, HAVING, TOP, DISTINCT, or subqueries in WHERE</item>
    ///   <item>Its optional WHERE clause contains only simple column-to-literal comparisons</item>
    /// </list>
    /// </remarks>
    private static bool TryExtractAntiJoinRewriteInfo(
        InPredicate inPred,
        out AntiJoinRewriteInfo info)
    {
        info = null!;

        // Left side must be a column reference
        if (inPred.Expression is not ColumnReferenceExpression outerColRef)
            return false;

        var outerCol = outerColRef.MultiPartIdentifier.Identifiers.Last().Value;

        // Must have a subquery
        if (inPred.Subquery?.QueryExpression is not QuerySpecification innerSpec)
            return false;

        // No GROUP BY, HAVING, TOP, DISTINCT
        if (innerSpec.GroupByClause != null) return false;
        if (innerSpec.HavingClause != null) return false;
        if (innerSpec.TopRowFilter != null) return false;
        if (innerSpec.UniqueRowFilter == UniqueRowFilter.Distinct) return false;

        // Single table, no JOINs: FROM clause must have exactly one NamedTableReference
        if (innerSpec.FromClause?.TableReferences.Count != 1) return false;
        if (innerSpec.FromClause.TableReferences[0] is not NamedTableReference innerTable)
            return false;

        var innerTableName = innerTable.SchemaObject.Identifiers.Last().Value;

        // Single column in SELECT (not *, not an expression)
        if (innerSpec.SelectElements.Count != 1) return false;
        if (innerSpec.SelectElements[0] is not SelectScalarExpression selectScalar) return false;
        if (selectScalar.Expression is not ColumnReferenceExpression innerColRef) return false;

        var innerCol = innerColRef.MultiPartIdentifier.Identifiers.Last().Value;

        // Optional WHERE: must be simple column-to-literal comparisons joined by AND
        List<InnerFilterCondition>? innerFilters = null;
        if (innerSpec.WhereClause != null)
        {
            innerFilters = new List<InnerFilterCondition>();
            if (!TryExtractSimpleFilters(innerSpec.WhereClause.SearchCondition, innerFilters))
                return false;
        }

        info = new AntiJoinRewriteInfo
        {
            OuterColumn = outerCol,
            InnerTableName = innerTableName,
            InnerColumn = innerCol,
            InnerFilters = innerFilters
        };
        return true;
    }

    /// <summary>
    /// Recursively extracts simple column = literal conditions from a boolean expression.
    /// Returns false if any condition is too complex for anti-join rewrite
    /// (e.g., subqueries, OR, function calls).
    /// </summary>
    private static bool TryExtractSimpleFilters(
        BooleanExpression expr,
        List<InnerFilterCondition> filters)
    {
        switch (expr)
        {
            case BooleanComparisonExpression comparison:
            {
                // Must be column op literal
                if (comparison.FirstExpression is not ColumnReferenceExpression col)
                    return false;

                string? value;
                if (comparison.SecondExpression is IntegerLiteral intLit)
                    value = intLit.Value;
                else if (comparison.SecondExpression is StringLiteral strLit)
                    value = strLit.Value;
                else if (comparison.SecondExpression is NumericLiteral numLit)
                    value = numLit.Value;
                else
                    return false; // Not a simple literal

                var op = comparison.ComparisonType switch
                {
                    BooleanComparisonType.Equals => "eq",
                    BooleanComparisonType.NotEqualToBrackets => "ne",
                    BooleanComparisonType.NotEqualToExclamation => "ne",
                    BooleanComparisonType.GreaterThan => "gt",
                    BooleanComparisonType.GreaterThanOrEqualTo => "ge",
                    BooleanComparisonType.LessThan => "lt",
                    BooleanComparisonType.LessThanOrEqualTo => "le",
                    _ => (string?)null
                };

                if (op == null) return false;

                var attrName = col.MultiPartIdentifier.Identifiers.Last().Value.ToLowerInvariant();
                filters.Add(new InnerFilterCondition
                {
                    Attribute = attrName,
                    Operator = op,
                    Value = value
                });
                return true;
            }

            case BooleanBinaryExpression bin
                when bin.BinaryExpressionType == BooleanBinaryExpressionType.And:
                return TryExtractSimpleFilters(bin.FirstExpression, filters)
                    && TryExtractSimpleFilters(bin.SecondExpression, filters);

            case BooleanParenthesisExpression paren:
                return TryExtractSimpleFilters(paren.Expression, filters);

            default:
                return false; // OR, NOT, subqueries, etc. — too complex
        }
    }

    /// <summary>
    /// Injects a link-entity element into FetchXML to represent a LEFT OUTER JOIN anti-join.
    /// The link-entity has link-type="outer" and includes a null condition on the join column
    /// to filter out matched rows (anti-join semantics).
    /// </summary>
    private static string InjectAntiJoinLinkEntity(string fetchXml, AntiJoinRewriteInfo info)
    {
        var doc = XDocument.Parse(fetchXml);
        var entityElement = doc.Root?.Element("entity");
        if (entityElement == null) return fetchXml;

        var alias = $"__antijoin_{Guid.NewGuid().ToString("N")[..8]}";
        var innerTable = info.InnerTableName.ToLowerInvariant();
        var innerCol = info.InnerColumn.ToLowerInvariant();
        var outerCol = info.OuterColumn.ToLowerInvariant();

        // Build link-entity element
        var linkEntity = new XElement("link-entity",
            new XAttribute("name", innerTable),
            new XAttribute("from", innerCol),
            new XAttribute("to", outerCol),
            new XAttribute("link-type", "outer"),
            new XAttribute("alias", alias));

        // Add inner filter conditions from subquery WHERE (if any)
        if (info.InnerFilters is { Count: > 0 })
        {
            var filterElement = new XElement("filter", new XAttribute("type", "and"));
            foreach (var f in info.InnerFilters)
            {
                filterElement.Add(new XElement("condition",
                    new XAttribute("attribute", f.Attribute),
                    new XAttribute("operator", f.Operator),
                    new XAttribute("value", f.Value)));
            }
            linkEntity.Add(filterElement);
        }

        entityElement.Add(linkEntity);

        // Add outer-level null condition: where the anti-join alias column IS NULL
        // This is the anti-join semantics: only rows with NO match in the linked entity
        var outerFilter = entityElement.Element("filter");
        var nullCondition = new XElement("condition",
            new XAttribute("entityname", alias),
            new XAttribute("attribute", innerCol),
            new XAttribute("operator", "null"));

        if (outerFilter != null)
        {
            // Append to existing filter
            outerFilter.Add(nullCondition);
        }
        else
        {
            // Create new filter
            entityElement.Add(new XElement("filter", nullCondition));
        }

        return doc.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// Extracts <see cref="InPredicate"/> nodes with subqueries from a boolean expression tree,
    /// collecting them into the provided list. Returns the remaining boolean expression
    /// with IN subquery predicates removed, or null if the entire expression was consumed.
    /// Only extracts from top-level AND conjuncts — IN predicates inside OR/NOT/parenthesized
    /// expressions are left in place (they would require more complex rewrite).
    /// </summary>
    private static BooleanExpression? ExtractInSubqueryPredicates(
        BooleanExpression? expr,
        List<InPredicate> collected)
    {
        if (expr is null) return null;

        // Direct IN (subquery) — consume it entirely
        if (expr is InPredicate inPred && inPred.Subquery != null)
        {
            collected.Add(inPred);
            return null;
        }

        // AND — extract from both sides independently
        if (expr is BooleanBinaryExpression bin
            && bin.BinaryExpressionType == BooleanBinaryExpressionType.And)
        {
            var left = ExtractInSubqueryPredicates(bin.FirstExpression, collected);
            var right = ExtractInSubqueryPredicates(bin.SecondExpression, collected);

            if (left == null && right == null) return null;
            if (left == null) return right;
            if (right == null) return left;

            // Both sides still have content — create new AND node (avoid mutating shared AST)
            return new BooleanBinaryExpression
            {
                BinaryExpressionType = BooleanBinaryExpressionType.And,
                FirstExpression = left,
                SecondExpression = right
            };
        }

        // For other expression types (OR, NOT, parenthesized, etc.), don't extract —
        // leave them as-is in the WHERE clause.
        return expr;
    }

    /// <summary>
    /// Extracts the column name from the first element in a SELECT list.
    /// Used to determine the key column for IN subquery semi-joins.
    /// </summary>
    private static string? ExtractFirstSelectColumnName(QuerySpecification? querySpec)
    {
        if (querySpec?.SelectElements == null || querySpec.SelectElements.Count == 0) return null;

        var firstElement = querySpec.SelectElements[0];
        if (firstElement is SelectScalarExpression scalar)
        {
            // If aliased, use the alias
            if (scalar.ColumnName?.Value != null) return scalar.ColumnName.Value;

            // If a column reference, use the last identifier (column name)
            if (scalar.Expression is ColumnReferenceExpression colRef)
                return colRef.MultiPartIdentifier.Identifiers.Last().Value;
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EXISTS / NOT EXISTS subquery planning
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Plans a SELECT whose WHERE clause contains one or more EXISTS or NOT EXISTS predicates.
    /// Strips the EXISTS predicates from the WHERE, plans the outer query normally (which may
    /// generate FetchXML for pushable conditions), then plans each inner subquery separately
    /// and wraps the outer scan in <see cref="HashSemiJoinNode"/> instances.
    /// </summary>
    private QueryPlanResult PlanExistsSubquery(
        SelectStatement selectStmt,
        QuerySpecification querySpec,
        QueryPlanOptions options)
    {
        // 1. Extract EXISTS predicates from WHERE, collecting them and building a cleaned expression
        var existsPredicates = new List<(ExistsPredicate exists, bool isNot)>();
        var cleanedWhere = ExtractExistsPredicates(querySpec.WhereClause?.SearchCondition, existsPredicates);

        // 2. Temporarily modify the WHERE to exclude EXISTS predicates so FetchXML generation works
        var origWhereClause = querySpec.WhereClause;
        if (cleanedWhere != null)
        {
            querySpec.WhereClause = new WhereClause { SearchCondition = cleanedWhere };
        }
        else
        {
            querySpec.WhereClause = null;
        }

        QueryPlanResult outerResult;
        try
        {
            // Re-enter PlanSelect for the outer query (without EXISTS predicates).
            // This will generate FetchXML for pushable conditions and apply the normal pipeline.
            outerResult = PlanSelect(selectStmt, querySpec, options);
        }
        finally
        {
            // Restore the original AST so callers see no mutation
            querySpec.WhereClause = origWhereClause;
        }

        // Resolve outer table alias and entity name for correlation matching
        var outerEntityName = outerResult.EntityLogicalName;
        var outerAlias = ExtractOuterTableAlias(querySpec);

        // 3. For each EXISTS, extract correlation and wrap in HashSemiJoinNode
        var currentNode = outerResult.RootNode;
        foreach (var (exists, isNot) in existsPredicates)
        {
            // Extract correlation columns from inner WHERE
            var correlation = ExtractCorrelationFromExists(exists, outerEntityName, outerAlias);
            if (correlation == null)
            {
                throw new QueryParseException(
                    "EXISTS subquery requires a correlation predicate (e.g., WHERE inner.col = outer.col).");
            }

            // Plan the inner subquery
            var innerResult = PlanQueryExpressionAsSelect(exists.Subquery.QueryExpression, options);

            // Wrap in HashSemiJoinNode (antiSemiJoin = true for NOT EXISTS)
            currentNode = new HashSemiJoinNode(
                currentNode, innerResult.RootNode,
                correlation.Value.outerCol, correlation.Value.innerCol,
                antiSemiJoin: isNot);
        }

        return new QueryPlanResult
        {
            RootNode = currentNode,
            FetchXml = outerResult.FetchXml,
            VirtualColumns = outerResult.VirtualColumns,
            EntityLogicalName = outerEntityName
        };
    }

    /// <summary>
    /// Extracts <see cref="ExistsPredicate"/> nodes from a boolean expression tree,
    /// collecting them into the provided list along with whether they are negated (NOT EXISTS).
    /// Returns the remaining boolean expression with EXISTS predicates removed, or null if
    /// the entire expression was consumed.
    /// Only extracts from top-level AND conjuncts — EXISTS predicates inside OR/parenthesized
    /// expressions are left in place.
    /// </summary>
    private static BooleanExpression? ExtractExistsPredicates(
        BooleanExpression? expr,
        List<(ExistsPredicate exists, bool isNot)> collected)
    {
        if (expr is null) return null;

        // Direct ExistsPredicate
        if (expr is ExistsPredicate exists)
        {
            collected.Add((exists, false));
            return null;
        }

        // NOT EXISTS (BooleanNotExpression wrapping ExistsPredicate)
        if (expr is BooleanNotExpression not && not.Expression is ExistsPredicate notExists)
        {
            collected.Add((notExists, true));
            return null;
        }

        // AND — extract from both sides independently
        if (expr is BooleanBinaryExpression bin
            && bin.BinaryExpressionType == BooleanBinaryExpressionType.And)
        {
            var left = ExtractExistsPredicates(bin.FirstExpression, collected);
            var right = ExtractExistsPredicates(bin.SecondExpression, collected);

            if (left == null && right == null) return null;
            if (left == null) return right;
            if (right == null) return left;

            // Both sides still have content — create new AND node (avoid mutating shared AST)
            return new BooleanBinaryExpression
            {
                BinaryExpressionType = BooleanBinaryExpressionType.And,
                FirstExpression = left,
                SecondExpression = right
            };
        }

        // For other expression types (OR, NOT wrapping non-EXISTS, etc.), don't extract
        return expr;
    }

    /// <summary>
    /// Extracts the alias of the primary (leftmost) table in the FROM clause, if present.
    /// Used to match correlation predicates in EXISTS subqueries (e.g., WHERE c.parentcustomerid = a.accountid
    /// where "a" is the alias for the outer table).
    /// </summary>
    private static string? ExtractOuterTableAlias(QuerySpecification querySpec)
    {
        if (querySpec.FromClause?.TableReferences.Count > 0)
        {
            return ExtractAliasFromTableReference(querySpec.FromClause.TableReferences[0]);
        }
        return null;
    }

    private static string? ExtractAliasFromTableReference(TableReference tableRef)
    {
        return tableRef switch
        {
            NamedTableReference named => named.Alias?.Value,
            QualifiedJoin join => ExtractAliasFromTableReference(join.FirstTableReference),
            UnqualifiedJoin unqualified => ExtractAliasFromTableReference(unqualified.FirstTableReference),
            _ => null
        };
    }

    /// <summary>
    /// Extracts the correlation predicate from the inner WHERE clause of an EXISTS subquery.
    /// Finds a BooleanComparisonExpression with equality where one side references the outer
    /// table (by entity name or alias) and the other references the inner table.
    /// </summary>
    private static (string outerCol, string innerCol)? ExtractCorrelationFromExists(
        ExistsPredicate exists,
        string outerEntityName,
        string? outerAlias)
    {
        var innerQuery = exists.Subquery.QueryExpression as QuerySpecification;
        if (innerQuery?.WhereClause?.SearchCondition == null)
            return null;

        return FindCorrelationPredicate(innerQuery.WhereClause.SearchCondition, outerEntityName, outerAlias);
    }

    /// <summary>
    /// Recursively walks a boolean expression tree looking for a correlation predicate:
    /// a BooleanComparisonExpression with equality where one ColumnReference references the
    /// outer table (matched by entity name or alias) and the other references the inner table.
    /// </summary>
    private static (string outerCol, string innerCol)? FindCorrelationPredicate(
        BooleanExpression expr,
        string outerEntityName,
        string? outerAlias)
    {
        if (expr is BooleanComparisonExpression comp
            && comp.ComparisonType == BooleanComparisonType.Equals
            && comp.FirstExpression is ColumnReferenceExpression left
            && comp.SecondExpression is ColumnReferenceExpression right)
        {
            var leftParts = left.MultiPartIdentifier.Identifiers;
            var rightParts = right.MultiPartIdentifier.Identifiers;

            var leftQualifier = leftParts.Count > 1 ? leftParts[0].Value : null;
            var rightQualifier = rightParts.Count > 1 ? rightParts[0].Value : null;

            var leftCol = leftParts[leftParts.Count - 1].Value;
            var rightCol = rightParts[rightParts.Count - 1].Value;

            // Check if left side references the outer table (by alias or entity name)
            if (IsOuterReference(leftQualifier, outerEntityName, outerAlias))
                return (leftCol, rightCol);

            // Check if right side references the outer table (by alias or entity name)
            if (IsOuterReference(rightQualifier, outerEntityName, outerAlias))
                return (rightCol, leftCol);
        }

        // Check inside AND conjuncts
        if (expr is BooleanBinaryExpression bin)
        {
            return FindCorrelationPredicate(bin.FirstExpression, outerEntityName, outerAlias)
                ?? FindCorrelationPredicate(bin.SecondExpression, outerEntityName, outerAlias);
        }

        // Check inside parenthesized expressions
        if (expr is BooleanParenthesisExpression paren)
        {
            return FindCorrelationPredicate(paren.Expression, outerEntityName, outerAlias);
        }

        return null;
    }

    /// <summary>
    /// Returns true if the given qualifier matches the outer table by alias or entity name.
    /// </summary>
    private static bool IsOuterReference(string? qualifier, string outerEntityName, string? outerAlias)
    {
        if (qualifier == null) return false;

        if (outerAlias != null && qualifier.Equals(outerAlias, StringComparison.OrdinalIgnoreCase))
            return true;

        if (qualifier.Equals(outerEntityName, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private (IQueryPlanNode node, string entityName) PlanJoinTree(
        QualifiedJoin join,
        QueryPlanOptions options)
    {
        var leftResult = PlanTableReference(join.FirstTableReference, options);
        var rightResult = PlanTableReference(join.SecondTableReference, options);

        // Extract join columns from ON condition (supports multi-column keys)
        var joinColumns = ExtractJoinColumns(join.SearchCondition);
        var leftCols = joinColumns.Select(c => c.leftCol).ToArray();
        var rightCols = joinColumns.Select(c => c.rightCol).ToArray();

        // Map ScriptDom join type to our JoinType
        var joinType = join.QualifiedJoinType switch
        {
            QualifiedJoinType.Inner => JoinType.Inner,
            QualifiedJoinType.LeftOuter => JoinType.Left,
            QualifiedJoinType.RightOuter => JoinType.Right,
            QualifiedJoinType.FullOuter => JoinType.FullOuter,
            _ => JoinType.Inner
        };

        // RIGHT JOIN optimization: swap children and convert to LEFT JOIN
        if (joinType == JoinType.Right)
        {
            (leftResult, rightResult) = (rightResult, leftResult);
            (leftCols, rightCols) = (rightCols, leftCols);
            joinType = JoinType.Left;
        }

        // Use HashJoin for best general-purpose performance on unsorted data
        var joinNode = new HashJoinNode(
            leftResult.node, rightResult.node,
            leftCols, rightCols, joinType);

        return (joinNode, leftResult.entityName);
    }

    private (IQueryPlanNode node, string entityName) PlanTableReference(
        TableReference tableRef,
        QueryPlanOptions options)
    {
        if (tableRef is QualifiedJoin nestedJoin)
            return PlanJoinTree(nestedJoin, options);

        if (tableRef is UnqualifiedJoin unqualified)
            return PlanUnqualifiedJoin(unqualified, options);

        if (tableRef is QueryDerivedTable derived)
            return PlanDerivedTable(derived, options);

        if (tableRef is NamedTableReference named)
        {
            // Cross-environment reference: ScriptDom parses [LABEL].dbo.entity as a 3-part name
            // with DatabaseIdentifier="LABEL", or [SERVER].[DB].dbo.entity as 4-part with
            // ServerIdentifier="SERVER". Either indicates a cross-environment reference.
            var profileLabel = named.SchemaObject.ServerIdentifier?.Value
                ?? named.SchemaObject.DatabaseIdentifier?.Value;

            if (profileLabel != null)
            {
                return PlanRemoteTableReference(named, profileLabel, options);
            }

            // Smart label detection for 2-part names: [LABEL].entity is parsed as
            // SchemaIdentifier=LABEL, BaseIdentifier=entity. If schema is not "dbo"
            // and a RemoteExecutorFactory is configured, check if it matches a profile label.
            // If no factory is configured, non-dbo schemas still fail — never silently query local.
            var schemaId = named.SchemaObject.SchemaIdentifier?.Value;
            if (schemaId != null
                && !string.Equals(schemaId, "dbo", StringComparison.OrdinalIgnoreCase))
            {
                if (options.RemoteExecutorFactory != null)
                    return PlanRemoteTableReference(named, schemaId, options);

                throw new QueryParseException(
                    $"No environment found matching '{schemaId}'. " +
                    $"Configure a profile with label '{schemaId}' to use cross-environment queries.");
            }

            var entityName = GetMultiPartName(named.SchemaObject);
            var fetchXml = $"<fetch><entity name=\"{entityName}\"><all-attributes /></entity></fetch>";
            var scanNode = new FetchXmlScanNode(fetchXml, entityName);
            return (scanNode, entityName);
        }

        throw new QueryParseException($"Unsupported table reference type in client-side join: {tableRef.GetType().Name}");
    }

    /// <summary>
    /// Plans a cross-environment table reference ([LABEL].entity).
    /// Resolves the profile label, creates a RemoteScanNode for FetchXML execution
    /// against the remote environment, and wraps it in a TableSpoolNode for materialization.
    /// </summary>
    private static (IQueryPlanNode node, string entityName) PlanRemoteTableReference(
        NamedTableReference named,
        string profileLabel,
        QueryPlanOptions options)
    {
        if (options.RemoteExecutorFactory == null)
            throw new QueryParseException(
                $"Cross-environment query references '[{profileLabel}]' but no remote executor factory is configured.");

        var remoteExecutor = options.RemoteExecutorFactory(profileLabel)
            ?? throw new QueryParseException(
                $"No environment found matching label '{profileLabel}'. Configure a profile with label '{profileLabel}' to use cross-environment queries.");

        var entityName = named.SchemaObject.BaseIdentifier.Value;
        var fetchXml = $"<fetch><entity name=\"{entityName}\"><all-attributes /></entity></fetch>";

        var remoteScan = new RemoteScanNode(fetchXml, entityName, profileLabel, remoteExecutor);
        var spool = new TableSpoolNode(remoteScan);

        return (spool, entityName);
    }

    /// <summary>
    /// Plans a derived table (subquery in FROM clause).
    /// The inner SELECT is planned normally via <see cref="PlanQueryExpressionAsSelect"/>,
    /// then materialized into a <see cref="TableSpoolNode"/> so the outer query can scan it.
    /// </summary>
    private (IQueryPlanNode node, string entityName) PlanDerivedTable(
        QueryDerivedTable derived,
        QueryPlanOptions options)
    {
        // Plan the inner query
        var innerResult = PlanQueryExpressionAsSelect(derived.QueryExpression, options);

        // Wrap in TableSpoolNode so the outer query can scan it
        var spool = new TableSpoolNode(innerResult.RootNode);

        // Use the alias as the entity name for column references
        var alias = derived.Alias?.Value ?? "derived";

        return (spool, alias);
    }

    /// <summary>
    /// Plans an unqualified join (CROSS JOIN, CROSS APPLY, OUTER APPLY).
    /// CROSS JOIN produces a NestedLoopJoinNode with JoinType.Cross.
    /// CROSS APPLY and OUTER APPLY require correlated subquery infrastructure (Task 7)
    /// and throw a clear error for now.
    /// </summary>
    private (IQueryPlanNode node, string entityName) PlanUnqualifiedJoin(
        UnqualifiedJoin join,
        QueryPlanOptions options)
    {
        if (join.UnqualifiedJoinType is UnqualifiedJoinType.CrossApply)
            throw new QueryParseException("CROSS APPLY is not yet supported in this execution path.");
        if (join.UnqualifiedJoinType is UnqualifiedJoinType.OuterApply)
            throw new QueryParseException("OUTER APPLY is not yet supported in this execution path.");

        var leftResult = PlanTableReference(join.FirstTableReference, options);
        var rightResult = PlanTableReference(join.SecondTableReference, options);

        IQueryPlanNode joinNode = join.UnqualifiedJoinType switch
        {
            UnqualifiedJoinType.CrossJoin => new NestedLoopJoinNode(
                leftResult.node, rightResult.node, null, null, JoinType.Cross),

            _ => throw new QueryParseException($"Unsupported unqualified join type: {join.UnqualifiedJoinType}")
        };

        return (joinNode, leftResult.entityName);
    }

    /// <summary>
    /// Checks whether a FROM clause contains any <see cref="UnqualifiedJoin"/>
    /// (CROSS JOIN, CROSS APPLY, OUTER APPLY) at any nesting level.
    /// </summary>
    private static bool ContainsUnqualifiedJoin(FromClause? fromClause)
    {
        if (fromClause == null) return false;
        foreach (var tableRef in fromClause.TableReferences)
        {
            if (ContainsUnqualifiedJoinInTableRef(tableRef))
                return true;
        }
        return false;
    }

    private static bool ContainsUnqualifiedJoinInTableRef(TableReference tableRef)
    {
        if (tableRef is UnqualifiedJoin)
            return true;
        if (tableRef is QualifiedJoin qualified)
            return ContainsUnqualifiedJoinInTableRef(qualified.FirstTableReference)
                || ContainsUnqualifiedJoinInTableRef(qualified.SecondTableReference);
        return false;
    }

    /// <summary>
    /// Checks whether a FROM clause contains any <see cref="QueryDerivedTable"/>
    /// (subquery in FROM, e.g. <c>SELECT * FROM (SELECT ...) AS sub</c>) at any nesting level.
    /// </summary>
    private static bool ContainsDerivedTable(FromClause? fromClause)
    {
        if (fromClause == null) return false;
        foreach (var tableRef in fromClause.TableReferences)
        {
            if (ContainsDerivedTableInTableRef(tableRef))
                return true;
        }
        return false;
    }

    private static bool ContainsDerivedTableInTableRef(TableReference tableRef)
    {
        if (tableRef is QueryDerivedTable)
            return true;
        if (tableRef is QualifiedJoin qualified)
            return ContainsDerivedTableInTableRef(qualified.FirstTableReference)
                || ContainsDerivedTableInTableRef(qualified.SecondTableReference);
        if (tableRef is UnqualifiedJoin unqualified)
            return ContainsDerivedTableInTableRef(unqualified.FirstTableReference)
                || ContainsDerivedTableInTableRef(unqualified.SecondTableReference);
        return false;
    }

    /// <summary>
    /// Checks whether a FROM clause contains any cross-environment reference
    /// (e.g. <c>[UAT].dbo.account</c> or <c>[UAT].account</c>) at any nesting level.
    /// </summary>
    private static bool ContainsCrossEnvironmentReference(FromClause? fromClause)
    {
        if (fromClause == null) return false;
        foreach (var tableRef in fromClause.TableReferences)
        {
            if (ContainsCrossEnvironmentReferenceInTableRef(tableRef))
                return true;
        }
        return false;
    }

    private static bool ContainsCrossEnvironmentReferenceInTableRef(TableReference tableRef)
    {
        if (tableRef is NamedTableReference named)
        {
            // 3/4-part names: [LABEL].dbo.entity or [SERVER].[DB].dbo.entity
            if (named.SchemaObject.ServerIdentifier != null || named.SchemaObject.DatabaseIdentifier != null)
                return true;

            // 2-part names: [LABEL].entity where schema is not "dbo"
            // Non-dbo schemas are always cross-env (or error) — never silently local
            var schema = named.SchemaObject.SchemaIdentifier?.Value;
            if (schema != null && !string.Equals(schema, "dbo", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (tableRef is QualifiedJoin qualified)
            return ContainsCrossEnvironmentReferenceInTableRef(qualified.FirstTableReference)
                || ContainsCrossEnvironmentReferenceInTableRef(qualified.SecondTableReference);
        if (tableRef is UnqualifiedJoin unqualified)
            return ContainsCrossEnvironmentReferenceInTableRef(unqualified.FirstTableReference)
                || ContainsCrossEnvironmentReferenceInTableRef(unqualified.SecondTableReference);
        return false;
    }

    private static IReadOnlyList<(string leftCol, string rightCol)> ExtractJoinColumns(BooleanExpression searchCondition)
    {
        var result = new List<(string leftCol, string rightCol)>();
        ExtractJoinColumnsRecursive(searchCondition, result);
        if (result.Count == 0)
            throw new QueryParseException("Client-side JOIN ON condition must be equality comparisons (e.g., a.id = b.id).");
        return result;
    }

    private static void ExtractJoinColumnsRecursive(BooleanExpression expr, List<(string leftCol, string rightCol)> result)
    {
        if (expr is BooleanComparisonExpression comp
            && comp.ComparisonType == BooleanComparisonType.Equals
            && comp.FirstExpression is ColumnReferenceExpression leftRef
            && comp.SecondExpression is ColumnReferenceExpression rightRef)
        {
            var leftCol = leftRef.MultiPartIdentifier.Identifiers[leftRef.MultiPartIdentifier.Identifiers.Count - 1].Value;
            var rightCol = rightRef.MultiPartIdentifier.Identifiers[rightRef.MultiPartIdentifier.Identifiers.Count - 1].Value;
            result.Add((leftCol, rightCol));
            return;
        }

        if (expr is BooleanBinaryExpression bin
            && bin.BinaryExpressionType == BooleanBinaryExpressionType.And)
        {
            ExtractJoinColumnsRecursive(bin.FirstExpression, result);
            ExtractJoinColumnsRecursive(bin.SecondExpression, result);
            return;
        }

        throw new QueryParseException("Client-side JOIN ON condition must be equality comparisons joined with AND (e.g., a.id = b.id AND a.type = b.type).");
    }
}
