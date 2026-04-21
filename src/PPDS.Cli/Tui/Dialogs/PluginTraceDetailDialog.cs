using PPDS.Cli.Services.PluginTraces;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog showing full details of a plugin trace log with tabbed sections.
/// </summary>
internal sealed class PluginTraceDetailDialog : TuiDialog
{
    /// <summary>
    /// Creates a new plugin trace detail dialog.
    /// </summary>
    /// <param name="detail">The trace detail to display.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public PluginTraceDetailDialog(PluginTraceDetail detail, InteractiveSession? session = null)
        : base("Trace Detail", session)
    {
        Width = Dim.Percent(80);
        Height = Dim.Percent(80);

        var tabView = new TabView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };

        // Details tab
        var detailsView = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        BuildDetailsTab(detailsView, detail);
        tabView.AddTab(new TabView.Tab("Details", detailsView), andSelect: true);

        // Exception tab
        var exceptionView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Text = !string.IsNullOrEmpty(detail.ExceptionDetails) ? detail.ExceptionDetails : "No exception",
            ColorScheme = TuiColorPalette.ReadOnlyText
        };
        tabView.AddTab(new TabView.Tab("Exception", exceptionView), andSelect: false);

        // Message Block tab
        var messageView = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            Text = !string.IsNullOrEmpty(detail.MessageBlock) ? detail.MessageBlock : "No message block",
            ColorScheme = TuiColorPalette.ReadOnlyText
        };
        tabView.AddTab(new TabView.Tab("Message Block", messageView), andSelect: false);

        // Configuration tab
        var configView = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        BuildConfigTab(configView, detail);
        tabView.AddTab(new TabView.Tab("Configuration", configView), andSelect: false);

        // Close button
        var closeButton = new Button("_Close")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(tabView, closeButton);
    }

    private static void BuildDetailsTab(View container, PluginTraceDetail detail)
    {
        const int labelWidth = 18;
        int row = 1;

        AddDetailRow(container, "Type:", detail.TypeName, ref row, labelWidth);
        AddDetailRow(container, "Message:", detail.MessageName ?? "\u2014", ref row, labelWidth);
        AddDetailRow(container, "Entity:", detail.PrimaryEntity ?? "\u2014", ref row, labelWidth);
        AddDetailRow(container, "Mode:", detail.Mode.ToString(), ref row, labelWidth);
        AddDetailRow(container, "Operation:", detail.OperationType.ToString(), ref row, labelWidth);
        AddDetailRow(container, "Depth:", detail.Depth.ToString(), ref row, labelWidth);
        AddDetailRow(container, "Duration (ms):", detail.DurationMs?.ToString() ?? "\u2014", ref row, labelWidth);
        AddDetailRow(container, "Created:", detail.CreatedOn.ToString("G"), ref row, labelWidth);
        AddDetailRow(container, "Correlation ID:", detail.CorrelationId?.ToString() ?? "\u2014", ref row, labelWidth);
        AddDetailRow(container, "Request ID:", detail.RequestId?.ToString() ?? "\u2014", ref row, labelWidth);
        AddDetailRow(container, "Has Exception:", detail.HasException ? "Yes" : "No", ref row, labelWidth);
    }

    private static void AddDetailRow(View container, string label, string value, ref int row, int labelWidth)
    {
        container.Add(new Label(label)
        {
            X = 1,
            Y = row,
            Width = labelWidth,
            ColorScheme = TuiColorPalette.TableHeader
        });
        container.Add(new Label(value)
        {
            X = labelWidth + 1,
            Y = row,
            Width = Dim.Fill(2)
        });
        row++;
    }

    private static void BuildConfigTab(View container, PluginTraceDetail detail)
    {
        var unsecuredLabel = new Label("Unsecured Configuration:")
        {
            X = 1,
            Y = 1,
            ColorScheme = TuiColorPalette.TableHeader
        };
        container.Add(unsecuredLabel);

        var unsecuredText = new TextView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Percent(40),
            ReadOnly = true,
            Text = !string.IsNullOrEmpty(detail.Configuration) ? detail.Configuration : "(empty)",
            ColorScheme = TuiColorPalette.ReadOnlyText
        };
        container.Add(unsecuredText);

        var securedLabel = new Label("Secured Configuration:")
        {
            X = 1,
            Y = Pos.Bottom(unsecuredText) + 1,
            ColorScheme = TuiColorPalette.TableHeader
        };
        container.Add(securedLabel);

        var securedText = new TextView
        {
            X = 1,
            Y = Pos.Bottom(securedLabel),
            Width = Dim.Fill(2),
            Height = Dim.Fill(1),
            ReadOnly = true,
            Text = !string.IsNullOrEmpty(detail.SecureConfiguration) ? detail.SecureConfiguration : "(not available)",
            ColorScheme = TuiColorPalette.ReadOnlyText
        };
        container.Add(securedText);
    }
}
