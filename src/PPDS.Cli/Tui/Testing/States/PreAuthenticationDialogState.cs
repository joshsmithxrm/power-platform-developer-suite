namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the PreAuthenticationDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="Message">The informational message.</param>
/// <param name="SelectedOption">The currently selected auth option (OpenBrowser/UseDeviceCode).</param>
/// <param name="AvailableOptions">List of available options.</param>
public sealed record PreAuthenticationDialogState(
    string Title,
    string Message,
    string? SelectedOption,
    IReadOnlyList<string> AvailableOptions);
