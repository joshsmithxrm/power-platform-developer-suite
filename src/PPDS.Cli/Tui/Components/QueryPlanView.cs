using System.Text;
using PPDS.Dataverse.Query.Planning;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Components;

/// <summary>
/// Terminal.Gui View that renders a query execution plan as an indented tree.
/// Shows node type, description, estimated rows, and optional execution time.
/// </summary>
/// <remarks>
/// Tree format:
/// <code>
/// ProjectNode (3 cols)                    0.1ms    100 rows
/// └─ FilterNode (statecode = 0)          0.2ms    100 rows
///    └─ FetchXmlScanNode (account)       45ms     5432 rows
/// </code>
/// </remarks>
internal sealed class QueryPlanView : FrameView
{
    private readonly TextView _treeView;
    private QueryPlanDescription? _plan;
    private long _executionTimeMs;

    public QueryPlanView()
        : base("Execution Plan")
    {
        Width = Dim.Fill();
        Height = Dim.Fill();

        _treeView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = false,
            ColorScheme = Infrastructure.TuiColorPalette.ReadOnlyText
        };

        Add(_treeView);
    }

    /// <summary>
    /// Sets the execution plan to display.
    /// </summary>
    /// <param name="plan">The plan description tree.</param>
    /// <param name="executionTimeMs">Total query execution time in milliseconds.</param>
    public void SetPlan(QueryPlanDescription? plan, long executionTimeMs = 0)
    {
        _plan = plan;
        _executionTimeMs = executionTimeMs;
        _treeView.Text = plan != null
            ? FormatPlanTree(plan, executionTimeMs)
            : "(No execution plan available)";
    }

    /// <summary>
    /// Formats a plan description tree as a string for display.
    /// This is a static method so it can be tested independently of Terminal.Gui.
    /// </summary>
    internal static string FormatPlanTree(QueryPlanDescription plan, long totalExecutionTimeMs = 0)
    {
        var sb = new StringBuilder();
        FormatNode(sb, plan, "", true);

        if (plan.PoolCapacity.HasValue)
        {
            sb.AppendLine();
            sb.AppendLine($"Pool capacity: {plan.PoolCapacity.Value}");
        }
        if (plan.EffectiveParallelism.HasValue)
        {
            sb.AppendLine($"Effective parallelism: {plan.EffectiveParallelism.Value}");
        }
        if (totalExecutionTimeMs > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Total execution time: {FormatTime(totalExecutionTimeMs)}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void FormatNode(StringBuilder sb, QueryPlanDescription node, string indent, bool isLast)
    {
        var connector = indent.Length == 0 ? "" : (isLast ? "\u2514\u2500 " : "\u251c\u2500 ");
        var childIndent = indent.Length == 0 ? "" : indent + (isLast ? "   " : "\u2502  ");

        sb.Append(indent);
        sb.Append(connector);
        sb.Append(node.NodeType);

        if (!string.IsNullOrEmpty(node.Description) && node.Description != node.NodeType)
        {
            sb.Append($" ({node.Description})");
        }

        if (node.EstimatedRows >= 0)
        {
            sb.Append($"    est. {node.EstimatedRows:N0} rows");
        }

        sb.AppendLine();

        for (var i = 0; i < node.Children.Count; i++)
        {
            var nextIndent = indent.Length == 0 ? "   " : childIndent;
            FormatNode(sb, node.Children[i], nextIndent, i == node.Children.Count - 1);
        }
    }

    /// <summary>
    /// Formats a time value in milliseconds as a human-readable string.
    /// </summary>
    internal static string FormatTime(long ms)
    {
        if (ms < 1)
            return "<1ms";
        if (ms < 1000)
            return $"{ms}ms";
        if (ms < 60000)
            return $"{ms / 1000.0:F1}s";
        return $"{ms / 60000.0:F1}m";
    }
}
