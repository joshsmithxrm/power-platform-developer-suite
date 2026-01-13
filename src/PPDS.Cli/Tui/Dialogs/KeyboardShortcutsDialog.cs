using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog displaying keyboard shortcuts help.
/// Uses custom dialog instead of MessageBox to avoid Terminal.Gui rendering bugs.
/// </summary>
internal sealed class KeyboardShortcutsDialog : TuiDialog, ITuiStateCapture<KeyboardShortcutsDialogState>
{
    private static readonly IReadOnlyList<ShortcutEntry> Shortcuts = new List<ShortcutEntry>
    {
        new("Alt+P", "Switch profile", "Global"),
        new("Alt+E", "Switch environment", "Global"),
        new("F1", "Keyboard shortcuts", "Global"),
        new("F12", "Error Log", "Global"),
        new("Ctrl+Q", "Quit", "Global"),
        new("Ctrl+Enter", "Execute query", "SQL Query"),
        new("Ctrl+E", "Export results", "SQL Query"),
        new("Ctrl+H", "Query history", "SQL Query"),
        new("/", "Filter results", "SQL Query"),
        new("Esc", "Close filter or exit", "SQL Query"),
    };
    /// <summary>
    /// Creates a new keyboard shortcuts dialog.
    /// </summary>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public KeyboardShortcutsDialog(InteractiveSession? session = null) : base("Keyboard Shortcuts", session)
    {
        Width = 55;
        Height = 24;

        var content = new Label(
            "Global Shortcuts (work everywhere):\n" +
            "  Alt+P    - Switch profile\n" +
            "  Alt+E    - Switch environment\n" +
            "  F1       - This help\n" +
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
    }

    /// <inheritdoc />
    public KeyboardShortcutsDialogState CaptureState() => new(
        Title: Title?.ToString() ?? string.Empty,
        Shortcuts: Shortcuts,
        ShortcutCount: Shortcuts.Count);
}
