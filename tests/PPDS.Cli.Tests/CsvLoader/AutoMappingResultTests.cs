using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class AutoMappingResultTests
{
    #region MatchRate Tests

    [Fact]
    public void MatchRate_AllMatched_Returns1()
    {
        var result = new AutoMappingResult
        {
            Mappings = new Dictionary<string, ColumnMappingEntry>(),
            TotalColumns = 5,
            MatchedColumns = 5,
            UnmatchedColumns = new List<UnmatchedColumn>(),
            Warnings = new List<string>()
        };

        Assert.Equal(1.0, result.MatchRate);
    }

    [Fact]
    public void MatchRate_NoneMatched_Returns0()
    {
        var result = new AutoMappingResult
        {
            Mappings = new Dictionary<string, ColumnMappingEntry>(),
            TotalColumns = 5,
            MatchedColumns = 0,
            UnmatchedColumns = new List<UnmatchedColumn>(),
            Warnings = new List<string>()
        };

        Assert.Equal(0.0, result.MatchRate);
    }

    [Fact]
    public void MatchRate_PartialMatch_ReturnsCorrectRatio()
    {
        var result = new AutoMappingResult
        {
            Mappings = new Dictionary<string, ColumnMappingEntry>(),
            TotalColumns = 10,
            MatchedColumns = 7,
            UnmatchedColumns = new List<UnmatchedColumn>(),
            Warnings = new List<string>()
        };

        Assert.Equal(0.7, result.MatchRate);
    }

    [Fact]
    public void MatchRate_ZeroTotalColumns_Returns0()
    {
        var result = new AutoMappingResult
        {
            Mappings = new Dictionary<string, ColumnMappingEntry>(),
            TotalColumns = 0,
            MatchedColumns = 0,
            UnmatchedColumns = new List<UnmatchedColumn>(),
            Warnings = new List<string>()
        };

        Assert.Equal(0.0, result.MatchRate);
    }

    #endregion

    #region IsComplete Tests

    [Fact]
    public void IsComplete_NoUnmatchedColumns_ReturnsTrue()
    {
        var result = new AutoMappingResult
        {
            Mappings = new Dictionary<string, ColumnMappingEntry>(),
            TotalColumns = 5,
            MatchedColumns = 5,
            UnmatchedColumns = new List<UnmatchedColumn>(),
            Warnings = new List<string>()
        };

        Assert.True(result.IsComplete);
    }

    [Fact]
    public void IsComplete_HasUnmatchedColumns_ReturnsFalse()
    {
        var result = new AutoMappingResult
        {
            Mappings = new Dictionary<string, ColumnMappingEntry>(),
            TotalColumns = 5,
            MatchedColumns = 3,
            UnmatchedColumns = new List<UnmatchedColumn>
            {
                new UnmatchedColumn { ColumnName = "foo" },
                new UnmatchedColumn { ColumnName = "bar" }
            },
            Warnings = new List<string>()
        };

        Assert.False(result.IsComplete);
    }

    #endregion

    #region UnmatchedColumn Tests

    [Fact]
    public void UnmatchedColumn_WithSuggestions_HasSuggestions()
    {
        var column = new UnmatchedColumn
        {
            ColumnName = "state",
            Suggestions = new List<string> { "ppds_stateid", "ppds_state" }
        };

        Assert.Equal("state", column.ColumnName);
        Assert.NotNull(column.Suggestions);
        Assert.Equal(2, column.Suggestions.Count);
    }

    [Fact]
    public void UnmatchedColumn_WithoutSuggestions_HasNullSuggestions()
    {
        var column = new UnmatchedColumn
        {
            ColumnName = "unknownfield",
            Suggestions = null
        };

        Assert.Null(column.Suggestions);
    }

    #endregion
}
