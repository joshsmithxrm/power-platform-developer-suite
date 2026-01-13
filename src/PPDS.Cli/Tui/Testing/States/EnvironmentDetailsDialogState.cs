using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the EnvironmentDetailsDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="DisplayName">The environment display name.</param>
/// <param name="Url">The environment URL.</param>
/// <param name="EnvironmentType">The environment type (shown in header badge).</param>
/// <param name="OrganizationId">The organization ID (null if unknown).</param>
/// <param name="Version">The Dataverse version (null if unknown).</param>
public sealed record EnvironmentDetailsDialogState(
    string Title,
    string DisplayName,
    string Url,
    EnvironmentType EnvironmentType,
    string? OrganizationId,
    string? Version);
