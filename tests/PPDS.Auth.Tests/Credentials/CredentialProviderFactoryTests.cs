using FluentAssertions;
using PPDS.Auth.Cloud;
using PPDS.Auth.Credentials;
using PPDS.Auth.Profiles;
using Xunit;

namespace PPDS.Auth.Tests.Credentials;

public class CredentialProviderFactoryTests
{
    [Fact]
    public void Create_NullProfile_Throws()
    {
        var act = () => CredentialProviderFactory.Create(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Create_InteractiveBrowser_ReturnsInteractiveBrowserProvider()
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.InteractiveBrowser };

        var provider = CredentialProviderFactory.Create(profile);

        provider.Should().BeOfType<InteractiveBrowserCredentialProvider>();
        provider.Dispose();
    }

    [Fact]
    public void Create_ClientSecret_WithoutEnvVar_Throws()
    {
        // ClientSecret requires either env var or secure store (CreateAsync)
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        // Clear the env var if set
        Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);

        var act = () => CredentialProviderFactory.Create(profile);

        act.Should().Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("No client secret found"));
    }

    [Fact]
    public void Create_ClientSecret_WithEnvVar_ReturnsClientSecretProvider()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.ClientSecret,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, "test-secret");
            var provider = CredentialProviderFactory.Create(profile);

            provider.Should().BeOfType<ClientSecretCredentialProvider>();
            provider.Dispose();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
        }
    }

    [Fact]
    public void Create_ManagedIdentity_ReturnsManagedIdentityProvider()
    {
        var profile = new AuthProfile { AuthMethod = AuthMethod.ManagedIdentity };

        var provider = CredentialProviderFactory.Create(profile);

        provider.Should().BeOfType<ManagedIdentityCredentialProvider>();
        provider.Dispose();
    }

    [Fact]
    public void Create_GitHubFederated_WithValidProfile_ReturnsGitHubProvider()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.GitHubFederated,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var provider = CredentialProviderFactory.Create(profile);

        provider.Should().BeOfType<GitHubFederatedCredentialProvider>();
        provider.Dispose();
    }

    [Fact]
    public void Create_AzureDevOpsFederated_WithValidProfile_ReturnsAzureDevOpsProvider()
    {
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.AzureDevOpsFederated,
            ApplicationId = "app-id",
            TenantId = "tenant-id"
        };

        var provider = CredentialProviderFactory.Create(profile);

        provider.Should().BeOfType<AzureDevOpsFederatedCredentialProvider>();
        provider.Dispose();
    }

    [Fact]
    public void Create_UsernamePassword_WithoutStore_Throws()
    {
        // UsernamePassword requires secure store (must use CreateAsync)
        var profile = new AuthProfile
        {
            AuthMethod = AuthMethod.UsernamePassword,
            Username = "user@example.com"
        };

        var act = () => CredentialProviderFactory.Create(profile);

        act.Should().Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("CreateAsync"));
    }

    [Theory]
    [InlineData(AuthMethod.InteractiveBrowser)]
    [InlineData(AuthMethod.DeviceCode)]
    [InlineData(AuthMethod.ClientSecret)]
    [InlineData(AuthMethod.CertificateFile)]
    [InlineData(AuthMethod.CertificateStore)]
    [InlineData(AuthMethod.ManagedIdentity)]
    [InlineData(AuthMethod.GitHubFederated)]
    [InlineData(AuthMethod.AzureDevOpsFederated)]
    [InlineData(AuthMethod.UsernamePassword)]
    public void IsSupported_ValidAuthMethod_ReturnsTrue(AuthMethod authMethod)
    {
        var result = CredentialProviderFactory.IsSupported(authMethod);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsSupported_InvalidAuthMethod_ReturnsFalse()
    {
        var result = CredentialProviderFactory.IsSupported((AuthMethod)999);

        result.Should().BeFalse();
    }

    [Theory]
    [InlineData(AuthMethod.ClientSecret, true)]
    [InlineData(AuthMethod.CertificateFile, true)]
    [InlineData(AuthMethod.UsernamePassword, true)]
    [InlineData(AuthMethod.InteractiveBrowser, false)]
    [InlineData(AuthMethod.DeviceCode, false)]
    [InlineData(AuthMethod.ManagedIdentity, false)]
    [InlineData(AuthMethod.GitHubFederated, false)]
    [InlineData(AuthMethod.AzureDevOpsFederated, false)]
    public void RequiresCredentialStore_ReturnsExpectedValue(AuthMethod authMethod, bool expected)
    {
        var result = CredentialProviderFactory.RequiresCredentialStore(authMethod);

        result.Should().Be(expected);
    }

    [Fact]
    public void SpnSecretEnvVar_HasExpectedName()
    {
        CredentialProviderFactory.SpnSecretEnvVar.Should().Be("PPDS_SPN_SECRET");
    }

    [Fact]
    public void TestClientSecretEnvVar_HasExpectedName()
    {
        CredentialProviderFactory.TestClientSecretEnvVar.Should().Be("PPDS_TEST_CLIENT_SECRET");
    }

    #region GetSpnSecretFromEnvironment Tests

    [Fact]
    public void GetSpnSecretFromEnvironment_WhenSpnSecretSet_ReturnsSpnSecret()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, "spn-secret-value");
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);

            var result = CredentialProviderFactory.GetSpnSecretFromEnvironment();

            result.Should().Be("spn-secret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
        }
    }

    [Fact]
    public void GetSpnSecretFromEnvironment_WhenTestClientSecretSet_ReturnsTestClientSecret()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, "test-secret-value");

            var result = CredentialProviderFactory.GetSpnSecretFromEnvironment();

            result.Should().Be("test-secret-value");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);
        }
    }

    [Fact]
    public void GetSpnSecretFromEnvironment_WhenBothSet_PrefersSspnSecret()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, "spn-secret-value");
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, "test-secret-value");

            var result = CredentialProviderFactory.GetSpnSecretFromEnvironment();

            result.Should().Be("spn-secret-value", because: "PPDS_SPN_SECRET takes precedence over PPDS_TEST_CLIENT_SECRET");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);
        }
    }

    [Fact]
    public void GetSpnSecretFromEnvironment_WhenNeitherSet_ReturnsNull()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);

            var result = CredentialProviderFactory.GetSpnSecretFromEnvironment();

            result.Should().BeNull();
        }
        finally
        {
            // No cleanup needed
        }
    }

    #endregion

    #region ShouldBypassCredentialStore Tests

    [Fact]
    public void ShouldBypassCredentialStore_WhenSpnSecretSet_ReturnsTrue()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, "any-value");
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);

            var result = CredentialProviderFactory.ShouldBypassCredentialStore();

            result.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
        }
    }

    [Fact]
    public void ShouldBypassCredentialStore_WhenTestClientSecretSet_ReturnsTrue()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, "any-value");

            var result = CredentialProviderFactory.ShouldBypassCredentialStore();

            result.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);
        }
    }

    [Fact]
    public void ShouldBypassCredentialStore_WhenNeitherSet_ReturnsFalse()
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);

            var result = CredentialProviderFactory.ShouldBypassCredentialStore();

            result.Should().BeFalse();
        }
        finally
        {
            // No cleanup needed
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldBypassCredentialStore_WhenSetToEmptyOrWhitespace_ReturnsFalse(string value)
    {
        try
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, value);
            Environment.SetEnvironmentVariable(CredentialProviderFactory.TestClientSecretEnvVar, null);

            var result = CredentialProviderFactory.ShouldBypassCredentialStore();

            result.Should().BeFalse(because: "empty or whitespace values should not trigger bypass");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CredentialProviderFactory.SpnSecretEnvVar, null);
        }
    }

    #endregion
}
