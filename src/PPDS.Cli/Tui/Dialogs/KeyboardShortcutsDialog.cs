using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog displaying keyboard shortcuts help.
/// Uses custom dialog instead of MessageBox to avoid Terminal.Gui rendering bugs.
/// </summary>
internal sealed class KeyboardShortcutsDialog : Dialog
{
    /// <summary>
    /// Creates a new keyboard shortcuts dialog.
    /// </summary>
    public KeyboardShortcutsDialog() : base("Keyboard Shortcuts")
    {
        Width = 55;
        Height = 24;
        ColorScheme = TuiColorPalette.Default;

        var content = new Label(
            "Global Shortcuts (work everywhere):\n" +
            "  Alt+P    - Switch profile\n" +
            "  Alt+E    - Switch environment\n" +
            "  F1       - This help\n" +
            "  F2       - SQL Query\n" +
            "  F12      - Error Log\n" +
            "  Ctrl+Q   - Quit\n\n" +
            "SQL Query Screen:\n" +
            "  Ctrl+Enter - Execute query\n" +
            "  Ctrl+E     - Export results\n" +
            "  Ctrl+H     - Query history\n" +
            "  /          - Filter results\n" +
            "  Esc        - Close filter or exit\n\n" +
            "Table Navigation:\n" +
            "  Arrows     - Navigate cells\n" +
            "  PgUp/Dn    - Page up/down\n" +
            "  Home/End   - First/last row\n" +
            "  Ctrl+C     - Copy cell")
        {
            X = 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill() - 2
        };

        var closeButton = new Button("_OK")
        {
            X = Pos.Center(),
            Y = Pos.AnchorEnd(1)
        };
        closeButton.Clicked += () => Application.RequestStop();

        Add(content, closeButton);

        // Handle Escape to close
        KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.Esc)
            {
                Application.RequestStop();
                e.Handled = true;
            }
        };
    }
}
