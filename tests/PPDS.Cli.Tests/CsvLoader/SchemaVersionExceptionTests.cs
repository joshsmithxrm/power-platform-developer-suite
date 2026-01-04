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
    public void Message_SuggestsRegenerateMappingFile()
    {
        var exception = new SchemaVersionException("2.0", "1.0");

        Assert.Contains("regenerate", exception.Message.ToLower());
        Assert.Contains("using this cli version", exception.Message.ToLower());
    }
}
