using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Base class for TUI dialogs providing standardized hotkey registration,
/// escape handling, color scheme, and disposal patterns.
/// </summary>
/// <remarks>
/// All dialogs should inherit from this class to ensure:
/// - Consistent color scheme (TuiColorPalette.Default)
/// - Automatic hotkey registry integration (blocks screen-scope hotkeys while dialog is open)
/// - Standard Escape key handling (closes dialog by default, override OnEscapePressed for custom behavior)
/// - Proper cleanup on disposal
/// </remarks>
internal abstract class TuiDialog : Dialog
{
    private readonly IHotkeyRegistry? _hotkeyRegistry;

    /// <summary>
    /// Creates a new TUI dialog with standardized behavior.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="session">Optional session for hotkey registry integration. Pass null for simple dialogs.</param>
    protected TuiDialog(string title, InteractiveSession? session = null) : base(title)
    {
        // CodeQL [cs/virtual-call-in-constructor] Terminal.Gui requires ColorScheme to be set during construction
        ColorScheme = TuiColorPalette.Default;
        _hotkeyRegistry = session?.GetHotkeyRegistry();
        _hotkeyRegistry?.SetActiveDialog(this);

        KeyPress += (e) =>
        {
            if (e.KeyEvent.Key == Key.Esc)
            {
                OnEscapePressed();
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// Called when Escape is pressed. Default behavior closes the dialog.
    /// Override for custom behavior (e.g., confirm unsaved changes, cancel operations).
    /// </summary>
    protected virtual void OnEscapePressed()
    {
        Application.RequestStop();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkeyRegistry?.SetActiveDialog(null);
        }
        base.Dispose(disposing);
    }
}
