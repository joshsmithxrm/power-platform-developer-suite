using System;
using System.Threading;
using System.Threading.Tasks;

namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Resolves secrets from various sources.
    /// Priority: Key Vault URI > Direct Value
    /// </summary>
    /// <remarks>
    /// Environment variable binding is handled by the .NET configuration system.
    /// Set environment variable using the config path (e.g., Dataverse__Connections__0__ClientSecret).
    /// </remarks>
    public static class SecretResolver
    {
        /// <summary>
        /// Resolves a secret value from the configured sources.
        /// </summary>
        /// <param name="keyVaultUri">Azure Key Vault secret URI (highest priority).</param>
        /// <param name="directValue">Direct value from configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The resolved secret value, or null if not configured.</returns>
        public static async Task<string?> ResolveAsync(
            string? keyVaultUri,
            string? directValue,
            CancellationToken cancellationToken = default)
        {
            // Priority 1: Key Vault
            if (!string.IsNullOrWhiteSpace(keyVaultUri))
            {
                return await ResolveFromKeyVaultAsync(keyVaultUri, cancellationToken);
            }

            // Priority 2: Direct Value (from config, which may be bound from env var)
            return directValue;
        }

        /// <summary>
        /// Resolves a secret value synchronously.
        /// Only supports direct values (Key Vault requires async).
        /// </summary>
        public static string? ResolveSync(
            string? keyVaultUri,
            string? directValue)
        {
            // Key Vault requires async
            if (!string.IsNullOrWhiteSpace(keyVaultUri))
            {
                throw new InvalidOperationException(
                    "Key Vault secret resolution requires async. Use ResolveAsync or set ClientSecret via environment variable binding.");
            }

            // Direct Value (from config, which may be bound from env var)
            return directValue;
        }

        /// <summary>
        /// Resolves a secret from Azure Key Vault.
        /// </summary>
        private static async Task<string?> ResolveFromKeyVaultAsync(string secretUri, CancellationToken cancellationToken)
        {
            // Key Vault integration is optional - requires Azure.Identity and Azure.Security.KeyVault.Secrets
            // These are optional dependencies. If not available, throw a helpful error.

            try
            {
                // Parse the secret URI
                var uri = new Uri(secretUri);

                // Extract vault name and secret name from URI
                // Format: https://{vault-name}.vault.azure.net/secrets/{secret-name}[/{version}]
                var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2 || !string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Invalid Key Vault secret URI format. Expected: https://{{vault}}.vault.azure.net/secrets/{{name}}. Got: {secretUri}");
                }

                var vaultUri = new Uri($"https://{uri.Host}");
                var secretName = segments[1];
                var version = segments.Length > 2 ? segments[2] : null;

                // Use reflection to load Azure SDK types to avoid hard dependency
                return await ResolveFromKeyVaultCoreAsync(vaultUri, secretName, version, cancellationToken);
            }
            catch (UriFormatException ex)
            {
                throw new ArgumentException($"Invalid Key Vault secret URI: {secretUri}", ex);
            }
        }

        private static async Task<string?> ResolveFromKeyVaultCoreAsync(
            Uri vaultUri,
            string secretName,
            string? version,
            CancellationToken cancellationToken)
        {
            // Try to load Azure SDK via reflection
            var identityType = Type.GetType("Azure.Identity.DefaultAzureCredential, Azure.Identity");
            var clientType = Type.GetType("Azure.Security.KeyVault.Secrets.SecretClient, Azure.Security.KeyVault.Secrets");

            if (identityType == null || clientType == null)
            {
                throw new InvalidOperationException(
                    "Key Vault secret resolution requires Azure.Identity and Azure.Security.KeyVault.Secrets packages. " +
                    "Install these packages or use environment variables instead.");
            }

            // Create DefaultAzureCredential
            var credential = Activator.CreateInstance(identityType);

            // Create SecretClient
            var client = Activator.CreateInstance(clientType, vaultUri, credential);

            // Call GetSecretAsync
            var getSecretMethod = clientType.GetMethod("GetSecretAsync", new[] { typeof(string), typeof(string), typeof(CancellationToken) });
            if (getSecretMethod == null)
            {
                throw new InvalidOperationException("Could not find GetSecretAsync method on SecretClient.");
            }

            var task = (Task)getSecretMethod.Invoke(client, new object?[] { secretName, version, cancellationToken })!;
            await task.ConfigureAwait(false);

            // Get the result using reflection
            var resultProperty = task.GetType().GetProperty("Result");
            var response = resultProperty?.GetValue(task);

            var valueProperty = response?.GetType().GetProperty("Value");
            var secret = valueProperty?.GetValue(response);

            var secretValueProperty = secret?.GetType().GetProperty("Value");
            return secretValueProperty?.GetValue(secret) as string;
        }
    }
}
