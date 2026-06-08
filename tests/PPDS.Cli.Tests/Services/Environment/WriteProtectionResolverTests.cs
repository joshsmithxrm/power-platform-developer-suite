using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Moq;
using PPDS.Auth.Profiles;
using PPDS.Cli.Services.Environment;
using Xunit;

namespace PPDS.Cli.Tests.Services.Environment;

/// <summary>
/// Unit tests for <see cref="WriteProtectionResolver"/> — the shared protection-level resolution used by
/// <c>api request</c> and the model-driven-app write commands (issue #1195).
/// </summary>
public class WriteProtectionResolverTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(EnvironmentType.Production, ProtectionLevel.Production)]
    [InlineData(EnvironmentType.Unknown, ProtectionLevel.Production)]   // fail-safe
    [InlineData(EnvironmentType.Sandbox, ProtectionLevel.Development)]
    [InlineData(EnvironmentType.Development, ProtectionLevel.Development)]
    [InlineData(EnvironmentType.Trial, ProtectionLevel.Development)]
    public void Resolve_MapsEnvironmentType_WhenNoOverride(EnvironmentType type, ProtectionLevel expected)
    {
        WriteProtectionResolver.Resolve(type, configuredProtection: null).Should().Be(expected);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Resolve_ExplicitOverride_AlwaysWins()
    {
        // A configured override beats the type-derived level (even Production → Development).
        WriteProtectionResolver.Resolve(EnvironmentType.Production, ProtectionLevel.Development)
            .Should().Be(ProtectionLevel.Development);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NullEnvConfigService_Throws()
    {
        var act = async () => await WriteProtectionResolver.ResolveAsync(
            null!, "https://example.crm.dynamics.com/", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName("envConfigService");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_UsesConfiguredType()
    {
        var env = new Mock<IEnvironmentConfigService>();
        env.Setup(e => e.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EnvironmentConfig { Url = "u", Type = EnvironmentType.Production });

        var level = await WriteProtectionResolver.ResolveAsync(env.Object, "u", CancellationToken.None);

        level.Should().Be(ProtectionLevel.Production);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ResolveAsync_NullConfig_FailsSafeToProduction()
    {
        var env = new Mock<IEnvironmentConfigService>();
        env.Setup(e => e.GetConfigAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((EnvironmentConfig?)null);

        var level = await WriteProtectionResolver.ResolveAsync(env.Object, "u", CancellationToken.None);

        level.Should().Be(ProtectionLevel.Production);
    }
}
