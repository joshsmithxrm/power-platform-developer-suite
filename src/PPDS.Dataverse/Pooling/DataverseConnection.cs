using System;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Security;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Configuration for a Dataverse connection (Application User / Service Principal).
    /// Multiple connections can be configured to distribute load across Application Users.
    /// </summary>
    public class DataverseConnection
    {
        /// <summary>
        /// Gets or sets the unique name for this connection.
        /// Used for logging, metrics, and identifying which Application User is handling requests.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the authentication type.
        /// Default: ClientSecret
        /// </summary>
        public DataverseAuthType AuthType { get; set; } = DataverseAuthType.ClientSecret;

        /// <summary>
        /// Gets or sets the Dataverse environment URL.
        /// Example: https://contoso.crm.dynamics.com
        /// </summary>
        public string? Url { get; set; }

        /// <summary>
        /// Gets or sets the Azure AD tenant ID.
        /// Optional - defaults to common tenant.
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Gets or sets the Azure AD application (client) ID.
        /// Required for all auth types.
        /// </summary>
        public string? ClientId { get; set; }

        #region ClientSecret Authentication

        /// <summary>
        /// Gets or sets the Azure Key Vault secret URI for the client secret.
        /// Highest priority for secret resolution.
        /// Example: https://myvault.vault.azure.net/secrets/dataverse-secret
        /// </summary>
        public string? ClientSecretKeyVaultUri { get; set; }

        /// <summary>
        /// Gets or sets the client secret.
        /// Can be set directly, via environment variable binding, or use ClientSecretKeyVaultUri for Key Vault.
        /// </summary>
        [SensitiveData(Reason = "Contains client secret", DataType = "Secret")]
        public string? ClientSecret { get; set; }

        #endregion

        #region Certificate Authentication

        /// <summary>
        /// Gets or sets the certificate thumbprint for certificate auth.
        /// </summary>
        public string? CertificateThumbprint { get; set; }

        /// <summary>
        /// Gets or sets the certificate store name.
        /// Default: My
        /// </summary>
        public string? CertificateStoreName { get; set; }

        /// <summary>
        /// Gets or sets the certificate store location.
        /// Default: CurrentUser
        /// </summary>
        public string? CertificateStoreLocation { get; set; }

        #endregion

        #region OAuth Authentication

        /// <summary>
        /// Gets or sets the OAuth redirect URI.
        /// Required for OAuth authentication.
        /// </summary>
        public string? RedirectUri { get; set; }

        /// <summary>
        /// Gets or sets the OAuth login prompt behavior.
        /// Default: Auto
        /// </summary>
        public OAuthLoginPrompt LoginPrompt { get; set; } = OAuthLoginPrompt.Auto;

        #endregion

        /// <summary>
        /// Gets or sets the maximum connections to create for this configuration.
        /// Default: 10
        /// </summary>
        public int MaxPoolSize { get; set; } = 10;

        #region Source Tracking (Internal)

        /// <summary>
        /// Gets or sets the environment name this connection was resolved from.
        /// Null for root-level connections (no environment).
        /// Used for error messages and diagnostics.
        /// </summary>
        internal string? SourceEnvironment { get; set; }

        /// <summary>
        /// Gets or sets the index of this connection in the source configuration array.
        /// Used for error messages and diagnostics.
        /// </summary>
        internal int SourceIndex { get; set; }

        #endregion

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseConnection"/> class.
        /// </summary>
        public DataverseConnection()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseConnection"/> class.
        /// </summary>
        /// <param name="name">The unique name for this connection.</param>
        public DataverseConnection(string name)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
        }

        /// <summary>
        /// Returns a string representation of the connection configuration.
        /// Credentials are intentionally excluded to prevent leakage.
        /// </summary>
        public override string ToString()
        {
            return $"DataverseConnection {{ Name = {Name}, Url = {Url}, AuthType = {AuthType} }}";
        }
    }
}
