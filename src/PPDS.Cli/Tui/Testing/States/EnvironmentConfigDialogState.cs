using PPDS.Auth.Profiles;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the EnvironmentConfigDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="Url">The environment URL being configured.</param>
/// <param name="Label">The user-configured display label.</param>
/// <param name="Type">The environment type string.</param>
/// <param name="SelectedColorIndex">Index of selected color in the color list.</param>
/// <param name="SelectedColor">The selected environment color (null for type default).</param>
/// <param name="ConfigChanged">Whether configuration was saved.</param>
/// <param name="IsVisible">Whether the dialog is visible.</param>
public sealed record EnvironmentConfigDialogState(
    string Title,
    string Url,
    string Label,
    string Type,
    int SelectedColorIndex,
    EnvironmentColor? SelectedColor,
    bool ConfigChanged,
    bool IsVisible);
