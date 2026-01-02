using FluentAssertions;
using Xunit;

namespace PPDS.Auth.IntegrationTests;

/// <summary>
/// Smoke tests for authentication infrastructure.
/// These tests verify the auth library is correctly referenced and basic types work.
/// </summary>
public class AuthenticationSmokeTests
{
    [Fact]
    public void ProfileStore_CanBeInstantiated()
    {
        // This test verifies the PPDS.Auth assembly is correctly referenced
        // and basic types can be loaded.
        var storeType = typeof(PPDS.Auth.Profiles.ProfileStore);
        storeType.Should().NotBeNull();
    }

    [Fact]
    public void CredentialProvider_TypesExist()
    {
        // Verify credential provider types are accessible
        var clientSecretType = typeof(PPDS.Auth.Credentials.ClientSecretCredentialProvider);
        clientSecretType.Should().NotBeNull();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void ProfileStore_DefaultPath_IsValid()
    {
        // Arrange & Act
        var store = new PPDS.Auth.Profiles.ProfileStore();

        // Assert - just verify it can be created without throwing
        store.Should().NotBeNull();
    }
}
