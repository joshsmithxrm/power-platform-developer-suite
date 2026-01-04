using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class MappingAnalysisTests
{
    #region MatchRate Tests

    [Fact]
    public void MatchRate_AllMatched_Returns1()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 5,
            MatchedColumns = 5,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.Equal(1.0, analysis.MatchRate);
    }

    [Fact]
    public void MatchRate_NoneMatched_Returns0()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 5,
            MatchedColumns = 0,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.Equal(0.0, analysis.MatchRate);
    }

    [Fact]
    public void MatchRate_PartialMatch_ReturnsCorrectRatio()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 10,
            MatchedColumns = 7,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.Equal(0.7, analysis.MatchRate);
    }

    [Fact]
    public void MatchRate_ZeroTotalColumns_Returns0()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 0,
            MatchedColumns = 0,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.Equal(0.0, analysis.MatchRate);
    }

    #endregion

    #region IsComplete Tests

    [Fact]
    public void IsComplete_AllMatched_ReturnsTrue()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 5,
            MatchedColumns = 5,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.True(analysis.IsComplete);
    }

    [Fact]
    public void IsComplete_PartialMatch_ReturnsFalse()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 5,
            MatchedColumns = 3,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.False(analysis.IsComplete);
    }

    #endregion

    #region Prefix Tests

    [Fact]
    public void Prefix_CanBeSet()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "ppds_city",
            TotalColumns = 1,
            MatchedColumns = 1,
            Prefix = "ppds_",
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.Equal("ppds_", analysis.Prefix);
    }

    [Fact]
    public void Prefix_CanBeNull()
    {
        var analysis = new MappingAnalysis
        {
            Entity = "account",
            TotalColumns = 1,
            MatchedColumns = 1,
            Prefix = null,
            Columns = new List<ColumnAnalysis>(),
            Recommendations = new List<string>()
        };

        Assert.Null(analysis.Prefix);
    }

    #endregion
}

public class ColumnAnalysisTests
{
    [Fact]
    public void MatchedColumn_HasRequiredProperties()
    {
        var column = new ColumnAnalysis
        {
            CsvColumn = "name",
            IsMatched = true,
            TargetAttribute = "ppds_name",
            MatchType = "prefix",
            AttributeType = "String"
        };

        Assert.Equal("name", column.CsvColumn);
        Assert.True(column.IsMatched);
        Assert.Equal("ppds_name", column.TargetAttribute);
        Assert.Equal("prefix", column.MatchType);
        Assert.Equal("String", column.AttributeType);
    }

    [Fact]
    public void UnmatchedColumn_HasSuggestions()
    {
        var column = new ColumnAnalysis
        {
            CsvColumn = "state",
            IsMatched = false,
            Suggestions = new List<string> { "ppds_stateid", "ppds_state" }
        };

        Assert.False(column.IsMatched);
        Assert.Null(column.TargetAttribute);
        Assert.Equal(2, column.Suggestions!.Count);
    }

    [Fact]
    public void LookupColumn_HasIsLookupFlag()
    {
        var column = new ColumnAnalysis
        {
            CsvColumn = "stateid",
            IsMatched = true,
            TargetAttribute = "ppds_stateid",
            IsLookup = true,
            AttributeType = "Lookup"
        };

        Assert.True(column.IsLookup);
    }

    [Fact]
    public void Column_CanHaveSampleValues()
    {
        var column = new ColumnAnalysis
        {
            CsvColumn = "abbreviation",
            IsMatched = true,
            SampleValues = new List<string> { "CA", "TX", "NY" }
        };

        Assert.Equal(3, column.SampleValues!.Count);
        Assert.Contains("CA", column.SampleValues);
    }

    [Fact]
    public void NonLookupColumn_HasIsLookupFalse()
    {
        var column = new ColumnAnalysis
        {
            CsvColumn = "name",
            IsMatched = true,
            IsLookup = false
        };

        Assert.False(column.IsLookup);
    }
}
