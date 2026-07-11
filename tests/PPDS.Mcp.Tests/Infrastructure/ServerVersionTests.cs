using FluentAssertions;
using PPDS.Mcp.Infrastructure;
using Xunit;

namespace PPDS.Mcp.Tests.Infrastructure;

/// <summary>
/// Unit tests for <see cref="ServerVersion"/>.
/// Verifies fix for #1273: MCP <c>serverInfo.version</c> must reflect MinVer's stamped
/// package version, not the SDK's default AssemblyVersion fallback (always "1.0.0.0").
/// </summary>
[Trait("Category", "Unit")]
public sealed class ServerVersionTests
{
    #region Resolve(string?, string?) — informational version present

    [Fact]
    public void Resolve_InformationalVersionWithBuildMetadata_StripsMetadataSuffix()
    {
        // Arrange — MinVer's non-tag-build format: "{version}-{prerelease}+{sha}"
        var informational = "1.0.2-alpha.0.5+abc1234def5678";

        // Act
        var result = ServerVersion.Resolve(informational, fileVersion: "1.0.2.0");

        // Assert
        result.Should().Be("1.0.2-alpha.0.5");
    }

    [Fact]
    public void Resolve_InformationalVersionWithoutBuildMetadata_ReturnsUnchanged()
    {
        // Arrange — MinVer's tag-build format has no '+' suffix.
        var informational = "1.0.1";

        // Act
        var result = ServerVersion.Resolve(informational, fileVersion: "1.0.1.0");

        // Assert
        result.Should().Be("1.0.1");
    }

    [Fact]
    public void Resolve_InformationalVersionWithMultiplePlusSigns_StripsFromFirstPlus()
    {
        // Arrange — defensive: only the first '+' should demarcate build metadata,
        // matching semver (everything after the first '+' is metadata).
        var informational = "1.0.2-alpha.0.5+abc+def";

        // Act
        var result = ServerVersion.Resolve(informational, fileVersion: null);

        // Assert
        result.Should().Be("1.0.2-alpha.0.5");
    }

    #endregion

    #region Resolve(string?, string?) — informational version missing or blank

    [Fact]
    public void Resolve_MissingInformationalVersion_FallsBackToFileVersion()
    {
        // Act
        var result = ServerVersion.Resolve(informationalVersion: null, fileVersion: "1.0.2.0");

        // Assert
        result.Should().Be("1.0.2.0");
    }

    [Fact]
    public void Resolve_BlankInformationalVersion_FallsBackToFileVersion()
    {
        // Act
        var result = ServerVersion.Resolve(informationalVersion: "   ", fileVersion: "1.0.2.0");

        // Assert
        result.Should().Be("1.0.2.0");
    }

    [Fact]
    public void Resolve_MissingBothInformationalAndFileVersion_FallsBackToUnknown()
    {
        // Act
        var result = ServerVersion.Resolve(informationalVersion: null, fileVersion: null);

        // Assert
        result.Should().Be(ServerVersion.Unknown);
    }

    [Fact]
    public void Resolve_BlankInformationalAndBlankFileVersion_FallsBackToUnknown()
    {
        // Act
        var result = ServerVersion.Resolve(informationalVersion: "", fileVersion: "  ");

        // Assert
        result.Should().Be(ServerVersion.Unknown);
    }

    #endregion

    #region Resolve(Assembly)

    [Fact]
    public void Resolve_NullAssembly_ThrowsArgumentNullException()
    {
        // Act
        var act = () => ServerVersion.Resolve(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Resolve_RealPpdsMcpAssembly_ReturnsMinVerVersionNotFixedAssemblyVersion()
    {
        // Arrange — ServerVersion itself lives in the MinVer-versioned ppds-mcp-server
        // assembly, so this exercises the real attributes MinVer stamps at build time
        // without needing a live handshake.
        var assembly = typeof(ServerVersion).Assembly;

        // Act
        var result = ServerVersion.Resolve(assembly);

        // Assert — must never be blank, never carry build metadata, and must never
        // collapse to the fixed "{major}.0.0.0" AssemblyVersion the bug reported.
        result.Should().NotBeNullOrWhiteSpace();
        result.Should().NotContain("+");
        result.Should().NotBe("1.0.0.0");
    }

    #endregion
}
