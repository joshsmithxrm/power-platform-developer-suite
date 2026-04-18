using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using PPDS.Auth.Credentials;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Provides platform-specific encryption for sensitive profile data.
/// </summary>
/// <remarks>
/// <para>
/// Windows uses DPAPI (CurrentUser scope). On non-Windows platforms there is
/// no equivalent user-scoped data protection API available in-process, so
/// this type refuses to persist values unless the caller has explicitly
/// opted in to cleartext via the <see cref="AllowCleartextEnvVar"/> environment variable
/// or by setting <see cref="AllowCleartext"/>. Callers that need secure
/// credential storage on macOS/Linux should use
/// <see cref="NativeCredentialStore"/> (Keychain/libsecret) instead.
/// </para>
/// </remarks>
public static class ProfileEncryption
{
    private const string EncryptedPrefix = "ENCRYPTED:";
    private const string CleartextPrefix = "CLEARTEXT:";

    /// <summary>
    /// Environment variable that, when set to "1" or "true", allows storing
    /// profile values as base64-encoded cleartext on platforms without
    /// in-process encryption. Intended for CI/CD only.
    /// </summary>
    public const string AllowCleartextEnvVar = "PPDS_ALLOW_CLEARTEXT";

    /// <summary>
    /// Process-wide override that, when set to true, allows cleartext
    /// persistence on non-Windows platforms. Primarily used by tests.
    /// </summary>
    public static bool AllowCleartext { get; set; }

    /// <summary>
    /// Encrypts a string value using platform-specific encryption.
    /// </summary>
    /// <param name="value">The value to encrypt.</param>
    /// <returns>
    /// The encrypted value with <c>ENCRYPTED:</c> prefix on Windows, or a
    /// <c>CLEARTEXT:</c>-prefixed base64 value on other platforms when
    /// cleartext caching has been opted in. Returns an empty string when
    /// <paramref name="value"/> is null or empty.
    /// </returns>
    /// <exception cref="AuthenticationException">
    /// Thrown on non-Windows platforms when cleartext caching has not been
    /// explicitly allowed. Error code: <c>Auth.SecureStorageUnavailable</c>.
    /// </exception>
    public static string Encrypt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use DPAPI on Windows - user-scoped, machine-bound.
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(encrypted);
        }

        // No in-process secure store available. Refuse unless explicitly
        // opted into cleartext. Callers should prefer NativeCredentialStore
        // (Keychain/libsecret) for real secrets.
        if (!IsCleartextAllowed())
        {
            throw new AuthenticationException(
                "Secure profile encryption is not available on this platform. " +
                "Use PPDS's native credential store (Keychain/libsecret) for secrets, " +
                "or set PPDS_ALLOW_CLEARTEXT=1 to opt in to cleartext storage for CI/CD.",
                "Auth.SecureStorageUnavailable");
        }

        // Cleartext fallback (opt-in only). Base64 for transport-safety,
        // not confidentiality. The CLEARTEXT: prefix makes the format
        // explicit at rest.
        return CleartextPrefix + Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Decrypts an encrypted string value.
    /// </summary>
    /// <param name="encryptedValue">The encrypted value (with or without prefix).</param>
    /// <returns>The decrypted value.</returns>
    public static string Decrypt(string? encryptedValue)
    {
        if (string.IsNullOrEmpty(encryptedValue))
        {
            return string.Empty;
        }

        // Cleartext fallback marker (non-Windows opt-in).
        if (encryptedValue.StartsWith(CleartextPrefix, StringComparison.Ordinal))
        {
            var base64 = encryptedValue[CleartextPrefix.Length..];
            if (string.IsNullOrEmpty(base64))
            {
                return string.Empty;
            }

            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            }
            catch (FormatException)
            {
                return encryptedValue;
            }
        }

        // Encrypted (DPAPI) values — with or without prefix.
        var encryptedBase64 = encryptedValue.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
            ? encryptedValue[EncryptedPrefix.Length..]
            : encryptedValue;

        if (string.IsNullOrEmpty(encryptedBase64))
        {
            return string.Empty;
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(encryptedBase64);
        }
        catch (FormatException)
        {
            // Not base64 — treat as already-plaintext legacy value.
            return encryptedValue;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                var decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch (CryptographicException)
            {
                // Ciphertext produced by another user/machine — cannot recover.
                return string.Empty;
            }
        }

        // On non-Windows we do not attempt to decrypt legacy XOR-obfuscated
        // values: the previous scheme was not a confidentiality boundary,
        // and rehydrating it here would keep pretending it was. Callers
        // that find a bare ENCRYPTED: value on a non-Windows host must
        // re-authenticate; the cleartext path uses CLEARTEXT: instead.
        return string.Empty;
    }

    /// <summary>
    /// Checks if a value is encrypted (has the ENCRYPTED: or CLEARTEXT: prefix).
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>True if the value carries one of the known prefixes.</returns>
    public static bool IsEncrypted(string? value)
    {
        if (value == null)
        {
            return false;
        }

        return value.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
            || value.StartsWith(CleartextPrefix, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true when cleartext storage has been opted in (env var or API).
    /// </summary>
    internal static bool IsCleartextAllowed()
    {
        if (AllowCleartext)
        {
            return true;
        }

        var envValue = Environment.GetEnvironmentVariable(AllowCleartextEnvVar);
        if (string.IsNullOrEmpty(envValue))
        {
            return false;
        }

        return envValue.Equals("1", StringComparison.Ordinal)
            || envValue.Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
