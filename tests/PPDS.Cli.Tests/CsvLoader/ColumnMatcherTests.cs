using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.CsvLoader;
using Xunit;

namespace PPDS.Cli.Tests.CsvLoader;

public class ColumnMatcherTests
{
    [Fact]
    public void MaxSampleValues_IsThree()
    {
        Assert.Equal(3, ColumnMatcher.MaxSampleValues);
    }

    [Theory]
    [InlineData("ppds_city", "ppds_")]
    [InlineData("ppds_zipcode", "ppds_")]
    [InlineData("msfp_survey", "msfp_")]
    [InlineData("cr123_entity", "cr123_")]
    public void ExtractPublisherPrefix_ReturnsPrefix(string entityName, string expected)
    {
        var result = ColumnMatcher.ExtractPublisherPrefix(entityName);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("contact")]
    [InlineData("account")]
    [InlineData("systemuser")]
    [InlineData("")]
    public void ExtractPublisherPrefix_ReturnsNull_WhenNoUnderscore(string entityName)
    {
        var result = ColumnMatcher.ExtractPublisherPrefix(entityName);

        Assert.Null(result);
    }

    [Theory]
    [InlineData("City Name", "cityname")]
    [InlineData("city_name", "cityname")]
    [InlineData("city-name", "cityname")]
    [InlineData("CityName", "cityname")]
    [InlineData("CITY_NAME", "cityname")]
    public void NormalizeForMatching_RemovesSpecialCharsAndLowers(string input, string expected)
    {
        var result = ColumnMatcher.NormalizeForMatching(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsLookupAttribute_ReturnsTrue_ForLookupType()
    {
        var attr = new LookupAttributeMetadata { LogicalName = "parentid" };

        Assert.True(ColumnMatcher.IsLookupAttribute(attr));
    }

    [Fact]
    public void IsLookupAttribute_ReturnsFalse_ForStringType()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name" };

        Assert.False(ColumnMatcher.IsLookupAttribute(attr));
    }

    [Fact]
    public void IsOptionSetAttribute_ReturnsTrue_ForPicklist()
    {
        var attr = new PicklistAttributeMetadata { LogicalName = "status" };

        Assert.True(ColumnMatcher.IsOptionSetAttribute(attr));
    }

    [Fact]
    public void IsOptionSetAttribute_ReturnsTrue_ForState()
    {
        var attr = new StateAttributeMetadata { LogicalName = "statecode" };

        Assert.True(ColumnMatcher.IsOptionSetAttribute(attr));
    }

    [Fact]
    public void IsOptionSetAttribute_ReturnsTrue_ForStatus()
    {
        var attr = new StatusAttributeMetadata { LogicalName = "statuscode" };

        Assert.True(ColumnMatcher.IsOptionSetAttribute(attr));
    }

    [Fact]
    public void IsOptionSetAttribute_ReturnsFalse_ForString()
    {
        var attr = new StringAttributeMetadata { LogicalName = "name" };

        Assert.False(ColumnMatcher.IsOptionSetAttribute(attr));
    }

    [Fact]
    public void BuildAttributeLookup_ReturnsEmpty_WhenNoAttributes()
    {
        var entityMetadata = new EntityMetadata();
        // No attributes set

        var lookup = ColumnMatcher.BuildAttributeLookup(entityMetadata);

        Assert.Empty(lookup);
    }

    [Fact]
    public void TryFindAttribute_FindsExactMatch()
    {
        var attr = new StringAttributeMetadata { LogicalName = "ppds_name" };
        var attributes = new Dictionary<string, AttributeMetadata>
        {
            ["ppds_name"] = attr
        };

        var found = ColumnMatcher.TryFindAttribute("ppdsname", attributes, out var result);

        Assert.True(found);
        Assert.Same(attr, result);
    }

    [Fact]
    public void TryFindAttribute_ReturnsFalse_WhenNotFound()
    {
        var attributes = new Dictionary<string, AttributeMetadata>();

        var found = ColumnMatcher.TryFindAttribute("nonexistent", attributes, out var result);

        Assert.False(found);
        Assert.Null(result);
    }

    [Fact]
    public void FindSimilarAttributes_FromDictionary_FindsSimilarBySubstring()
    {
        var attributes = new Dictionary<string, AttributeMetadata>
        {
            ["ppds_cityid"] = new StringAttributeMetadata { LogicalName = "ppds_cityid" },
            ["ppds_stateid"] = new StringAttributeMetadata { LogicalName = "ppds_stateid" }
        };

        var similar = ColumnMatcher.FindSimilarAttributes("city", attributes, "ppds_");

        Assert.Contains("ppds_cityid", similar);
    }

    [Fact]
    public void FindSimilarAttributes_FromDictionary_PrioritizesPrefixedMatches()
    {
        var attributes = new Dictionary<string, AttributeMetadata>
        {
            ["ppds_city"] = new StringAttributeMetadata { LogicalName = "ppds_city" },
            ["city"] = new StringAttributeMetadata { LogicalName = "city" }
        };

        var similar = ColumnMatcher.FindSimilarAttributes("city", attributes, "ppds_");

        // Prefixed match should come first
        Assert.Equal("ppds_city", similar.First());
    }

    [Fact]
    public void FindSimilarAttributes_FromDictionary_LimitsToMaxSampleValues()
    {
        var attributes = Enumerable.Range(1, 10)
            .ToDictionary(
                i => $"ppds_city{i}",
                i => (AttributeMetadata)new StringAttributeMetadata { LogicalName = $"ppds_city{i}" });

        var similar = ColumnMatcher.FindSimilarAttributes("city", attributes, "ppds_");

        Assert.True(similar.Count <= ColumnMatcher.MaxSampleValues);
    }

    [Fact]
    public void FindSimilarAttributes_FromDictionary_ReturnsEmptyForNoMatch()
    {
        var attributes = new Dictionary<string, AttributeMetadata>
        {
            ["ppds_name"] = new StringAttributeMetadata { LogicalName = "ppds_name" }
        };

        var similar = ColumnMatcher.FindSimilarAttributes("zzz", attributes);

        Assert.Empty(similar);
    }

    [Fact]
    public void FindSimilarAttributes_FromDictionary_MatchesByContainment()
    {
        var attributes = new Dictionary<string, AttributeMetadata>
        {
            ["ppds_fullname"] = new StringAttributeMetadata { LogicalName = "ppds_fullname" }
        };

        var similar = ColumnMatcher.FindSimilarAttributes("name", attributes);

        Assert.Contains("ppds_fullname", similar);
    }
}
