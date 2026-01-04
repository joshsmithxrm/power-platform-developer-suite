using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class SchemaVersionExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var exception = new SchemaVersionException("2.0", "1.0");

        Assert.Equal("2.0", exception.FileVersion);
        Assert.Equal("1.0", exception.CliVersion);
    }

    [Fact]
    public void Message_ContainsVersionInfo()
    {
        var exception = new SchemaVersionException("2.0", "1.0");

        Assert.Contains("2.0", exception.Message);
        Assert.Contains("1.0", exception.Message);
    }

    [Fact]
    public void Message_SuggestsUpgrade()
    {
        var exception = new SchemaVersionException("2.5", "1.0");

        Assert.Contains("v2.x", exception.Message);
        Assert.Contains("upgrade", exception.Message.ToLower());
    }

    [Fact]
    public void Message_SuggestsRegenerateMappingFile()
    {
        var exception = new SchemaVersionException("2.0", "1.0");

        Assert.Contains("regenerate", exception.Message.ToLower());
    }

    [Fact]
    public void GetMajorVersion_ExtractsMajorFromSemVer()
    {
        // Testing via the message which uses GetMajorVersion internally
        var exception = new SchemaVersionException("3.1.5", "1.0");

        Assert.Contains("v3.x", exception.Message);
    }

    [Fact]
    public void GetMajorVersion_HandlesVersionWithoutMinor()
    {
        // Edge case: version is just "2" without dot
        var exception = new SchemaVersionException("2", "1.0");

        Assert.Contains("v2.x", exception.Message);
    }
}
