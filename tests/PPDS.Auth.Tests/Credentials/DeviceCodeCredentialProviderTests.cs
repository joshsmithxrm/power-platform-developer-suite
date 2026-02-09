using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests for <see cref="DeviceCodeCredentialProvider"/>.
/// </summary>
public class DeviceCodeCredentialProviderTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithDefaults_DoesNotThrow()
    {
        using var provider = new DeviceCodeCredentialProvider();

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.DeviceCode);
    }

    [Fact]
    public void Constructor_WithAllParameters_DoesNotThrow()
    {
        using var provider = new DeviceCodeCredentialProvider(
            cloud: CloudEnvironment.Public,
            tenantId: "test-tenant",
            username: "user@example.com",
            homeAccountId: "account-id",
            deviceCodeCallback: _ => { });

        provider.Should().NotBeNull();
    }

    [Fact]
    public void FromProfile_CreatesProviderWithProfileSettings()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.DeviceCode,
            Cloud = CloudEnvironment.UsGov,
            TenantId = "gov-tenant",
            Username = "gov-user@example.com",
            HomeAccountId = "gov-account-id"
        };

        using var provider = DeviceCodeCredentialProvider.FromProfile(profile);

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.DeviceCode);
    }

    #endregion

    #region Token Cache URL Tracking

    /// <summary>
    /// Verifies that DeviceCodeCredentialProvider tracks the URL associated with cached tokens,
    /// matching the pattern in InteractiveBrowserCredentialProvider.
    /// Without URL tracking, a token obtained for globaldisco could be incorrectly reused
    /// for a different environment URL.
    /// </summary>
    [Fact]
    public void HasCachedResultUrlField_ForTokenScopeMismatchPrevention()
    {
        // The provider must have a _cachedResultUrl field to track which URL
        // the cached token was obtained for, preventing scope mismatch.
        // InteractiveBrowserCredentialProvider has this (fixed in PR #515),
        // DeviceCodeCredentialProvider must also have it.
        var field = typeof(DeviceCodeCredentialProvider)
            .GetField("_cachedResultUrl",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        field.Should().NotBeNull(
            because: "DeviceCodeCredentialProvider must track the URL associated with cached tokens " +
                     "to prevent token scope mismatch (same fix as InteractiveBrowserCredentialProvider PR #515)");
    }

    #endregion
}
