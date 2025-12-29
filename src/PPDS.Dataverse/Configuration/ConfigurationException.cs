using System;
using System.Collections.Generic;
using System.Text;

namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Exception thrown when Dataverse configuration is invalid.
    /// Provides detailed context including property name, connection name, environment name,
    /// and resolution hints showing where the property can be configured.
    /// </summary>
    public class ConfigurationException : Exception
    {
        private readonly string? _formattedMessage;

        /// <summary>
        /// Gets the name of the connection that has invalid configuration.
        /// </summary>
        public string? ConnectionName { get; }

        /// <summary>
        /// Gets the index of the connection in the configuration array.
        /// </summary>
        public int? ConnectionIndex { get; }

        /// <summary>
        /// Gets the name of the property that is invalid.
        /// </summary>
        public string? PropertyName { get; }

        /// <summary>
        /// Gets the name of the environment where the configuration error occurred.
        /// Null for root-level (single-environment) configurations.
        /// </summary>
        public string? EnvironmentName { get; }

        /// <summary>
        /// Gets the configuration paths where the property can be set, in order of precedence.
        /// </summary>
        public IReadOnlyList<string> ResolutionHints { get; } = Array.Empty<string>();

        /// <summary>
        /// Gets the formatted message including resolution hints.
        /// </summary>
        public override string Message => _formattedMessage ?? base.Message;

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationException"/> class.
        /// </summary>
        public ConfigurationException(string message)
            : base(message)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationException"/> class.
        /// </summary>
        public ConfigurationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationException"/> class.
        /// </summary>
        public ConfigurationException(string connectionName, string propertyName, string message)
            : base($"Connection '{connectionName}': {message}")
        {
            ConnectionName = connectionName;
            PropertyName = propertyName;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="ConfigurationException"/> class
        /// with full context for user-friendly error messages.
        /// </summary>
        private ConfigurationException(
            string propertyName,
            string? connectionName,
            int? connectionIndex,
            string? environmentName,
            IReadOnlyList<string> resolutionHints,
            string baseMessage)
            : base(baseMessage)
        {
            PropertyName = propertyName;
            ConnectionName = connectionName;
            ConnectionIndex = connectionIndex;
            EnvironmentName = environmentName;
            ResolutionHints = resolutionHints;
            _formattedMessage = FormatMessage(baseMessage, propertyName, connectionName, connectionIndex, environmentName, resolutionHints);
        }

        /// <summary>
        /// Creates an exception for a missing required property.
        /// </summary>
        public static ConfigurationException MissingRequired(string connectionName, string propertyName)
        {
            return new ConfigurationException(
                connectionName,
                propertyName,
                $"'{propertyName}' is required but was not specified.");
        }

        /// <summary>
        /// Creates an exception for a missing required property with full context and resolution hints.
        /// </summary>
        /// <param name="propertyName">The name of the missing property (e.g., "Url", "ClientId").</param>
        /// <param name="connectionName">The connection name (e.g., "Primary", "Secondary").</param>
        /// <param name="connectionIndex">The zero-based index of the connection in the configuration array.</param>
        /// <param name="environmentName">The environment name, or null for root-level configurations.</param>
        /// <param name="sectionName">The configuration section name (default: "Dataverse").</param>
        /// <returns>A ConfigurationException with formatted message and resolution hints.</returns>
        public static ConfigurationException MissingRequiredWithHints(
            string propertyName,
            string connectionName,
            int connectionIndex,
            string? environmentName,
            string sectionName = "Dataverse")
        {
            var hints = GenerateResolutionHints(propertyName, connectionIndex, environmentName, sectionName);

            return new ConfigurationException(
                propertyName,
                connectionName,
                connectionIndex,
                environmentName,
                hints,
                $"Missing required property: {propertyName}");
        }

        /// <summary>
        /// Creates an exception for when no connections are configured.
        /// </summary>
        /// <param name="environmentName">The environment name, or null for root-level configurations.</param>
        /// <param name="sectionName">The configuration section name (default: "Dataverse").</param>
        public static ConfigurationException NoConnectionsConfigured(
            string? environmentName,
            string sectionName = "Dataverse")
        {
            var hints = new List<string>();

            if (!string.IsNullOrEmpty(environmentName))
            {
                hints.Add($"{sectionName}:Environments:{environmentName}:Connections");
            }

            hints.Add($"{sectionName}:Connections");

            var exception = new ConfigurationException(
                "Connections",
                connectionName: null,
                connectionIndex: null,
                environmentName,
                hints,
                "At least one connection must be configured.");

            return exception;
        }

        /// <summary>
        /// Creates an exception for an invalid property value.
        /// </summary>
        public static ConfigurationException InvalidValue(string connectionName, string propertyName, string reason)
        {
            return new ConfigurationException(
                connectionName,
                propertyName,
                $"'{propertyName}' is invalid: {reason}");
        }

        /// <summary>
        /// Creates an exception for a secret resolution failure.
        /// </summary>
        public static ConfigurationException SecretResolutionFailed(string connectionName, string propertyName, string source, Exception innerException)
        {
            return new ConfigurationException(
                $"Connection '{connectionName}': Failed to resolve secret for '{propertyName}' from {source}",
                innerException);
        }

        /// <summary>
        /// Generates resolution hints showing where a property can be configured.
        /// Lists paths in order of precedence (most specific first).
        /// </summary>
        private static List<string> GenerateResolutionHints(
            string propertyName,
            int connectionIndex,
            string? environmentName,
            string sectionName)
        {
            var hints = new List<string>();

            if (!string.IsNullOrEmpty(environmentName))
            {
                // Environment-based configuration
                // 1. Connection-level (most specific)
                hints.Add($"{sectionName}:Environments:{environmentName}:Connections:{connectionIndex}:{propertyName}");
                // 2. Environment-level
                hints.Add($"{sectionName}:Environments:{environmentName}:{propertyName}");
                // 3. Root-level (least specific)
                hints.Add($"{sectionName}:{propertyName}");
            }
            else
            {
                // Root-level configuration (no environments)
                // 1. Connection-level (most specific)
                hints.Add($"{sectionName}:Connections:{connectionIndex}:{propertyName}");
                // 2. Root-level
                hints.Add($"{sectionName}:{propertyName}");
            }

            return hints;
        }

        /// <summary>
        /// Formats the exception message with visual structure for console output.
        /// </summary>
        private static string FormatMessage(
            string errorDescription,
            string propertyName,
            string? connectionName,
            int? connectionIndex,
            string? environmentName,
            IReadOnlyList<string> resolutionHints)
        {
            var sb = new StringBuilder();

            sb.AppendLine();
            sb.AppendLine("Dataverse Configuration Error");
            sb.AppendLine();
            sb.AppendLine(errorDescription);
            sb.AppendLine();

            if (!string.IsNullOrEmpty(connectionName) || connectionIndex.HasValue)
            {
                if (!string.IsNullOrEmpty(connectionName))
                {
                    sb.AppendLine($"  Connection: {connectionName} (index: {connectionIndex})");
                }
                else
                {
                    sb.AppendLine($"  Connection index: {connectionIndex}");
                }
            }

            if (!string.IsNullOrEmpty(environmentName))
            {
                sb.AppendLine($"  Environment: {environmentName}");
            }

            if (resolutionHints.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Configure {propertyName} at any of these levels (in order of precedence):");

                for (int i = 0; i < resolutionHints.Count; i++)
                {
                    sb.AppendLine($"  {i + 1}. {resolutionHints[i]}");
                }
            }

            return sb.ToString();
        }
    }
}
