using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands;

/// <summary>
/// Utility for writing consistent header messages to the console.
/// </summary>
public static class ConsoleHeader
{
    /// <summary>
    /// Writes the "Connected as" header for API commands.
    /// </summary>
    /// <param name="profile">The active auth profile.</param>
    /// <param name="environmentName">Optional environment name to show on a separate "Connected to..." line.</param>
    public static void WriteConnectedAs(AuthProfile profile, string? environmentName = null)
    {
        var identity = GetIdentityDisplay(profile);
        Console.WriteLine($"Connected as {identity}");

        if (!string.IsNullOrEmpty(environmentName))
        {
            Console.WriteLine($"Connected to... {environmentName}");
        }
    }

    /// <summary>
    /// Writes the "Connected as" header using resolved connection info.
    /// Falls back to URL if display name is not available.
    /// </summary>
    /// <param name="connectionInfo">The resolved connection information.</param>
    public static void WriteConnectedAs(ResolvedConnectionInfo connectionInfo)
    {
        var identity = GetIdentityDisplay(connectionInfo.Profile);
        Console.WriteLine($"Connected as {identity}");

        var envDisplay = connectionInfo.EnvironmentDisplayName ?? connectionInfo.EnvironmentUrl;
        if (!string.IsNullOrEmpty(envDisplay))
        {
            Console.WriteLine($"Connected to... {envDisplay}");
        }
    }

    /// <summary>
    /// Writes the "Connected as" header with a label prefix (e.g., "Source:" or "Target:").
    /// </summary>
    /// <param name="label">The label to prefix (e.g., "Source" or "Target").</param>
    /// <param name="connectionInfo">The resolved connection information.</param>
    public static void WriteConnectedAsLabeled(string label, ResolvedConnectionInfo connectionInfo)
    {
        var identity = GetIdentityDisplay(connectionInfo.Profile);
        Console.WriteLine($"{label}: Connected as {identity}");

        var envDisplay = connectionInfo.EnvironmentDisplayName ?? connectionInfo.EnvironmentUrl;
        if (!string.IsNullOrEmpty(envDisplay))
        {
            Console.WriteLine($"{new string(' ', label.Length + 2)}Connected to... {envDisplay}");
        }
    }

    /// <summary>
    /// Gets the display identity for a profile.
    /// </summary>
    /// <param name="profile">The profile.</param>
    /// <returns>The identity display string.</returns>
    public static string GetIdentityDisplay(AuthProfile profile)
    {
        if (!string.IsNullOrEmpty(profile.Username))
            return profile.Username;

        if (!string.IsNullOrEmpty(profile.ApplicationId))
        {
            if (!string.IsNullOrEmpty(profile.Name))
                return $"{profile.Name} ({profile.ApplicationId})";

            return $"app:{profile.ApplicationId[..Math.Min(8, profile.ApplicationId.Length)]}...";
        }

        return "(unknown)";
    }
}
