using System;
using System.Text.RegularExpressions;

namespace PPDS.Dataverse.Security
{
    /// <summary>
    /// Provides utilities for redacting sensitive information from connection strings
    /// before logging or displaying to users.
    /// </summary>
    public static class ConnectionStringRedactor
    {
        /// <summary>
        /// The placeholder text used to replace sensitive values.
        /// </summary>
        public const string RedactedPlaceholder = "***REDACTED***";

        /// <summary>
        /// Keys in connection strings that contain sensitive data and should be redacted.
        /// </summary>
        private static readonly string[] SensitiveKeys =
        [
            "ClientSecret",
            "Password",
            "Secret",
            "Key",
            "Pwd",
            "Token",
            "ApiKey",
            "AccessToken",
            "RefreshToken",
            "SharedAccessKey",
            "AccountKey",
            "Credential"
        ];

        /// <summary>
        /// Pattern to match sensitive key-value pairs in connection strings.
        /// Matches: Key=Value; or Key=Value (at end) or Key="Value with spaces"
        /// </summary>
        private static readonly Regex SensitivePattern = BuildSensitivePattern();

        /// <summary>
        /// Redacts sensitive values from a connection string.
        /// </summary>
        /// <param name="connectionString">The connection string to redact.</param>
        /// <returns>The connection string with sensitive values replaced by <see cref="RedactedPlaceholder"/>.</returns>
        /// <example>
        /// <code>
        /// var redacted = ConnectionStringRedactor.Redact(
        ///     "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=xxx;ClientSecret=supersecret");
        /// // Returns: "AuthType=ClientSecret;Url=https://org.crm.dynamics.com;ClientId=xxx;ClientSecret=***REDACTED***"
        /// </code>
        /// </example>
        public static string Redact(string? connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                return connectionString ?? string.Empty;
            }

            return SensitivePattern.Replace(connectionString, match =>
            {
                var key = match.Groups["key"].Value;
                var separator = match.Groups["separator"].Value;
                return $"{key}{separator}{RedactedPlaceholder}";
            });
        }

        /// <summary>
        /// Redacts sensitive values from an exception message that may contain connection string data.
        /// </summary>
        /// <param name="message">The exception message to redact.</param>
        /// <returns>The message with sensitive values redacted.</returns>
        public static string RedactExceptionMessage(string? message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return message ?? string.Empty;
            }

            // Apply the same redaction pattern
            var result = SensitivePattern.Replace(message, match =>
            {
                var key = match.Groups["key"].Value;
                var separator = match.Groups["separator"].Value;
                return $"{key}{separator}{RedactedPlaceholder}";
            });

            return result;
        }

        /// <summary>
        /// Checks if a string appears to contain a connection string with sensitive data.
        /// </summary>
        /// <param name="value">The string to check.</param>
        /// <returns>True if the string appears to contain sensitive connection string data.</returns>
        public static bool ContainsSensitiveData(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            return SensitivePattern.IsMatch(value);
        }

        private static Regex BuildSensitivePattern()
        {
            // Build pattern: (ClientSecret|Password|Secret|...)=([^;]*|"[^"]*")
            var keyPattern = string.Join("|", SensitiveKeys);

            // Match: Key=Value or Key="Quoted Value"
            // Captures: key (the sensitive key name), separator (=), and value
            var pattern = $@"(?<key>{keyPattern})(?<separator>\s*=\s*)(?:""[^""]*""|[^;]*)";

            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
        }
    }
}
