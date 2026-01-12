namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Information about a keyboard shortcut.
/// </summary>
/// <param name="Key">The key combination (e.g., "F1", "Alt+P").</param>
/// <param name="Description">What the shortcut does.</param>
/// <param name="Scope">The scope where the shortcut is active (Global/Screen/Dialog).</param>
public sealed record ShortcutEntry(
    string Key,
    string Description,
    string Scope);

/// <summary>
/// Captures the state of the KeyboardShortcutsDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="Shortcuts">List of keyboard shortcuts.</param>
/// <param name="ShortcutCount">Total number of shortcuts displayed.</param>
public sealed record KeyboardShortcutsDialogState(
    string Title,
    IReadOnlyList<ShortcutEntry> Shortcuts,
    int ShortcutCount);
