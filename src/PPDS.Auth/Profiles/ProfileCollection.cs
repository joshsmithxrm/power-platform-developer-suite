using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Collection of authentication profiles with active profile tracking.
/// </summary>
public sealed class ProfileCollection
{
    /// <summary>
    /// Storage format version for migration support.
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets the index of the active profile.
    /// Null if no profile is active (collection empty).
    /// </summary>
    [JsonPropertyName("activeIndex")]
    public int? ActiveIndex { get; set; }

    /// <summary>
    /// Gets or sets the profiles dictionary (keyed by index).
    /// </summary>
    [JsonPropertyName("profiles")]
    public Dictionary<int, AuthProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Gets the active profile, or null if none is active.
    /// </summary>
    [JsonIgnore]
    public AuthProfile? ActiveProfile =>
        ActiveIndex.HasValue && Profiles.TryGetValue(ActiveIndex.Value, out var profile)
            ? profile
            : null;

    /// <summary>
    /// Gets all profiles in index order.
    /// </summary>
    [JsonIgnore]
    public IEnumerable<AuthProfile> All => Profiles.Values.OrderBy(p => p.Index);

    /// <summary>
    /// Gets the count of profiles.
    /// </summary>
    [JsonIgnore]
    public int Count => Profiles.Count;

    /// <summary>
    /// Gets the next available index.
    /// </summary>
    [JsonIgnore]
    public int NextIndex => Profiles.Count == 0 ? 1 : Profiles.Keys.Max() + 1;

    /// <summary>
    /// Adds a profile to the collection.
    /// </summary>
    /// <param name="profile">The profile to add.</param>
    /// <param name="setAsActive">Whether to set this as the active profile.</param>
    public void Add(AuthProfile profile, bool setAsActive = false)
    {
        if (profile.Index <= 0)
        {
            profile.Index = NextIndex;
        }

        if (Profiles.ContainsKey(profile.Index))
        {
            throw new InvalidOperationException($"Profile with index {profile.Index} already exists.");
        }

        Profiles[profile.Index] = profile;

        // Auto-select first profile as active
        if (setAsActive || Profiles.Count == 1)
        {
            ActiveIndex = profile.Index;
        }
    }

    /// <summary>
    /// Gets a profile by index.
    /// </summary>
    /// <param name="index">The profile index.</param>
    /// <returns>The profile, or null if not found.</returns>
    public AuthProfile? GetByIndex(int index)
    {
        return Profiles.TryGetValue(index, out var profile) ? profile : null;
    }

    /// <summary>
    /// Gets a profile by name (case-insensitive).
    /// </summary>
    /// <param name="name">The profile name.</param>
    /// <returns>The profile, or null if not found.</returns>
    public AuthProfile? GetByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return Profiles.Values.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets a profile by name or index string.
    /// </summary>
    /// <param name="nameOrIndex">The profile name or index (as string).</param>
    /// <returns>The profile, or null if not found.</returns>
    public AuthProfile? GetByNameOrIndex(string nameOrIndex)
    {
        if (string.IsNullOrWhiteSpace(nameOrIndex))
            return null;

        // Try as index first
        if (int.TryParse(nameOrIndex, out var index))
        {
            return GetByIndex(index);
        }

        // Try as name
        return GetByName(nameOrIndex);
    }

    /// <summary>
    /// Removes a profile by index.
    /// </summary>
    /// <param name="index">The profile index.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool RemoveByIndex(int index)
    {
        if (!Profiles.Remove(index))
        {
            return false;
        }

        // If we removed the active profile, clear active or select another
        if (ActiveIndex == index)
        {
            ActiveIndex = Profiles.Count > 0 ? Profiles.Keys.Min() : null;
        }

        return true;
    }

    /// <summary>
    /// Removes a profile by name.
    /// </summary>
    /// <param name="name">The profile name.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool RemoveByName(string name)
    {
        var profile = GetByName(name);
        return profile != null && RemoveByIndex(profile.Index);
    }

    /// <summary>
    /// Sets the active profile by index.
    /// </summary>
    /// <param name="index">The profile index.</param>
    /// <exception cref="InvalidOperationException">If profile not found.</exception>
    public void SetActiveByIndex(int index)
    {
        if (!Profiles.ContainsKey(index))
        {
            throw new InvalidOperationException($"Profile with index {index} not found.");
        }

        ActiveIndex = index;
    }

    /// <summary>
    /// Sets the active profile by name.
    /// </summary>
    /// <param name="name">The profile name.</param>
    /// <exception cref="InvalidOperationException">If profile not found.</exception>
    public void SetActiveByName(string name)
    {
        var profile = GetByName(name);
        if (profile == null)
        {
            throw new InvalidOperationException($"Profile with name '{name}' not found.");
        }

        ActiveIndex = profile.Index;
    }

    /// <summary>
    /// Clears all profiles.
    /// </summary>
    public void Clear()
    {
        Profiles.Clear();
        ActiveIndex = null;
    }

    /// <summary>
    /// Checks if a profile name is already in use (case-insensitive).
    /// </summary>
    /// <param name="name">The name to check.</param>
    /// <param name="excludeIndex">Optional index to exclude from check (for rename).</param>
    /// <returns>True if the name is in use.</returns>
    public bool IsNameInUse(string name, int? excludeIndex = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        return Profiles.Values.Any(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
            p.Index != excludeIndex);
    }
}
