using PPDS.Auth.Profiles;
using PPDS.Cli.Tui.Infrastructure;

namespace PPDS.Cli.Tui.Testing.States;

/// <summary>
/// Captures the state of the ProfileDetailsDialog for testing.
/// </summary>
/// <param name="Title">The dialog title.</param>
/// <param name="ProfileName">The profile name.</param>
/// <param name="AuthMethod">The authentication method.</param>
/// <param name="Identity">The identity (username or app ID).</param>
/// <param name="EnvironmentName">The environment display name (null if not set).</param>
/// <param name="EnvironmentUrl">The environment URL (null if not set).</param>
/// <param name="EnvironmentType">The environment type.</param>
/// <param name="IsActive">Whether this is the active profile.</param>
/// <param name="CreatedAt">When the profile was created (null if unknown).</param>
public sealed record ProfileDetailsDialogState(
    string Title,
    string ProfileName,
    string AuthMethod,
    string? Identity,
    string? EnvironmentName,
    string? EnvironmentUrl,
    EnvironmentType EnvironmentType,
    bool IsActive,
    DateTimeOffset? CreatedAt);
