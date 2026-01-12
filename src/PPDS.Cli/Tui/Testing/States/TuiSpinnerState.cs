namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the TuiSpinner for testing.
/// </summary>
/// <param name="IsSpinning">Whether the spinner is currently animating.</param>
/// <param name="Message">The message displayed with the spinner (null if none).</param>
/// <param name="IsVisible">Whether the spinner is visible.</param>
public sealed record TuiSpinnerState(
    bool IsSpinning,
    string? Message,
    bool IsVisible);
