using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Information about an environment in the selector list.
/// </summary>
/// <param name="DisplayName">The environment display name.</param>
/// <param name="Url">The environment URL.</param>
/// <param name="EnvironmentType">The environment type (Production/Sandbox/Development).</param>
public sealed record EnvironmentListItem(
    string DisplayName,
    string Url,
    EnvironmentType EnvironmentType);

/// <summary>
/// Captures the state of the EnvironmentSelectorDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="Environments">List of available environments.</param>
/// <param name="SelectedIndex">Currently selected index (-1 if none).</param>
/// <param name="SelectedEnvironmentUrl">URL of the selected environment (null if none).</param>
/// <param name="IsLoading">Whether the dialog is discovering environments.</param>
/// <param name="HasDetailsButton">Whether the details button is available.</param>
/// <param name="ErrorMessage">Error message if discovery failed (null if no error).</param>
public sealed record EnvironmentSelectorDialogState(
    string Title,
    IReadOnlyList<EnvironmentListItem> Environments,
    int SelectedIndex,
    string? SelectedEnvironmentUrl,
    bool IsLoading,
    bool HasDetailsButton,
    string? ErrorMessage);
