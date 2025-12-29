using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PPDS.Auth.Profiles;

/// <summary>
/// Provides platform-specific encryption for sensitive profile data.
/// </summary>
public static class ProfileEncryption
{
    private const string EncryptedPrefix = "ENCRYPTED:";

    /// <summary>
    /// Encrypts a string value using platform-specific encryption.
    /// </summary>
    /// <param name="value">The value to encrypt.</param>
    /// <returns>The encrypted value with ENCRYPTED: prefix.</returns>
    public static string Encrypt(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        byte[] encrypted;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use DPAPI on Windows
            encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        }
        else
        {
            // On other platforms, use a simple obfuscation
            // Note: This is NOT secure encryption, just basic obfuscation
            // For production, consider using platform-specific keychains
            encrypted = ObfuscateBytes(bytes);
        }

        return EncryptedPrefix + Convert.ToBase64String(encrypted);
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

        // Handle both prefixed and non-prefixed values
        var base64 = encryptedValue.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
            ? encryptedValue[EncryptedPrefix.Length..]
            : encryptedValue;

        if (string.IsNullOrEmpty(base64))
        {
            return string.Empty;
        }

        byte[] encrypted;
        try
        {
            encrypted = Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            // If it's not valid base64, return as-is (might be plaintext)
            return encryptedValue;
        }

        byte[] decrypted;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Use DPAPI on Windows
            try
            {
                decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                // Might be obfuscated data from another platform, try that
                decrypted = DeobfuscateBytes(encrypted);
            }
        }
        else
        {
            decrypted = DeobfuscateBytes(encrypted);
        }

        return Encoding.UTF8.GetString(decrypted);
    }

    /// <summary>
    /// Checks if a value is encrypted (has the ENCRYPTED: prefix).
    /// </summary>
    /// <param name="value">The value to check.</param>
    /// <returns>True if the value is encrypted.</returns>
    public static bool IsEncrypted(string? value)
    {
        return value?.StartsWith(EncryptedPrefix, StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// Simple XOR-based obfuscation for non-Windows platforms.
    /// This is NOT cryptographically secure, just prevents casual viewing.
    /// </summary>
    private static byte[] ObfuscateBytes(byte[] data)
    {
        // Use a machine-specific key based on username and machine name
        var key = GetMachineKey();
        var result = new byte[data.Length];

        for (int i = 0; i < data.Length; i++)
        {
            result[i] = (byte)(data[i] ^ key[i % key.Length]);
        }

        return result;
    }

    /// <summary>
    /// Reverses the XOR obfuscation.
    /// </summary>
    private static byte[] DeobfuscateBytes(byte[] data)
    {
        // XOR is its own inverse
        return ObfuscateBytes(data);
    }

    /// <summary>
    /// Gets a machine-specific key for obfuscation.
    /// </summary>
    private static byte[] GetMachineKey()
    {
        var keySource = $"{Environment.UserName}:{Environment.MachineName}:PPDS";
        return SHA256.HashData(Encoding.UTF8.GetBytes(keySource));
    }
}
