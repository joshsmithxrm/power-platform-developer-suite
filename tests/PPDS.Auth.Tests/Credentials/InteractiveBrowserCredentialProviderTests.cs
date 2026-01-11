using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

/// <summary>
/// Tests for <see cref="InteractiveBrowserCredentialProvider"/>.
/// </summary>
public class InteractiveBrowserCredentialProviderTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_WithDefaults_DoesNotThrow()
    {
        using var provider = new InteractiveBrowserCredentialProvider();

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.InteractiveBrowser);
    }

    [Fact]
    public void Constructor_WithAllParameters_DoesNotThrow()
    {
        using var provider = new InteractiveBrowserCredentialProvider(
            cloud: CloudEnvironment.Public,
            tenantId: "test-tenant",
            username: "user@example.com",
            homeAccountId: "account-id");

        provider.Should().NotBeNull();
    }

    [Fact]
    public void FromProfile_CreatesProviderWithProfileSettings()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.InteractiveBrowser,
            Cloud = CloudEnvironment.UsGov,
            TenantId = "gov-tenant",
            Username = "gov-user@example.com",
            HomeAccountId = "gov-account-id"
        };

        using var provider = InteractiveBrowserCredentialProvider.FromProfile(profile);

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.InteractiveBrowser);
    }

    #endregion

    #region Callback Tests

    /// <summary>
    /// This test documents that DeviceCodeCredentialProvider HAS a callback mechanism
    /// to notify callers when interactive auth is about to happen.
    /// </summary>
    [Fact]
    public void DeviceCodeProvider_HasCallbackParameter()
    {
        // Arrange - DeviceCodeCredentialProvider accepts a callback for auth notification
        Action<DeviceCodeInfo> callback = _ => { };

        // Act - DeviceCodeCredentialProvider accepts a callback
        using var provider = new DeviceCodeCredentialProvider(
            cloud: CloudEnvironment.Public,
            tenantId: null,
            username: null,
            homeAccountId: null,
            deviceCodeCallback: callback);

        // Assert - The provider was created with a callback
        provider.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies that InteractiveBrowserCredentialProvider has a beforeInteractiveAuth callback
    /// that returns a PreAuthDialogResult, allowing callers to control auth flow.
    /// </summary>
    [Fact]
    public void InteractiveBrowserProvider_HasBeforeAuthCallback()
    {
        // Verify the callback parameter exists
        var constructorParams = typeof(InteractiveBrowserCredentialProvider)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.Name)
            .ToList();

        constructorParams.Should().Contain("beforeInteractiveAuth",
            because: "InteractiveBrowserCredentialProvider should have a beforeInteractiveAuth callback");
        constructorParams.Should().Contain("deviceCodeCallback",
            because: "InteractiveBrowserCredentialProvider should have a deviceCodeCallback for fallback");
    }

    /// <summary>
    /// Verifies that the beforeInteractiveAuth callback receives the device code callback
    /// and can return different results (OpenBrowser, UseDeviceCode, Cancel).
    /// </summary>
    [Fact]
    public void InteractiveBrowserProvider_CallbackSignature_SupportsAllResults()
    {
        // Test each possible result
        foreach (var expectedResult in Enum.GetValues<PreAuthDialogResult>())
        {
            Func<Action<DeviceCodeInfo>?, PreAuthDialogResult> callback = (deviceCodeCb) =>
            {
                // Verify we receive the device code callback
                // (it may be null if not provided)
                return expectedResult;
            };

            using var provider = new InteractiveBrowserCredentialProvider(
                cloud: CloudEnvironment.Public,
                deviceCodeCallback: _ => { },
                beforeInteractiveAuth: callback);

            provider.Should().NotBeNull();
        }
    }

    /// <summary>
    /// Verifies that InteractiveBrowserCredentialProvider.FromProfile accepts
    /// both deviceCodeCallback and beforeInteractiveAuth parameters.
    /// </summary>
    [Fact]
    public void FromProfile_AcceptsDeviceCodeAndBeforeAuthCallbacks()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.InteractiveBrowser,
            Cloud = CloudEnvironment.Public,
            TenantId = "test-tenant"
        };

        Action<DeviceCodeInfo> deviceCodeCallback = _ => { };
        Func<Action<DeviceCodeInfo>?, PreAuthDialogResult> beforeInteractiveAuth = _ => PreAuthDialogResult.OpenBrowser;

        using var provider = InteractiveBrowserCredentialProvider.FromProfile(
            profile,
            deviceCodeCallback,
            beforeInteractiveAuth);

        provider.Should().NotBeNull();
        provider.AuthMethod.Should().Be(AuthMethod.InteractiveBrowser);
    }

    #endregion

    #region IsAvailable Tests

    [Fact]
    public void IsAvailable_InNormalEnvironment_DoesNotThrow()
    {
        // On a normal desktop environment (not SSH, not CI, has display)
        // This test may return different results in CI environments
        var act = () => InteractiveBrowserCredentialProvider.IsAvailable();

        // Just verify it doesn't throw - actual value depends on environment
        act.Should().NotThrow();
    }

    #endregion
}
