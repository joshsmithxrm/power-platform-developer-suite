using FluentAssertions;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class ManagedIdentityCredentialProviderTests
{
    [Fact]
    public void Constructor_WithNoClientId_UsesSystemAssigned()
    {
        using var provider = new ManagedIdentityCredentialProvider();

        provider.AuthMethod.Should().Be(AuthMethod.ManagedIdentity);
        provider.Identity.Should().Be("(system-assigned)");
        provider.TenantId.Should().BeNull();
    }

    [Fact]
    public void Constructor_WithClientId_UsesUserAssigned()
    {
        using var provider = new ManagedIdentityCredentialProvider("my-client-id");

        provider.AuthMethod.Should().Be(AuthMethod.ManagedIdentity);
        provider.Identity.Should().Be("my-client-id");
    }

    [Fact]
    public void AuthMethod_ReturnsManagedIdentity()
    {
        using var provider = new ManagedIdentityCredentialProvider();
        provider.AuthMethod.Should().Be(AuthMethod.ManagedIdentity);
    }
}
