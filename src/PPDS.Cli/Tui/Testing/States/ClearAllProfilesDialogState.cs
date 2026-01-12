namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ClearAllProfilesDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="WarningMessage">The warning message displayed.</param>
/// <param name="ProfileCount">Number of profiles that will be deleted.</param>
/// <param name="ConfirmButtonText">Text on the confirm button.</param>
/// <param name="CancelButtonText">Text on the cancel button.</param>
public sealed record ClearAllProfilesDialogState(
    string Title,
    string WarningMessage,
    int ProfileCount,
    string ConfirmButtonText,
    string CancelButtonText);
