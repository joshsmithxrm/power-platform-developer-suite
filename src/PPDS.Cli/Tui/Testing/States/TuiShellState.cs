using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the TuiShell for testing.
/// </summary>
/// <param name="Title">The window title text.</param>
/// <param name="MenuBarItems">List of top-level menu item labels.</param>
/// <param name="StatusBar">The status bar state.</param>
/// <param name="CurrentScreenTitle">Title of the current screen (null if showing main menu).</param>
/// <param name="CurrentScreenType">Type name of the current screen (null if showing main menu).</param>
/// <param name="IsMainMenuVisible">Whether the main menu content is displayed.</param>
/// <param name="TabCount">Number of open tabs.</param>
/// <param name="HasErrors">Whether there are errors in the error service.</param>
/// <param name="ErrorCount">Number of errors in the error service.</param>
public sealed record TuiShellState(
    string Title,
    IReadOnlyList<string> MenuBarItems,
    TuiStatusBarState StatusBar,
    string? CurrentScreenTitle,
    string? CurrentScreenType,
    bool IsMainMenuVisible,
    int TabCount,
    bool HasErrors,
    int ErrorCount);
