using System;
using System.Collections.Generic;
using PPDS.Dataverse.DependencyInjection;

namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Resolves named environments from DataverseOptions.
    /// </summary>
    public static class EnvironmentResolver
    {
        /// <summary>
        /// Gets a named environment from the configuration.
        /// Environment name matching is case-insensitive.
        /// </summary>
        /// <param name="options">The Dataverse options.</param>
        /// <param name="environmentName">The environment name to retrieve.</param>
        /// <returns>The environment configuration.</returns>
        /// <exception cref="KeyNotFoundException">The environment was not found.</exception>
        public static DataverseEnvironmentOptions GetEnvironment(
            DataverseOptions options,
            string environmentName)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (string.IsNullOrWhiteSpace(environmentName))
            {
                throw new ArgumentException("Environment name cannot be empty.", nameof(environmentName));
            }

            // Try exact match first
            if (options.Environments.TryGetValue(environmentName, out var environment))
            {
                return environment;
            }

            // Fall back to case-insensitive match
            foreach (var kvp in options.Environments)
            {
                if (string.Equals(kvp.Key, environmentName, StringComparison.OrdinalIgnoreCase))
                {
                    return kvp.Value;
                }
            }

            throw new KeyNotFoundException(
                $"Environment '{environmentName}' not found. Available environments: {string.Join(", ", options.Environments.Keys)}");
        }

        /// <summary>
        /// Gets the default environment from the configuration.
        /// Falls back to creating an environment from root-level Connections if no environments are defined.
        /// </summary>
        /// <param name="options">The Dataverse options.</param>
        /// <returns>The default environment configuration.</returns>
        public static DataverseEnvironmentOptions GetDefaultEnvironment(DataverseOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            // If a default environment is specified, use it
            if (!string.IsNullOrWhiteSpace(options.DefaultEnvironment))
            {
                return GetEnvironment(options, options.DefaultEnvironment);
            }

            // If environments are defined, use the first one
            if (options.Environments.Count > 0)
            {
                using var enumerator = options.Environments.Values.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    return enumerator.Current;
                }
            }

            // Fallback: create a virtual environment from root-level connections
            return new DataverseEnvironmentOptions
            {
                Name = "default",
                Url = options.Url,
                TenantId = options.TenantId,
                Connections = options.Connections
            };
        }

        /// <summary>
        /// Checks if a named environment exists in the configuration.
        /// Environment name matching is case-insensitive.
        /// </summary>
        public static bool HasEnvironment(DataverseOptions options, string environmentName)
        {
            if (options?.Environments == null || string.IsNullOrWhiteSpace(environmentName))
            {
                return false;
            }

            // Try exact match first
            if (options.Environments.ContainsKey(environmentName))
            {
                return true;
            }

            // Fall back to case-insensitive match
            foreach (var key in options.Environments.Keys)
            {
                if (string.Equals(key, environmentName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets all environment names.
        /// </summary>
        public static IEnumerable<string> GetEnvironmentNames(DataverseOptions options)
        {
            if (options == null)
            {
                return Array.Empty<string>();
            }

            return options.Environments.Keys;
        }
    }
}
