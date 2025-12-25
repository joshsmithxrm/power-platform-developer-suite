using System;
using System.Text;
using PPDS.Dataverse.Pooling;

namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Builds Dataverse connection strings from typed configuration.
    /// </summary>
    public static class ConnectionStringBuilder
    {
        /// <summary>
        /// Builds a connection string from a DataverseConnection configuration.
        /// </summary>
        /// <param name="connection">The connection configuration.</param>
        /// <param name="resolvedSecret">The resolved secret value (from SecretResolver).</param>
        /// <returns>A Dataverse connection string.</returns>
        public static string Build(DataverseConnection connection, string? resolvedSecret = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            return connection.AuthType switch
            {
                DataverseAuthType.ClientSecret => BuildClientSecretConnectionString(connection, resolvedSecret),
                DataverseAuthType.Certificate => BuildCertificateConnectionString(connection),
                DataverseAuthType.OAuth => BuildOAuthConnectionString(connection),
                DataverseAuthType.ManagedIdentity => BuildManagedIdentityConnectionString(connection),
                _ => throw new ConfigurationException(
                    connection.Name,
                    nameof(connection.AuthType),
                    $"Unknown authentication type: {connection.AuthType}")
            };
        }

        private static string BuildClientSecretConnectionString(DataverseConnection connection, string? resolvedSecret)
        {
            ValidateRequired(connection, nameof(connection.Url), connection.Url);
            ValidateRequired(connection, nameof(connection.ClientId), connection.ClientId);
            ValidateRequired(connection, "ClientSecret", resolvedSecret);

            var sb = new StringBuilder();
            sb.Append("AuthType=ClientSecret;");
            sb.Append($"Url={connection.Url};");
            sb.Append($"ClientId={connection.ClientId};");
            sb.Append($"ClientSecret={resolvedSecret};");

            if (!string.IsNullOrWhiteSpace(connection.TenantId))
            {
                sb.Append($"TenantId={connection.TenantId};");
            }

            return sb.ToString().TrimEnd(';');
        }

        private static string BuildCertificateConnectionString(DataverseConnection connection)
        {
            ValidateRequired(connection, nameof(connection.Url), connection.Url);
            ValidateRequired(connection, nameof(connection.ClientId), connection.ClientId);
            ValidateRequired(connection, nameof(connection.CertificateThumbprint), connection.CertificateThumbprint);

            var sb = new StringBuilder();
            sb.Append("AuthType=Certificate;");
            sb.Append($"Url={connection.Url};");
            sb.Append($"ClientId={connection.ClientId};");
            sb.Append($"Thumbprint={connection.CertificateThumbprint};");

            if (!string.IsNullOrWhiteSpace(connection.TenantId))
            {
                sb.Append($"TenantId={connection.TenantId};");
            }

            if (!string.IsNullOrWhiteSpace(connection.CertificateStoreName))
            {
                sb.Append($"StoreName={connection.CertificateStoreName};");
            }

            if (!string.IsNullOrWhiteSpace(connection.CertificateStoreLocation))
            {
                sb.Append($"StoreLocation={connection.CertificateStoreLocation};");
            }

            return sb.ToString().TrimEnd(';');
        }

        private static string BuildOAuthConnectionString(DataverseConnection connection)
        {
            ValidateRequired(connection, nameof(connection.Url), connection.Url);
            ValidateRequired(connection, nameof(connection.ClientId), connection.ClientId);
            ValidateRequired(connection, nameof(connection.RedirectUri), connection.RedirectUri);

            var sb = new StringBuilder();
            sb.Append("AuthType=OAuth;");
            sb.Append($"Url={connection.Url};");
            sb.Append($"ClientId={connection.ClientId};");
            sb.Append($"RedirectUri={connection.RedirectUri};");

            var prompt = connection.LoginPrompt switch
            {
                OAuthLoginPrompt.Always => "Always",
                OAuthLoginPrompt.Never => "Never",
                OAuthLoginPrompt.SelectAccount => "SelectAccount",
                _ => "Auto"
            };
            sb.Append($"LoginPrompt={prompt};");

            if (!string.IsNullOrWhiteSpace(connection.TenantId))
            {
                sb.Append($"TenantId={connection.TenantId};");
            }

            return sb.ToString().TrimEnd(';');
        }

        private static string BuildManagedIdentityConnectionString(DataverseConnection connection)
        {
            ValidateRequired(connection, nameof(connection.Url), connection.Url);

            var sb = new StringBuilder();
            sb.Append("AuthType=ManagedIdentity;");
            sb.Append($"Url={connection.Url};");

            // Optional: ClientId for user-assigned managed identity
            // System-assigned managed identity doesn't need ClientId
            if (!string.IsNullOrWhiteSpace(connection.ClientId))
            {
                sb.Append($"ClientId={connection.ClientId};");
            }

            return sb.ToString().TrimEnd(';');
        }

        private static void ValidateRequired(DataverseConnection connection, string propertyName, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw ConfigurationException.MissingRequired(connection.Name, propertyName);
            }
        }
    }
}
