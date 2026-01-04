using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class CsvMappingSchemaTests
{
    [Fact]
    public void CurrentVersion_IsNotEmpty()
    {
        Assert.False(string.IsNullOrEmpty(CsvMappingSchema.CurrentVersion));
    }

    [Fact]
    public void CurrentVersion_IsValidSemVer()
    {
        var parts = CsvMappingSchema.CurrentVersion.Split('.');

        Assert.True(parts.Length >= 1);
        Assert.True(int.TryParse(parts[0], out _), "Major version should be numeric");
    }

    [Fact]
    public void SchemaUrl_IsValidUrl()
    {
        Assert.True(Uri.IsWellFormedUriString(CsvMappingSchema.SchemaUrl, UriKind.Absolute));
    }

    [Fact]
    public void SchemaUrl_PointsToGitHubRaw()
    {
        Assert.Contains("raw.githubusercontent.com", CsvMappingSchema.SchemaUrl);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("1.1", true)]
    [InlineData("1.99", true)]
    [InlineData(null, true)]
    [InlineData("", true)]
    public void IsCompatible_ReturnsTrueForSameMajorVersion(string? version, bool expected)
    {
        var result = CsvMappingSchema.IsCompatible(version);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("2.0")]
    [InlineData("3.1")]
    [InlineData("0.1")]
    public void IsCompatible_ReturnsFalseForDifferentMajorVersion(string version)
    {
        var result = CsvMappingSchema.IsCompatible(version);

        Assert.False(result);
    }

    [Theory]
    [InlineData("1.1", true)]
    [InlineData("1.99", true)]
    public void IsNewerMinorVersion_ReturnsTrue_WhenMinorHigher(string version, bool expected)
    {
        var result = CsvMappingSchema.IsNewerMinorVersion(version);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("2.0", false)] // Different major = not newer minor
    public void IsNewerMinorVersion_ReturnsFalse_WhenSameOrLowerOrDifferentMajor(string? version, bool expected)
    {
        var result = CsvMappingSchema.IsNewerMinorVersion(version);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("1.0", 1, 0)]
    [InlineData("2.5", 2, 5)]
    [InlineData("10.99", 10, 99)]
    [InlineData("1", 1, 0)]
    [InlineData("", 0, 0)]
    [InlineData("abc", 0, 0)]
    [InlineData("1.x", 1, 0)]
    public void ParseVersion_ExtractsMajorAndMinor(string version, int expectedMajor, int expectedMinor)
    {
        var (major, minor) = CsvMappingSchema.ParseVersion(version);

        Assert.Equal(expectedMajor, major);
        Assert.Equal(expectedMinor, minor);
    }

    [Theory]
    [InlineData("1.0")]
    [InlineData("1.1")]
    [InlineData(null)]
    [InlineData("")]
    public void ValidateVersion_DoesNotThrow_ForCompatibleVersion(string? version)
    {
        var exception = Record.Exception(() =>
            CsvMappingSchema.ValidateVersion(version));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateVersion_Throws_ForIncompatibleMajorVersion()
    {
        Assert.Throws<SchemaVersionException>(() =>
            CsvMappingSchema.ValidateVersion("2.0"));
    }

    [Fact]
    public void ValidateVersion_CallsWarningCallback_ForNewerMinorVersion()
    {
        string? warningMessage = null;

        CsvMappingSchema.ValidateVersion("1.99", msg => warningMessage = msg);

        Assert.NotNull(warningMessage);
        Assert.Contains("1.99", warningMessage);
    }

    [Fact]
    public void ValidateVersion_DoesNotCallWarningCallback_ForSameVersion()
    {
        string? warningMessage = null;

        CsvMappingSchema.ValidateVersion("1.0", msg => warningMessage = msg);

        Assert.Null(warningMessage);
    }

    [Fact]
    public void ValidateVersion_DoesNotCallWarningCallback_WhenCallbackIsNull()
    {
        // Should not throw even when callback is null and version is newer
        var exception = Record.Exception(() =>
            CsvMappingSchema.ValidateVersion("1.99", null));

        Assert.Null(exception);
    }

    [Fact]
    public void ValidateVersion_ThrowsWithCorrectVersionInfo()
    {
        var ex = Assert.Throws<SchemaVersionException>(() =>
            CsvMappingSchema.ValidateVersion("2.0"));

        Assert.Equal("2.0", ex.FileVersion);
        Assert.Equal(CsvMappingSchema.CurrentVersion, ex.CliVersion);
    }
}
