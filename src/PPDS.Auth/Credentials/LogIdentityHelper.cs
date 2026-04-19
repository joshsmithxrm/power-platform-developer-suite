using System;
using System.Security.Cryptography;
using System.Text;

namespace PPDS.Auth.Credentials;

/// <summary>
/// Helpers for redacting user identifiers before writing them to logs.
/// </summary>
/// <remarks>
/// Raw UPNs (e.g. <c>alice@contoso.com</c>) are user-identifying and leaking
/// them into log streams that may be shipped to CI, crash reports, or bug
/// reports expands the PII blast radius unnecessarily. Debug logs use the
/// hashed form from <see cref="HashIdentifier"/> so that two records for
/// the same account remain correlatable without exposing the UPN.
/// </remarks>
internal static class LogIdentityHelper
{
    /// <summary>
    /// Returns a stable, non-reversible 8-character hex tag derived from
    /// the input identifier. Safe to include in debug logs.
    /// </summary>
    /// <param name="identifier">
    /// The UPN, username, or other identifier to hash. May be null or empty.
    /// </param>
    /// <returns>
    /// An 8-character lowercase hex hash of <paramref name="identifier"/>,
    /// or <c>(none)</c> when the input is null/empty.
    /// </returns>
    public static string HashIdentifier(string? identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return "(none)";
        }

        var bytes = Encoding.UTF8.GetBytes(identifier.ToLowerInvariant());
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(8);
        for (var i = 0; i < 4; i++)
        {
            sb.Append(hash[i].ToString("x2"));
        }
        return sb.ToString();
    }
}
