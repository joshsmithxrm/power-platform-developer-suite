namespace PPDS.Cli.Tui.Testing;

/// <summary>
/// Interface for TUI components that can capture their state for testing.
/// Enables autonomous testing by Claude without visual inspection.
/// </summary>
/// <typeparam name="TState">The state record type for this component.</typeparam>
/// <remarks>
/// This interface is part of the TUI autonomous feedback loop (ADR-0028 extension).
/// Components implementing this interface expose their internal state for assertions
/// without requiring Terminal.Gui rendering or PTY-based testing.
/// </remarks>
public interface ITuiStateCapture<out TState>
{
    /// <summary>
    /// Captures the current state of the component.
    /// </summary>
    /// <returns>A snapshot of the component's current state.</returns>
    TState CaptureState();
}
