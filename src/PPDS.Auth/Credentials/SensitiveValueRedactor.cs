using System.Text.RegularExpressions;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Redacts sensitive values (client secrets, passwords, tokens, etc.) from
/// strings before they are surfaced in exception messages or logs.
/// </summary>
/// <remarks>
/// <para>
/// This mirrors the behaviour of
/// <c>PPDS.Dataverse.Security.ConnectionStringRedactor</c> but is duplicated
/// inside <c>PPDS.Auth</c> so this project can remain free of a dependency
/// on <c>PPDS.Dataverse</c>. Keep the two implementations in sync; the
/// authoritative copy is the one in <c>PPDS.Dataverse</c>.
/// </para>
/// </remarks>
internal static class SensitiveValueRedactor
{
    /// <summary>
    /// Placeholder used to replace sensitive values.
    /// </summary>
    public const string RedactedPlaceholder = "***REDACTED***";

    // Keep in sync with ConnectionStringRedactor.SensitiveKeys.
    private static readonly Regex Pattern = new(
        @"(?<key>ClientSecret|Password|Secret|Key|Pwd|Token|ApiKey|AccessToken|RefreshToken|SharedAccessKey|AccountKey|Credential)(?<separator>\s*=\s*)(?:""[^""]*""|[^;]*)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Redacts sensitive key=value pairs from a string.
    /// </summary>
    /// <param name="message">The string to redact (may be null).</param>
    /// <returns>A redacted copy, or empty string when <paramref name="message"/> is null/empty.</returns>
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message ?? string.Empty;
        }

        return Pattern.Replace(message, match =>
        {
            var key = match.Groups["key"].Value;
            var separator = match.Groups["separator"].Value;
            return $"{key}{separator}{RedactedPlaceholder}";
        });
    }
}
