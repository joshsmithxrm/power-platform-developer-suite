using System.Text;

namespace PPDS.Dataverse.Query.Planning;

/// <summary>
/// Formats an execution plan tree as indented text for EXPLAIN output.
/// </summary>
public static class PlanFormatter
{
    /// <summary>
    /// Renders a plan description tree as a formatted string.
    /// </summary>
    public static string Format(QueryPlanDescription plan)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Execution Plan:");
        FormatNode(sb, plan, "", true);

        // Append metadata footer if available
        if (plan.PoolCapacity.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"Pool capacity: {plan.PoolCapacity.Value}");
        }
        if (plan.EffectiveParallelism.HasValue)
        {
            sb.AppendLine($"Effective parallelism: {plan.EffectiveParallelism.Value}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renders a plan node tree (from <see cref="IQueryPlanNode"/>) as a formatted string.
    /// </summary>
    public static string Format(IQueryPlanNode rootNode)
    {
        var plan = QueryPlanDescription.FromNode(rootNode);
        return Format(plan);
    }

    private static void FormatNode(StringBuilder sb, QueryPlanDescription node, string indent, bool isLast)
    {
        var connector = isLast ? "└── " : "├── ";
        var childIndent = indent + (isLast ? "    " : "│   ");

        sb.Append(indent);
        sb.Append(connector);
        sb.Append(node.Description);

        if (node.EstimatedRows >= 0)
        {
            sb.Append($" (est. {node.EstimatedRows:N0} rows)");
        }

        sb.AppendLine();

        for (var i = 0; i < node.Children.Count; i++)
        {
            FormatNode(sb, node.Children[i], childIndent, i == node.Children.Count - 1);
        }
    }
}
