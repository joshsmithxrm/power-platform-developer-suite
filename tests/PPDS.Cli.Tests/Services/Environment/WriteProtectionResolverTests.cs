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
    [Theory]
    [Trait("Category", "Unit")]
    // hasEntry, type, protection, confirmation, sqlDml, crossEnvironment
    [InlineData(false, null, null, ProtectionLevel.Production, ProtectionLevel.Production, ProtectionLevel.Production)]
    [InlineData(true, null, null, ProtectionLevel.Production, ProtectionLevel.Production, ProtectionLevel.Production)]   // was Development for SQL DML before #1829
    [InlineData(true, EnvironmentType.Unknown, null, ProtectionLevel.Production, ProtectionLevel.Production, ProtectionLevel.Production)] // was Development for both SQL rules
    [InlineData(true, EnvironmentType.Production, null, ProtectionLevel.Production, ProtectionLevel.Production, ProtectionLevel.Production)]
    [InlineData(true, EnvironmentType.Development, null, ProtectionLevel.Development, ProtectionLevel.Development, ProtectionLevel.Development)]
    [InlineData(true, EnvironmentType.Test, null, ProtectionLevel.Development, ProtectionLevel.Development, ProtectionLevel.Development)]
    [InlineData(true, EnvironmentType.Development, ProtectionLevel.Test, ProtectionLevel.Test, ProtectionLevel.Test, ProtectionLevel.Test)]
    public void PerWriterRules_AllUseOneRule(
        bool hasEntry, EnvironmentType? type, ProtectionLevel? protection,
        ProtectionLevel confirmation, ProtectionLevel sqlDml, ProtectionLevel crossEnvironment)
    {
        // The SQL sites (SqlCommand, the daemon's query handler, the TUI session, cross-environment DML)
        // call these helpers. Since #1829 every writer uses one rule: no saved type is Production.
        var config = hasEntry ? new EnvironmentConfig { Url = "https://org.crm.dynamics.com/", Type = type, Protection = protection } : null;

        WriteProtectionResolver.ResolveForConfirmation(config).Should().Be(confirmation);
        WriteProtectionResolver.ResolveForSqlDml(config).Should().Be(sqlDml);
        WriteProtectionResolver.ResolveForCrossEnvironmentTarget(config).Should().Be(crossEnvironment);
    }
}
