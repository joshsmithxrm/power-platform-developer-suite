namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ReAuthenticationDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="ErrorMessage">The error message displayed to the user.</param>
/// <param name="ShouldReauthenticate">Whether the user chose to re-authenticate.</param>
public sealed record ReAuthenticationDialogState(
    string Title,
    string ErrorMessage,
    bool ShouldReauthenticate);
