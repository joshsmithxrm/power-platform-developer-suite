namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captured state of the DeviceCodeDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="UserCode">The device code displayed to the user.</param>
/// <param name="VerificationUrl">The URL where the user enters the code.</param>
/// <param name="ClipboardCopied">Whether the code was auto-copied to clipboard.</param>
/// <param name="IsVisible">Whether the dialog is currently visible.</param>
public sealed record DeviceCodeDialogState(
    string Title,
    string UserCode,
    string VerificationUrl,
    bool ClipboardCopied,
    bool IsVisible);
