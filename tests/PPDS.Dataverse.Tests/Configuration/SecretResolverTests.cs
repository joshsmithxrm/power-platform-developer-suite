using System;
using FluentAssertions;
using PPDS.Dataverse.Configuration;
using Xunit;

namespace PPDS.Dataverse.Tests.Configuration;

/// <summary>
/// Tests for SecretResolver.
/// </summary>
public class SecretResolverTests
{
    #region ResolveSync Tests

    [Fact]
    public void ResolveSync_ReturnsDirectValue_WhenProvided()
    {
        // Act
        var result = SecretResolver.ResolveSync(
            keyVaultUri: null,
            directValue: "my-direct-secret");

        // Assert
        result.Should().Be("my-direct-secret");
    }

    [Fact]
    public void ResolveSync_Throws_WhenKeyVaultUriProvided()
    {
        // Act & Assert
        var act = () => SecretResolver.ResolveSync(
            keyVaultUri: "https://myvault.vault.azure.net/secrets/mysecret",
            directValue: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*async*");
    }

    [Fact]
    public void ResolveSync_ReturnsNull_WhenAllSourcesEmpty()
    {
        // Act
        var result = SecretResolver.ResolveSync(
            keyVaultUri: null,
            directValue: null);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region ResolveAsync Tests

    [Fact]
    public async System.Threading.Tasks.Task ResolveAsync_ReturnsDirectValue_WhenProvided()
    {
        // Act
        var result = await SecretResolver.ResolveAsync(
            keyVaultUri: null,
            directValue: "my-direct-secret");

        // Assert
        result.Should().Be("my-direct-secret");
    }

    [Fact]
    public async System.Threading.Tasks.Task ResolveAsync_ReturnsNull_WhenAllSourcesEmpty()
    {
        // Act
        var result = await SecretResolver.ResolveAsync(
            keyVaultUri: null,
            directValue: null);

        // Assert
        result.Should().BeNull();
    }

    // Note: Key Vault tests would require mocking or actual Azure credentials
    // Those are best done as integration tests

    #endregion
}
