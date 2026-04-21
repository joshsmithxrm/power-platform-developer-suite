using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Services.PluginTraces;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog showing a text-based waterfall timeline of plugin execution.
/// </summary>
internal sealed class TraceTimelineDialog : TuiDialog
{
    private const int BarMaxWidth = 40;

    /// <summary>
    /// Creates a new trace timeline dialog.
    /// </summary>
    /// <param name="nodes">The timeline nodes to display.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public TraceTimelineDialog(List<TimelineNode> nodes, InteractiveSession? session = null)
        : base("Execution Timeline", session)
    {
        Width = Dim.Percent(90);
        Height = Dim.Percent(80);

        var lines = BuildTimelineLines(nodes);

        var listView = new ListView(lines)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            ColorScheme = TuiColorPalette.Default
        };

        var closeButton = new Button("_Close")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(listView, closeButton);
    }

    private static List<string> BuildTimelineLines(List<TimelineNode> nodes)
    {
        var lines = new List<string>();
        FlattenNodes(nodes, lines);

        if (lines.Count == 0)
        {
            lines.Add("No timeline data available.");
        }

        return lines;
    }

    private static void FlattenNodes(IReadOnlyList<TimelineNode> nodes, List<string> lines)
    {
        foreach (var node in nodes)
        {
            var indent = new string(' ', node.HierarchyDepth * 2);
            var typeName = TruncateText(node.Trace.TypeName, 30);
            var message = node.Trace.MessageName ?? "";
            var duration = node.Trace.DurationMs?.ToString() ?? "?";
            var status = node.Trace.HasException ? " [ERR]" : "";

            // Build proportional bar
            var barWidth = Math.Max(1, (int)(node.WidthPercent / 100.0 * BarMaxWidth));
            var bar = new string('\u2588', barWidth);

            var line = $"{indent}{typeName} {message} ({duration}ms){status}  {bar}";
            lines.Add(line);

            if (node.Children.Count > 0)
            {
                FlattenNodes(node.Children, lines);
            }
        }
    }

    private static string TruncateText(string text, int maxLength)
    {
        if (text.Length <= maxLength) return text;
        return text[..(maxLength - 3)] + "...";
    }
}
