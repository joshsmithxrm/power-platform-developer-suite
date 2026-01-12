namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Information about a registered hotkey.
/// </summary>
/// <param name="Key">The key combination.</param>
/// <param name="Scope">The scope (Global/Screen/Dialog).</param>
/// <param name="Description">Description of the hotkey action.</param>
public sealed record RegisteredHotkey(
    string Key,
    string Scope,
    string? Description);

/// <summary>
/// Captures the state of the HotkeyRegistry for testing.
/// </summary>
/// <param name="GlobalHotkeys">List of global hotkeys.</param>
/// <param name="ScreenHotkeys">List of screen-scoped hotkeys.</param>
/// <param name="DialogHotkeys">List of dialog-scoped hotkeys.</param>
/// <param name="TotalCount">Total number of registered hotkeys.</param>
public sealed record HotkeyRegistryState(
    IReadOnlyList<RegisteredHotkey> GlobalHotkeys,
    IReadOnlyList<RegisteredHotkey> ScreenHotkeys,
    IReadOnlyList<RegisteredHotkey> DialogHotkeys,
    int TotalCount);
