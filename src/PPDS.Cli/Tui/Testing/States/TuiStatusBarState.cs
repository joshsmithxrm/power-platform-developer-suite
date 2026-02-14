using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the TuiStatusBar for testing.
/// </summary>
/// <param name="ProfileButtonText">The text displayed on the profile button.</param>
/// <param name="EnvironmentButtonText">The text displayed on the environment button.</param>
/// <param name="EnvironmentType">The detected environment type (Production/Sandbox/Development/Unknown).</param>
/// <param name="HasProfile">Whether a profile is currently selected.</param>
/// <param name="HasEnvironment">Whether an environment is currently selected.</param>
/// <param name="HelpText">The help hint text (e.g., "F1=Help").</param>
public sealed record TuiStatusBarState(
    string ProfileButtonText,
    string EnvironmentButtonText,
    EnvironmentType EnvironmentType,
    bool HasProfile,
    bool HasEnvironment,
    string? HelpText);
