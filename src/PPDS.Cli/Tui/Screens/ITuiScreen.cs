using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// Interface for screens that can be hosted in the TuiShell.
/// Screens provide content and optional screen-specific menus.
/// </summary>
internal interface ITuiScreen : IDisposable
{
    /// <summary>
    /// The content view to display in the shell's content area.
    /// </summary>
    View Content { get; }

    /// <summary>
    /// The title for the content frame.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Optional screen-specific menu items (inserted between File and Help).
    /// Return null if no screen-specific menus are needed.
    /// </summary>
    MenuBarItem[]? ScreenMenuItems { get; }

    /// <summary>
    /// Optional export action for File > Export menu.
    /// Return null if the screen doesn't support export or has nothing to export.
    /// </summary>
    Action? ExportAction => null;

    /// <summary>
    /// Raised when the screen wants to close (e.g., user pressed Escape).
    /// The shell subscribes to this and calls NavigateBack().
    /// </summary>
    event Action? CloseRequested;

    /// <summary>
    /// Raised when the screen's menu state changes (e.g., data becomes available for export).
    /// The shell subscribes to this and rebuilds the menu bar.
    /// </summary>
    event Action? MenuStateChanged;

    /// <summary>
    /// Called when the screen becomes active (after content is added to shell).
    /// Register screen-scope hotkeys here.
    /// </summary>
    /// <param name="hotkeyRegistry">The hotkey registry to register screen-scope hotkeys with.</param>
    void OnActivated(IHotkeyRegistry hotkeyRegistry);

    /// <summary>
    /// Called when the screen is about to be deactivated (before content removal).
    /// Unregister screen-scope hotkeys here.
    /// </summary>
    void OnDeactivating();
}
