using System;
using System.Collections.Generic;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;

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

    // ═══════════════════════════════════════════════════════════════════
    //  Pass 2: Constant Folding
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walks the plan tree and replaces constant expressions in filter/project nodes.
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
