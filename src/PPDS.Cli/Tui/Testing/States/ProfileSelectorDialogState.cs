namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ProfileSelectorDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="Profiles">List of available profile names.</param>
/// <param name="SelectedIndex">Currently selected index (-1 if none).</param>
/// <param name="SelectedProfileName">Name of the selected profile (null if none).</param>
/// <param name="IsLoading">Whether the dialog is loading profiles.</param>
/// <param name="HasCreateButton">Whether the create button is available.</param>
/// <param name="HasDetailsButton">Whether the details button is available.</param>
/// <param name="ErrorMessage">Error message if loading failed (null if no error).</param>
public sealed record ProfileSelectorDialogState(
    string Title,
    IReadOnlyList<string> Profiles,
    int SelectedIndex,
    string? SelectedProfileName,
    bool IsLoading,
    bool HasCreateButton,
    bool HasDetailsButton,
    string? ErrorMessage);
