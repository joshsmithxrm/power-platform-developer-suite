using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Dataverse.Sql.Ast;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Query.Planning;

/// <summary>
/// Optimizes a <see cref="QueryPlanResult"/> by applying transformation passes
/// to the plan node tree. Current optimization passes:
/// <list type="number">
///   <item><description>Predicate pushdown: moves client-side WHERE conditions into FetchXML where possible.</description></item>
///   <item><description>Constant folding: evaluates constant expressions at plan time (e.g., 1 + 1 becomes 2).</description></item>
///   <item><description>Sort elimination: removes unnecessary client-side sort when data is already ordered.</description></item>
/// </list>
/// </summary>
public sealed class ExecutionPlanOptimizer
{
    /// <summary>
    /// Optimizes a query plan by applying all optimization passes.
    /// </summary>
    /// <param name="plan">The unoptimized query plan.</param>
    /// <returns>The optimized query plan (may be the same instance if no optimizations apply).</returns>
    public QueryPlanResult Optimize(QueryPlanResult plan)
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));

        var optimizedRoot = plan.RootNode;
        var optimizedFetchXml = plan.FetchXml;

        // Pass 1: Predicate pushdown
        var (pushdownRoot, pushdownFetchXml) = ApplyPredicatePushdown(optimizedRoot, optimizedFetchXml);
        optimizedRoot = pushdownRoot;
        optimizedFetchXml = pushdownFetchXml;

        // Pass 2: Constant folding
        optimizedRoot = ApplyConstantFolding(optimizedRoot);

        // Pass 3: Sort elimination (remove redundant client sorts)
        optimizedRoot = ApplySortElimination(optimizedRoot);

        // If nothing changed, return original
        if (ReferenceEquals(optimizedRoot, plan.RootNode) && optimizedFetchXml == plan.FetchXml)
        {
            return plan;
        }

        return new QueryPlanResult
        {
            RootNode = optimizedRoot,
            FetchXml = optimizedFetchXml,
            VirtualColumns = plan.VirtualColumns,
            EntityLogicalName = plan.EntityLogicalName
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Pass 1: Predicate Pushdown
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Identifies client-side filter conditions that can be expressed in FetchXML
    /// and pushes them into the FetchXML scan node, removing the ClientFilterNode.
    /// </summary>
    /// <remarks>
    /// With compiled predicates, the optimizer cannot inspect condition structure directly.
    /// Predicate pushdown is now handled at plan-build time: the planner only creates a
    /// ClientFilterNode for conditions that truly cannot be expressed in FetchXML (e.g.,
    /// column-to-column comparisons). Simple comparisons are pushed into FetchXML during
    /// planning and never produce a ClientFilterNode, so this pass is a no-op.
    /// </remarks>
    private static (IQueryPlanNode root, string fetchXml) ApplyPredicatePushdown(
        IQueryPlanNode root, string fetchXml)
    {
        // Predicate pushdown is now handled at plan-build time.
        // The planner only creates ClientFilterNode for non-pushable conditions,
        // so there is nothing to push down at optimization time.
        return (root, fetchXml);
    }

    /// <summary>
    /// Determines if a condition can be expressed in FetchXML.
    /// Simple column comparisons to literal values are pushable.
    /// Expression conditions (column-to-column, computed) are not.
    /// </summary>
    internal static bool CanPushToFetchXml(ISqlCondition condition)
    {
        return condition switch
        {
            SqlComparisonCondition => true,
            SqlLogicalCondition logical => logical.Conditions.All(CanPushToFetchXml),
            SqlExpressionCondition => false,
            _ => false
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Pass 2: Constant Folding
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walks the plan tree and replaces constant expressions in filter/project nodes.
    /// For example, a filter condition comparing a column to (1 + 1) folds to 2.
    /// </summary>
    /// <remarks>
    /// With compiled predicates, constant folding cannot inspect the predicate structure.
    /// Constant folding for filter conditions is now performed at compile time by the
    /// ExpressionCompiler. This pass still recurses into children for project nodes and
    /// other structural optimizations.
    /// </remarks>
    private static IQueryPlanNode ApplyConstantFolding(IQueryPlanNode node)
    {
        if (node is ClientFilterNode filterNode)
        {
            // Compiled predicate: recurse into children but cannot fold the predicate itself
            var foldedInput = ApplyConstantFolding(filterNode.Input);
            if (!ReferenceEquals(foldedInput, filterNode.Input))
            {
                return new ClientFilterNode(foldedInput, filterNode.Predicate, filterNode.PredicateDescription);
            }
        }

        // Recurse into known node types that wrap children
        if (node is ProjectNode projectNode)
        {
            var foldedInput = ApplyConstantFolding(projectNode.Children[0]);
            if (!ReferenceEquals(foldedInput, projectNode.Children[0]))
            {
                return new ProjectNode(foldedInput, GetProjectColumns(projectNode));
            }
        }

        return node;
    }

    /// <summary>
    /// Folds constant expressions within a condition.
    /// </summary>
    internal static ISqlCondition FoldCondition(ISqlCondition condition)
    {
        return condition switch
        {
            SqlExpressionCondition exprCond => FoldExpressionCondition(exprCond),
            SqlLogicalCondition logical => FoldLogicalCondition(logical),
            _ => condition
        };
    }

    private static ISqlCondition FoldExpressionCondition(SqlExpressionCondition condition)
    {
        var foldedLeft = FoldExpression(condition.Left);
        var foldedRight = FoldExpression(condition.Right);

        if (!ReferenceEquals(foldedLeft, condition.Left) || !ReferenceEquals(foldedRight, condition.Right))
        {
            return new SqlExpressionCondition(foldedLeft, condition.Operator, foldedRight);
        }

        return condition;
    }

    private static ISqlCondition FoldLogicalCondition(SqlLogicalCondition logical)
    {
        var folded = new List<ISqlCondition>();
        var changed = false;

        foreach (var child in logical.Conditions)
        {
            var foldedChild = FoldCondition(child);
            folded.Add(foldedChild);
            if (!ReferenceEquals(foldedChild, child))
            {
                changed = true;
            }
        }

        return changed ? new SqlLogicalCondition(logical.Operator, folded) : logical;
    }

    /// <summary>
    /// Folds a constant expression. If the expression is a binary operation on two literals,
    /// computes the result at plan time.
    /// </summary>
    internal static ISqlExpression FoldExpression(ISqlExpression expression)
    {
        if (expression is SqlBinaryExpression binary)
        {
            var foldedLeft = FoldExpression(binary.Left);
            var foldedRight = FoldExpression(binary.Right);

            // If both sides are now literals, compute the result
            if (foldedLeft is SqlLiteralExpression leftLit &&
                foldedRight is SqlLiteralExpression rightLit)
            {
                var result = EvaluateConstantBinary(leftLit.Value, binary.Operator, rightLit.Value);
                if (result != null)
                {
                    return result;
                }
            }

            if (!ReferenceEquals(foldedLeft, binary.Left) || !ReferenceEquals(foldedRight, binary.Right))
            {
                return new SqlBinaryExpression(foldedLeft, binary.Operator, foldedRight);
            }
        }

        return expression;
    }

    /// <summary>
    /// Evaluates a binary operation on two literal values at plan time.
    /// Returns null if evaluation is not possible.
    /// </summary>
    private static SqlLiteralExpression? EvaluateConstantBinary(
        SqlLiteral left, SqlBinaryOperator op, SqlLiteral right)
    {
        // Only fold numeric operations
        if (!TryGetNumber(left, out var leftNum) || !TryGetNumber(right, out var rightNum))
        {
            return null;
        }

        decimal result;
        switch (op)
        {
            case SqlBinaryOperator.Add:
                result = leftNum + rightNum;
                break;
            case SqlBinaryOperator.Subtract:
                result = leftNum - rightNum;
                break;
            case SqlBinaryOperator.Multiply:
                result = leftNum * rightNum;
                break;
            case SqlBinaryOperator.Divide:
                if (rightNum == 0) return null; // Avoid division by zero at plan time
                result = leftNum / rightNum;
                break;
            case SqlBinaryOperator.Modulo:
                if (rightNum == 0) return null;
                result = leftNum % rightNum;
                break;
            default:
                return null;
        }

        return new SqlLiteralExpression(
            SqlLiteral.Number(result.ToString(CultureInfo.InvariantCulture)));
    }

    private static bool TryGetNumber(SqlLiteral literal, out decimal value)
    {
        value = 0;
        if (literal.Type != SqlLiteralType.Number)
            return false;

        return decimal.TryParse(literal.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Checks if a folded condition is always true (tautology).
    /// This is a simple heuristic check for cases like 1 = 1.
    /// </summary>
    private static bool IsAlwaysTrue(ISqlCondition condition)
    {
        if (condition is SqlExpressionCondition exprCond &&
            exprCond.Left is SqlLiteralExpression leftLit &&
            exprCond.Right is SqlLiteralExpression rightLit &&
            exprCond.Operator == SqlComparisonOperator.Equal)
        {
            return string.Equals(
                leftLit.Value.Value,
                rightLit.Value.Value,
                StringComparison.Ordinal);
        }

        return false;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Pass 3: Sort Elimination
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Removes redundant sort operations when data is already ordered.
    /// For example, if FetchXML already contains an ORDER BY clause matching the
    /// client sort, the client-side sort is unnecessary.
    /// </summary>
    private static IQueryPlanNode ApplySortElimination(IQueryPlanNode node)
    {
        // Pattern: look for a sort node wrapping a FetchXmlScanNode whose FetchXML
        // already contains a matching order-by.
        // Since we don't have an explicit SortNode in the current plan tree (sorts are
        // pushed into FetchXML), this pass is a no-op for now but establishes the
        // framework for when explicit SortNode is added.
        return node;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static IReadOnlyList<ProjectColumn> GetProjectColumns(ProjectNode node)
    {
        return node.OutputColumns;
    }
}
