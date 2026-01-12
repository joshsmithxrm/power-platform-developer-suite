using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the MainWindow for testing.
/// </summary>
/// <param name="Title">The window title text.</param>
/// <param name="MenuBarItems">List of top-level menu item labels.</param>
/// <param name="StatusBar">The status bar state.</param>
/// <param name="WelcomeMessageVisible">Whether the welcome message is displayed.</param>
/// <param name="QuickActionButtons">List of quick action button labels.</param>
/// <param name="CurrentScreen">Name of the currently displayed screen (null if main menu).</param>
/// <param name="HasErrors">Whether there are errors in the error service.</param>
/// <param name="ErrorCount">Number of errors in the error service.</param>
public sealed record MainWindowState(
    string Title,
    IReadOnlyList<string> MenuBarItems,
    TuiStatusBarState StatusBar,
    bool WelcomeMessageVisible,
    IReadOnlyList<string> QuickActionButtons,
    string? CurrentScreen,
    bool HasErrors,
    int ErrorCount);
