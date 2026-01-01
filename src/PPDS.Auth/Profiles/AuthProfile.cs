using System;
using System.Text.Json.Serialization;
using PPDS.Auth.Cloud;

namespace PPDS.Auth.Profiles;

/// <summary>
/// An authentication profile containing credentials and environment binding.
/// </summary>
public sealed class AuthProfile
{
    /// <summary>
    /// Gets or sets the profile index (1-based, assigned on creation).
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Gets or sets the profile name (optional, max 30 characters).
    /// Null for unnamed profiles (reference by index only).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the authentication method.
    /// </summary>
    [JsonPropertyName("authMethod")]
    public AuthMethod AuthMethod { get; set; }

    /// <summary>
    /// Gets or sets the cloud environment.
    /// </summary>
    [JsonPropertyName("cloud")]
    public CloudEnvironment Cloud { get; set; } = CloudEnvironment.Public;

    /// <summary>
    /// Gets or sets the tenant ID.
    /// Required for app-based authentication.
    /// </summary>
    [JsonPropertyName("tenantId")]
    public string? TenantId { get; set; }

    #region User Authentication

    /// <summary>
    /// Gets or sets the username for device code or password auth.
    /// Populated after successful authentication.
    /// </summary>
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    /// <summary>
    /// Gets or sets the Entra ID Object ID (user or service principal).
    /// Populated after successful authentication.
    /// </summary>
    [JsonPropertyName("objectId")]
    public string? ObjectId { get; set; }

    /// <summary>
    /// Gets or sets the password (encrypted).
    /// For UsernamePassword auth.
    /// </summary>
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    #endregion

    #region Application Authentication

    /// <summary>
    /// Gets or sets the application (client) ID.
    /// Required for service principal authentication.
    /// </summary>
    [JsonPropertyName("applicationId")]
    public string? ApplicationId { get; set; }

    /// <summary>
    /// Gets or sets the client secret (encrypted).
    /// For ClientSecret authentication.
    /// </summary>
    [JsonPropertyName("clientSecret")]
    public string? ClientSecret { get; set; }

    #endregion

    #region Certificate Authentication

    /// <summary>
    /// Gets or sets the certificate file path.
    /// For CertificateFile authentication.
    /// </summary>
    [JsonPropertyName("certificatePath")]
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Gets or sets the certificate password (encrypted).
    /// For CertificateFile authentication.
    /// </summary>
    [JsonPropertyName("certificatePassword")]
    public string? CertificatePassword { get; set; }

    /// <summary>
    /// Gets or sets the certificate thumbprint.
    /// For CertificateStore authentication.
    /// </summary>
    [JsonPropertyName("certificateThumbprint")]
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Gets or sets the certificate store name.
    /// For CertificateStore authentication. Default: My
    /// </summary>
    [JsonPropertyName("certificateStoreName")]
    public string? CertificateStoreName { get; set; }

    /// <summary>
    /// Gets or sets the certificate store location.
    /// For CertificateStore authentication. Default: CurrentUser
    /// </summary>
    [JsonPropertyName("certificateStoreLocation")]
    public string? CertificateStoreLocation { get; set; }

    #endregion

    #region Environment

    /// <summary>
    /// Gets or sets the bound environment.
    /// Null for universal profiles (no environment selected).
    /// </summary>
    [JsonPropertyName("environment")]
    public EnvironmentInfo? Environment { get; set; }

    #endregion

    #region Metadata

    /// <summary>
    /// Gets or sets when the profile was created.
    /// </summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets when the profile was last used.
    /// </summary>
    [JsonPropertyName("lastUsedAt")]
    public DateTimeOffset? LastUsedAt { get; set; }

    #endregion

    #region Token Claims

    /// <summary>
    /// Gets or sets when the access token expires.
    /// Populated after successful authentication.
    /// </summary>
    [JsonPropertyName("tokenExpiresOn")]
    public DateTimeOffset? TokenExpiresOn { get; set; }

    /// <summary>
    /// Gets or sets the user's PUID from the JWT 'puid' claim.
    /// </summary>
    [JsonPropertyName("puid")]
    public string? Puid { get; set; }

    /// <summary>
    /// Gets or sets the MSAL home account identifier.
    /// Format: {objectId}.{tenantId} - uniquely identifies the account+tenant for token cache lookup.
    /// </summary>
    [JsonPropertyName("homeAccountId")]
    public string? HomeAccountId { get; set; }

    #endregion

    /// <summary>
    /// Gets whether this profile has an environment bound.
    /// </summary>
    [JsonIgnore]
    public bool HasEnvironment => Environment != null;

    /// <summary>
    /// Gets whether this profile has a name.
    /// </summary>
    [JsonIgnore]
    public bool HasName => !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// Gets the display identifier (name if available, otherwise index).
    /// </summary>
    [JsonIgnore]
    public string DisplayIdentifier => HasName ? Name! : $"[{Index}]";

    /// <summary>
    /// Gets the identity string for display (username or application ID).
    /// </summary>
    [JsonIgnore]
    public string IdentityDisplay
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Username))
                return Username;
            if (!string.IsNullOrWhiteSpace(ApplicationId))
                return $"app:{ApplicationId[..Math.Min(8, ApplicationId.Length)]}...";
            return "(unknown)";
        }
    }

    /// <summary>
    /// Returns a string representation of the profile.
    /// </summary>
    public override string ToString()
    {
        var envPart = HasEnvironment ? $", Env: {Environment!.DisplayName}" : "";
        return $"Profile {DisplayIdentifier} ({AuthMethod}, {Cloud}{envPart})";
    }

    /// <summary>
    /// Validates that the profile has required fields for its auth method.
    /// </summary>
    /// <exception cref="InvalidOperationException">If required fields are missing.</exception>
    public void Validate()
    {
        switch (AuthMethod)
        {
            case AuthMethod.InteractiveBrowser:
            case AuthMethod.DeviceCode:
                // No required fields - will authenticate interactively
                break;

            case AuthMethod.ClientSecret:
                RequireField(ApplicationId, nameof(ApplicationId));
                RequireField(ClientSecret, nameof(ClientSecret));
                RequireField(TenantId, nameof(TenantId));
                break;

            case AuthMethod.CertificateFile:
                RequireField(ApplicationId, nameof(ApplicationId));
                RequireField(CertificatePath, nameof(CertificatePath));
                RequireField(TenantId, nameof(TenantId));
                break;

            case AuthMethod.CertificateStore:
                RequireField(ApplicationId, nameof(ApplicationId));
                RequireField(CertificateThumbprint, nameof(CertificateThumbprint));
                RequireField(TenantId, nameof(TenantId));
                break;

            case AuthMethod.ManagedIdentity:
                // ApplicationId is optional (for user-assigned identity)
                break;

            case AuthMethod.GitHubFederated:
            case AuthMethod.AzureDevOpsFederated:
                RequireField(ApplicationId, nameof(ApplicationId));
                RequireField(TenantId, nameof(TenantId));
                break;

            case AuthMethod.UsernamePassword:
                RequireField(Username, nameof(Username));
                RequireField(Password, nameof(Password));
                break;

            default:
                throw new InvalidOperationException($"Unknown auth method: {AuthMethod}");
        }
    }

    private static void RequireField(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Required field '{fieldName}' is missing or empty.");
        }
    }
}
