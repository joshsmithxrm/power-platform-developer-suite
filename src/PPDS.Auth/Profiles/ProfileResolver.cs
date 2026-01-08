using System;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Resolves which profile to use based on the priority order:
/// 1. Explicit profile name (CLI flag or API parameter)
/// 2. PPDS_PROFILE environment variable
/// 3. Global active profile from profiles.json
/// </summary>
/// <remarks>
/// See ADR-0018 for the profile session isolation design.
/// </remarks>
public static class ProfileResolver
{
    /// <summary>
    /// Environment variable name for profile override.
    /// </summary>
    public const string ProfileEnvironmentVariable = "PPDS_PROFILE";

    /// <summary>
    /// Gets the effective profile name to use, following the priority order:
    /// 1. Explicit profile (from CLI flag or API parameter)
    /// 2. PPDS_PROFILE environment variable
    /// 3. Returns null (caller should use global active profile)
    /// </summary>
    /// <param name="explicitProfile">Profile explicitly specified by user (e.g., --profile flag).</param>
    /// <returns>
    /// The profile name to use, or null if the global active profile should be used.
    /// </returns>
    public static string? GetEffectiveProfileName(string? explicitProfile = null)
    {
        // 1. Explicit profile takes highest priority (CLI flag, API parameter)
        if (!string.IsNullOrWhiteSpace(explicitProfile))
        {
            return explicitProfile;
        }

        // 2. Environment variable
        var envProfile = Environment.GetEnvironmentVariable(ProfileEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envProfile))
        {
            return envProfile;
        }

        // 3. Return null - caller should use ActiveProfile from ProfileCollection
        return null;
    }

    /// <summary>
    /// Resolves the effective profile from a collection, following the priority order.
    /// </summary>
    /// <param name="collection">The profile collection to search.</param>
    /// <param name="explicitProfile">Profile explicitly specified by user (e.g., --profile flag).</param>
    /// <returns>The resolved profile, or null if no matching profile found.</returns>
    public static AuthProfile? ResolveProfile(ProfileCollection collection, string? explicitProfile = null)
    {
        var effectiveName = GetEffectiveProfileName(explicitProfile);

        if (effectiveName != null)
        {
            // Look up by name or index
            return collection.GetByNameOrIndex(effectiveName);
        }

        // Fall back to global active profile
        return collection.ActiveProfile;
    }
}
