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
    #region Environment Variable Tests

    [Fact]
    public void ResolveFromEnvironment_ReturnsValue_WhenSet()
    {
        // Arrange
        var varName = $"PPDS_TEST_{Guid.NewGuid():N}";
        var expected = "test-secret-value";
        Environment.SetEnvironmentVariable(varName, expected);

        try
        {
            // Act
            var result = SecretResolver.ResolveFromEnvironment(varName);

            // Assert
            result.Should().Be(expected);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void ResolveFromEnvironment_ReturnsNull_WhenNotSet()
    {
        // Act
        var result = SecretResolver.ResolveFromEnvironment("PPDS_NONEXISTENT_VAR_12345");

        // Assert
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ResolveFromEnvironment_ReturnsNull_WhenVarNameEmpty(string? varName)
    {
        // Act
        var result = SecretResolver.ResolveFromEnvironment(varName!);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region ResolveSync Tests

    [Fact]
    public void ResolveSync_ReturnsDirectValue_WhenNoOtherSources()
    {
        // Act
        var result = SecretResolver.ResolveSync(
            keyVaultUri: null,
            environmentVariable: null,
            directValue: "my-direct-secret");

        // Assert
        result.Should().Be("my-direct-secret");
    }

    [Fact]
    public void ResolveSync_PrefersEnvironmentVariable_OverDirectValue()
    {
        // Arrange
        var varName = $"PPDS_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, "env-secret");

        try
        {
            // Act
            var result = SecretResolver.ResolveSync(
                keyVaultUri: null,
                environmentVariable: varName,
                directValue: "direct-secret");

            // Assert
            result.Should().Be("env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public void ResolveSync_Throws_WhenKeyVaultUriProvided()
    {
        // Act & Assert
        var act = () => SecretResolver.ResolveSync(
            keyVaultUri: "https://myvault.vault.azure.net/secrets/mysecret",
            environmentVariable: null,
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
            environmentVariable: null,
            directValue: null);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region ResolveAsync Tests

    [Fact]
    public async System.Threading.Tasks.Task ResolveAsync_ReturnsDirectValue_WhenNoOtherSources()
    {
        // Act
        var result = await SecretResolver.ResolveAsync(
            keyVaultUri: null,
            environmentVariable: null,
            directValue: "my-direct-secret");

        // Assert
        result.Should().Be("my-direct-secret");
    }

    [Fact]
    public async System.Threading.Tasks.Task ResolveAsync_PrefersEnvironmentVariable_OverDirectValue()
    {
        // Arrange
        var varName = $"PPDS_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(varName, "env-secret");

        try
        {
            // Act
            var result = await SecretResolver.ResolveAsync(
                keyVaultUri: null,
                environmentVariable: varName,
                directValue: "direct-secret");

            // Assert
            result.Should().Be("env-secret");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task ResolveAsync_ReturnsNull_WhenAllSourcesEmpty()
    {
        // Act
        var result = await SecretResolver.ResolveAsync(
            keyVaultUri: null,
            environmentVariable: null,
            directValue: null);

        // Assert
        result.Should().BeNull();
    }

    // Note: Key Vault tests would require mocking or actual Azure credentials
    // Those are best done as integration tests

    #endregion
}
