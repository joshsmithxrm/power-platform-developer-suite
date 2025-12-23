using System;

namespace PPDS.Dataverse.Configuration
{
    /// <summary>
    /// Exception thrown when Dataverse configuration is invalid.
    /// </summary>
    public class ConfigurationException : Exception
    {
        /// <summary>
        /// Gets the name of the connection that has invalid configuration.
        /// </summary>
        public string? ConnectionName { get; }

        /// <summary>
        /// Gets the name of the property that is invalid.
        /// </summary>
        public string? PropertyName { get; }

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
    }
}
