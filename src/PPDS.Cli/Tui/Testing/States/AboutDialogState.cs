namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the AboutDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="ProductName">The product name.</param>
/// <param name="Version">The version string.</param>
/// <param name="Description">The product description.</param>
/// <param name="LicenseText">The license information.</param>
/// <param name="GitHubUrl">The GitHub repository URL.</param>
public sealed record AboutDialogState(
    string Title,
    string ProductName,
    string Version,
    string? Description,
    string? LicenseText,
    string? GitHubUrl);
