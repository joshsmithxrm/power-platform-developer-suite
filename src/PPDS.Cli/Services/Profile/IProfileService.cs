using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Services.Profile;

/// <summary>
/// Application service for managing authentication profiles.
/// </summary>
/// <remarks>
/// This service encapsulates profile management logic shared between CLI, TUI, and RPC interfaces.
/// </remarks>
public interface IProfileService
{
    /// <summary>
    /// Gets all profiles.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of profile summaries.</returns>
    Task<IReadOnlyList<ProfileSummary>> GetProfilesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the currently active profile.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The active profile, or null if none is active.</returns>
    Task<ProfileSummary?> GetActiveProfileAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the active profile by name or index.
    /// </summary>
    /// <param name="nameOrIndex">Profile name or index (as string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The newly active profile.</returns>
    /// <exception cref="PpdsNotFoundException">If the profile is not found.</exception>
    Task<ProfileSummary> SetActiveProfileAsync(string nameOrIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a new profile with authentication.
    /// </summary>
    /// <param name="request">Profile creation parameters.</param>
    /// <param name="deviceCodeCallback">Callback for device code display (required for device code auth).</param>
    /// <param name="beforeInteractiveAuth">Callback shown before browser auth to let the user choose Browser/DeviceCode/Cancel.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created profile.</returns>
    /// <exception cref="PpdsException">If profile creation fails.</exception>
    /// <exception cref="PpdsAuthException">If authentication fails.</exception>
    /// <exception cref="PpdsValidationException">If request parameters are invalid.</exception>
    Task<ProfileSummary> CreateProfileAsync(
        ProfileCreateRequest request,
        Action<DeviceCodeInfo>? deviceCodeCallback = null,
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult>? beforeInteractiveAuth = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a profile by name or index.
    /// </summary>
    /// <param name="nameOrIndex">Profile name or index (as string).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if deleted, false if not found.</returns>
    Task<bool> DeleteProfileAsync(string nameOrIndex, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a profile's name and/or environment.
    /// </summary>
    /// <param name="nameOrIndex">Profile name or index (as string).</param>
    /// <param name="newName">New profile name (null to keep existing).</param>
    /// <param name="newEnvironment">New environment identifier (null to keep existing).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated profile.</returns>
    /// <exception cref="PpdsNotFoundException">If the profile is not found.</exception>
    /// <exception cref="PpdsValidationException">If the new name is invalid or in use.</exception>
    Task<ProfileSummary> UpdateProfileAsync(
        string nameOrIndex,
        string? newName = null,
        string? newEnvironment = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears all profiles and stored credentials.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the environment for a profile directly (without re-resolving).
    /// Use this when you already have the environment URL and display name.
    /// </summary>
    /// <param name="nameOrIndex">Profile name or index (null for active profile).</param>
    /// <param name="environmentUrl">The environment URL.</param>
    /// <param name="displayName">The display name (optional).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="PpdsNotFoundException">If the profile is not found.</exception>
    Task SetEnvironmentAsync(
        string? nameOrIndex,
        string environmentUrl,
        string? displayName = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Summary information about a profile (safe to expose to UI).
/// </summary>
public sealed record ProfileSummary
{
    /// <summary>
    /// Profile index (1-based).
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Profile name (null for unnamed profiles).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Display identifier (name or [index]).
    /// </summary>
    public required string DisplayIdentifier { get; init; }

    /// <summary>
    /// Authentication method.
    /// </summary>
    public required AuthMethod AuthMethod { get; init; }

    /// <summary>
    /// Cloud environment.
    /// </summary>
    public required string Cloud { get; init; }

    /// <summary>
    /// User identity (username or app ID).
    /// </summary>
    public required string Identity { get; init; }

    /// <summary>
    /// Whether this is the active profile.
    /// </summary>
    public bool IsActive { get; init; }

    /// <summary>
    /// Environment display name (null if no environment bound).
    /// </summary>
    public string? EnvironmentName { get; init; }

    /// <summary>
    /// Environment URL (null if no environment bound).
    /// </summary>
    public string? EnvironmentUrl { get; init; }

    /// <summary>
    /// When the profile was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// When the profile was last used.
    /// </summary>
    public DateTimeOffset? LastUsedAt { get; init; }

    /// <summary>
    /// Creates a summary from an AuthProfile.
    /// </summary>
    public static ProfileSummary FromAuthProfile(AuthProfile profile, bool isActive)
    {
        return new ProfileSummary
        {
            Index = profile.Index,
            Name = profile.Name,
            DisplayIdentifier = profile.DisplayIdentifier,
            AuthMethod = profile.AuthMethod,
            Cloud = profile.Cloud.ToString(),
            Identity = profile.IdentityDisplay,
            IsActive = isActive,
            EnvironmentName = profile.Environment?.DisplayName,
            EnvironmentUrl = profile.Environment?.Url,
            CreatedAt = profile.CreatedAt,
            LastUsedAt = profile.LastUsedAt
        };
    }
}

/// <summary>
/// Request parameters for creating a profile.
/// </summary>
public sealed record ProfileCreateRequest
{
    /// <summary>
    /// Profile name (optional, max 30 characters).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Environment identifier (URL, ID, or name).
    /// </summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Cloud environment (default: Public).
    /// </summary>
    public string Cloud { get; init; } = "Public";

    /// <summary>
    /// Tenant ID (required for SPN auth).
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Use device code flow instead of interactive browser.
    /// </summary>
    public bool UseDeviceCode { get; init; }

    /// <summary>
    /// Explicit auth method selection. When set, takes priority over UseDeviceCode and field-based inference.
    /// Used by TUI to pass the user's radio button selection directly.
    /// </summary>
    public AuthMethod? AuthMethod { get; init; }

    /// <summary>
    /// Application ID for SPN auth.
    /// </summary>
    public string? ApplicationId { get; init; }

    /// <summary>
    /// Client secret for SPN auth.
    /// </summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Certificate file path.
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Certificate password.
    /// </summary>
    public string? CertificatePassword { get; init; }

    /// <summary>
    /// Certificate thumbprint (Windows store).
    /// </summary>
    public string? CertificateThumbprint { get; init; }

    /// <summary>
    /// Username for ROPC authentication.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Password for ROPC authentication.
    /// </summary>
    public string? Password { get; init; }

    /// <summary>
    /// Use managed identity.
    /// </summary>
    public bool UseManagedIdentity { get; init; }

    /// <summary>
    /// Use GitHub federated credentials.
    /// </summary>
    public bool UseGitHubFederated { get; init; }

    /// <summary>
    /// Use Azure DevOps federated credentials.
    /// </summary>
    public bool UseAzureDevOpsFederated { get; init; }

    /// <summary>
    /// Accept cleartext credential storage on Linux.
    /// </summary>
    public bool AcceptCleartextCaching { get; init; }
}

