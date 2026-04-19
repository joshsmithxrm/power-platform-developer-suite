using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Profiles;

/// <summary>
/// Tests for <see cref="ProfileEncryption"/>.
/// </summary>
/// <remarks>
/// On Windows these exercise the real DPAPI path. On non-Windows platforms
/// the tests flip <see cref="ProfileEncryption.AllowCleartext"/> to exercise
/// the CLEARTEXT opt-in path, and additionally verify that without the
/// opt-in the code refuses to encrypt (A2).
/// </remarks>
public class ProfileEncryptionTests : IDisposable
{
    private readonly bool _priorAllow;
    private static readonly bool IsWindows =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    public ProfileEncryptionTests()
    {
        _priorAllow = ProfileEncryption.AllowCleartext;
        // Unit tests need a deterministic encryption path on every OS.
        if (!IsWindows)
        {
            ProfileEncryption.AllowCleartext = true;
        }
    }

    public void Dispose()
    {
        ProfileEncryption.AllowCleartext = _priorAllow;
    }

    [Fact]
    public void Encrypt_ValidValue_ReturnsValueWithPrefix()
    {
        var value = "my-secret-value";

        var encrypted = ProfileEncryption.Encrypt(value);

        (encrypted.StartsWith("ENCRYPTED:") || encrypted.StartsWith("CLEARTEXT:")).Should().BeTrue();
        encrypted.Should().NotContain(value);
    }

    [Fact]
    public void Encrypt_Decrypt_RoundTrip()
    {
        var original = "my-secret-value";

        var encrypted = ProfileEncryption.Encrypt(original);
        var decrypted = ProfileEncryption.Decrypt(encrypted);

        decrypted.Should().Be(original);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Encrypt_NullOrEmpty_ReturnsEmpty(string? value)
    {
        var encrypted = ProfileEncryption.Encrypt(value);

        encrypted.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Decrypt_NullOrEmpty_ReturnsEmpty(string? value)
    {
        var decrypted = ProfileEncryption.Decrypt(value);

        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_InvalidBase64_ReturnsAsIs()
    {
        var invalid = "not-valid-base64!@#$%";

        var result = ProfileEncryption.Decrypt(invalid);

        result.Should().Be(invalid);
    }

    [Fact]
    public void IsEncrypted_WithPrefix_ReturnsTrue()
    {
        var encrypted = ProfileEncryption.Encrypt("test");

        var result = ProfileEncryption.IsEncrypted(encrypted);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsEncrypted_WithoutPrefix_ReturnsFalse()
    {
        var plaintext = "plain-value";

        var result = ProfileEncryption.IsEncrypted(plaintext);

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void IsEncrypted_NullOrEmpty_ReturnsFalse(string? value)
    {
        var result = ProfileEncryption.IsEncrypted(value);

        result.Should().BeFalse();
    }

    [Fact]
    public void Encrypt_DifferentValues_ProduceDifferentCiphertext()
    {
        var value1 = "first-secret";
        var value2 = "second-secret";

        var encrypted1 = ProfileEncryption.Encrypt(value1);
        var encrypted2 = ProfileEncryption.Encrypt(value2);

        encrypted1.Should().NotBe(encrypted2);
    }

    [Fact]
    public void Encrypt_SameValue_BothDecryptCorrectly()
    {
        var value = "test-secret";

        var encrypted1 = ProfileEncryption.Encrypt(value);
        var encrypted2 = ProfileEncryption.Encrypt(value);

        ProfileEncryption.Decrypt(encrypted1).Should().Be(value);
        ProfileEncryption.Decrypt(encrypted2).Should().Be(value);
    }

    [Fact]
    public void Encrypt_SpecialCharacters_RoundTrips()
    {
        var value = "secret!@#$%^&*(){}[]|\\:;\"'<>,.?/~`";

        var encrypted = ProfileEncryption.Encrypt(value);
        var decrypted = ProfileEncryption.Decrypt(encrypted);

        decrypted.Should().Be(value);
    }

    [Fact]
    public void Encrypt_Unicode_RoundTrips()
    {
        var value = "秘密🔒パスワード";

        var encrypted = ProfileEncryption.Encrypt(value);
        var decrypted = ProfileEncryption.Decrypt(encrypted);

        decrypted.Should().Be(value);
    }

    [Fact]
    public void Encrypt_LongValue_RoundTrips()
    {
        var value = new string('x', 10000);

        var encrypted = ProfileEncryption.Encrypt(value);
        var decrypted = ProfileEncryption.Decrypt(encrypted);

        decrypted.Should().Be(value);
    }

    [Fact]
    public void Encrypt_OnNonWindows_WithoutCleartextOptIn_Throws()
    {
        if (IsWindows)
        {
            // On Windows DPAPI is always available; this guard is a no-op there.
            return;
        }

        // Flip the opt-in off just for this test.
        ProfileEncryption.AllowCleartext = false;
        Environment.SetEnvironmentVariable(ProfileEncryption.AllowCleartextEnvVar, null);

        try
        {
            var act = () => ProfileEncryption.Encrypt("supersecret");
            act.Should().Throw<AuthenticationException>()
                .Where(e => e.ErrorCode == "Auth.SecureStorageUnavailable");
        }
        finally
        {
            ProfileEncryption.AllowCleartext = true;
        }
    }

    [Fact]
    public void Encrypt_OnNonWindows_WithEnvVarOptIn_Allows()
    {
        if (IsWindows)
        {
            return;
        }

        ProfileEncryption.AllowCleartext = false;
        Environment.SetEnvironmentVariable(ProfileEncryption.AllowCleartextEnvVar, "1");

        try
        {
            var encrypted = ProfileEncryption.Encrypt("s3cret");
            encrypted.Should().StartWith("CLEARTEXT:");
            ProfileEncryption.Decrypt(encrypted).Should().Be("s3cret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ProfileEncryption.AllowCleartextEnvVar, null);
            ProfileEncryption.AllowCleartext = true;
        }
    }

    [Fact]
    public void Decrypt_OnNonWindows_BareEncryptedPrefix_Throws()
    {
        if (IsWindows)
        {
            // On Windows the DPAPI branch handles ENCRYPTED: values; this guard fires
            // only on macOS/Linux where there is no in-process secure store.
            return;
        }

        // Construct a bare ENCRYPTED:<valid_base64> payload — the legacy format produced
        // by older PPDS Windows installs whose profile.json gets copied to a non-Windows
        // host. We must not silently treat this as an empty credential, because that
        // cascades into a "wrong credentials" UX downstream when the user re-runs auth.
        var legacyPayload = "ENCRYPTED:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("payload"));

        var act = () => ProfileEncryption.Decrypt(legacyPayload);

        act.Should().Throw<AuthenticationException>()
            .Where(e => e.ErrorCode == "Auth.LegacyEncryptedProfileUnsupported");
    }

    [Fact]
    public void ProfileEncryption_DoesNotExposeXorHelpers()
    {
        // Regression guard for A2: ObfuscateBytes/DeobfuscateBytes must not exist.
        var type = typeof(ProfileEncryption);
        var members = type.GetMembers(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.Instance);

        members.Should().NotContain(m => m.Name == "ObfuscateBytes");
        members.Should().NotContain(m => m.Name == "DeobfuscateBytes");
    }
}
