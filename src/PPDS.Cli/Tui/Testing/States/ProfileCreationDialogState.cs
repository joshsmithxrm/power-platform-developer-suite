namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ProfileCreationDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="ProfileName">The entered profile name.</param>
/// <param name="SelectedAuthMethod">The selected authentication method.</param>
/// <param name="AvailableAuthMethods">List of available auth methods.</param>
/// <param name="IsCreating">Whether profile creation is in progress.</param>
/// <param name="ValidationError">Validation error message (null if valid).</param>
/// <param name="CanCreate">Whether the create button is enabled.</param>
public sealed record ProfileCreationDialogState(
    string Title,
    string ProfileName,
    string? SelectedAuthMethod,
    IReadOnlyList<string> AvailableAuthMethods,
    bool IsCreating,
    string? ValidationError,
    bool CanCreate);
