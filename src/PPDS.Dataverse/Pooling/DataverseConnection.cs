using System;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Security;

namespace PPDS.Dataverse.Pooling
{
    /// <summary>
    /// Configuration for a Dataverse connection source.
    /// Multiple connections can be configured to distribute load across Application Users.
    /// Supports both raw connection strings and typed configuration with secret resolution.
    /// </summary>
    public class DataverseConnection
    {
        /// <summary>
        /// Gets or sets the unique name for this connection.
        /// Used for logging, metrics, and identifying which Application User is handling requests.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        #region Raw Connection String (Legacy)

        /// <summary>
        /// Gets or sets the Dataverse connection string.
        /// If set, this takes precedence over typed configuration properties.
        /// </summary>
        /// <remarks>
        /// This property contains sensitive credentials and should never be logged directly.
        /// Use <see cref="ConnectionStringRedactor.Redact"/> if you need to include
        /// connection string information in logs or error messages.
        /// Prefer using typed configuration (Url, ClientId, etc.) with secret resolution.
        /// </remarks>
        /// <example>
        /// AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=xxx;ClientSecret=xxx
        /// </example>
        [SensitiveData(Reason = "Contains authentication credentials", DataType = "ConnectionString")]
        public string ConnectionString { get; set; } = string.Empty;

        #endregion

        #region Typed Configuration

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
        /// Gets or sets the environment variable name containing the client secret.
        /// Second priority for secret resolution.
        /// </summary>
        public string? ClientSecretVariable { get; set; }

        /// <summary>
        /// Gets or sets the client secret directly.
        /// Lowest priority - not recommended for production.
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

        #endregion

        /// <summary>
        /// Gets or sets the maximum connections to create for this configuration.
        /// Default: 10
        /// </summary>
        public int MaxPoolSize { get; set; } = 10;

        /// <summary>
        /// Gets whether this connection uses typed configuration (vs raw connection string).
        /// </summary>
        public bool UsesTypedConfiguration => string.IsNullOrWhiteSpace(ConnectionString);

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
        /// <param name="connectionString">The Dataverse connection string.</param>
        public DataverseConnection(string name, string connectionString)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DataverseConnection"/> class.
        /// </summary>
        /// <param name="name">The unique name for this connection.</param>
        /// <param name="connectionString">The Dataverse connection string.</param>
        /// <param name="maxPoolSize">The maximum connections for this configuration.</param>
        public DataverseConnection(string name, string connectionString, int maxPoolSize)
            : this(name, connectionString)
        {
            MaxPoolSize = maxPoolSize;
        }

        /// <summary>
        /// Returns a string representation of the connection configuration.
        /// The connection string is intentionally excluded to prevent credential leakage.
        /// </summary>
        /// <returns>A string containing the connection name and pool size.</returns>
        public override string ToString()
        {
            return $"DataverseConnection {{ Name = {Name}, MaxPoolSize = {MaxPoolSize} }}";
        }

        /// <summary>
        /// Gets a redacted version of the connection string safe for logging.
        /// </summary>
        /// <returns>The connection string with sensitive values replaced.</returns>
        public string GetRedactedConnectionString()
        {
            return ConnectionStringRedactor.Redact(ConnectionString);
        }
    }
}
