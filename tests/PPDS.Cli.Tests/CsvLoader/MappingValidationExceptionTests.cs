using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class MappingValidationExceptionTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var unconfigured = new List<string> { "state", "county" };
        var missing = new List<string> { "newcol" };
        var stale = new List<string> { "oldcol" };

        var exception = new MappingValidationException(unconfigured, missing, stale);

        Assert.Equal(2, exception.UnconfiguredColumns.Count);
        Assert.Single(exception.MissingMappings);
        Assert.Single(exception.StaleMappings);
    }

    [Fact]
    public void Message_ContainsUnconfiguredCount()
    {
        var unconfigured = new List<string> { "col1", "col2", "col3" };
        var missing = new List<string>();
        var stale = new List<string>();

        var exception = new MappingValidationException(unconfigured, missing, stale);

        Assert.Contains("3 column(s) have no field configured", exception.Message);
    }

    [Fact]
    public void Message_ContainsMissingCount()
    {
        var unconfigured = new List<string>();
        var missing = new List<string> { "newcol1", "newcol2" };
        var stale = new List<string>();

        var exception = new MappingValidationException(unconfigured, missing, stale);

        Assert.Contains("2 CSV column(s) not found in mapping file", exception.Message);
    }

    [Fact]
    public void Message_ContainsBothIssues()
    {
        var unconfigured = new List<string> { "col1" };
        var missing = new List<string> { "newcol" };
        var stale = new List<string>();

        var exception = new MappingValidationException(unconfigured, missing, stale);

        Assert.Contains("1 column(s) have no field configured", exception.Message);
        Assert.Contains("1 CSV column(s) not found in mapping file", exception.Message);
    }

    [Fact]
    public void StaleMappings_NotIncludedInMessage()
    {
        // Stale mappings are warnings only, not included in exception message
        var unconfigured = new List<string>();
        var missing = new List<string>();
        var stale = new List<string> { "oldcol1", "oldcol2" };

        var exception = new MappingValidationException(unconfigured, missing, stale);

        Assert.DoesNotContain("oldcol", exception.Message);
        Assert.Equal(2, exception.StaleMappings.Count);
    }

    [Fact]
    public void EmptyLists_AreValid()
    {
        var exception = new MappingValidationException(
            new List<string>(),
            new List<string>(),
            new List<string>());

        Assert.Empty(exception.UnconfiguredColumns);
        Assert.Empty(exception.MissingMappings);
        Assert.Empty(exception.StaleMappings);
    }
}
