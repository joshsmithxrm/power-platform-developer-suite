using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class MappingIncompleteExceptionTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var unmatchedColumns = new List<UnmatchedColumn>
        {
            new UnmatchedColumn { ColumnName = "state", Suggestions = new List<string> { "ppds_stateid" } },
            new UnmatchedColumn { ColumnName = "county", Suggestions = null }
        };

        var exception = new MappingIncompleteException(7, 10, unmatchedColumns);

        Assert.Equal(7, exception.MatchedColumns);
        Assert.Equal(10, exception.TotalColumns);
        Assert.Equal(2, exception.UnmatchedColumns.Count);
    }

    [Fact]
    public void Message_ContainsMatchDetails()
    {
        var unmatchedColumns = new List<UnmatchedColumn>
        {
            new UnmatchedColumn { ColumnName = "foo" }
        };

        var exception = new MappingIncompleteException(5, 6, unmatchedColumns);

        Assert.Contains("5", exception.Message);
        Assert.Contains("6", exception.Message);
    }

    [Fact]
    public void UnmatchedColumns_IsReadOnly()
    {
        var unmatchedColumns = new List<UnmatchedColumn>
        {
            new UnmatchedColumn { ColumnName = "test" }
        };

        var exception = new MappingIncompleteException(0, 1, unmatchedColumns);

        Assert.IsAssignableFrom<IReadOnlyList<UnmatchedColumn>>(exception.UnmatchedColumns);
    }

    [Fact]
    public void EmptyUnmatchedColumns_IsValid()
    {
        var exception = new MappingIncompleteException(5, 5, new List<UnmatchedColumn>());

        Assert.Empty(exception.UnmatchedColumns);
        Assert.Equal(5, exception.MatchedColumns);
    }
}
