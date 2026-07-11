using System.Reflection;
using PPDS.Cli.Infrastructure;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="AssemblyVersionInfo"/> — the shared raw read of an assembly's
/// <see cref="AssemblyInformationalVersionAttribute"/> used by both the CLI diagnostics
/// header and the MCP serverInfo.version resolver.
/// </summary>
[Trait("Category", "Unit")]
public class AssemblyVersionInfoTests
{
    [Fact]
    public void GetInformationalVersion_NullAssembly_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => AssemblyVersionInfo.GetInformationalVersion(null!));
    }

    [Fact]
    public void GetInformationalVersion_ReturnsRawAttributeValueVerbatim()
    {
        // The test assembly is stamped with an AssemblyInformationalVersionAttribute, so the
        // helper must return exactly that value — no trimming, stripping, or normalization.
        var assembly = typeof(AssemblyVersionInfoTests).Assembly;
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var actual = AssemblyVersionInfo.GetInformationalVersion(assembly);

        Assert.Equal(expected, actual);
    }
}
