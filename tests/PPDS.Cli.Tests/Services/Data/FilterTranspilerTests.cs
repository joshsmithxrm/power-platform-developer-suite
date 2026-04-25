using FluentAssertions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

public class FilterTranspilerTests
{
    [Fact]
    public void TranspileToFetchXmlFilter_SimpleEquality_ReturnsFetchXmlFilter()
    {
        var result = FilterTranspiler.TranspileToFetchXmlFilter("account", "statecode = 0");

        result.Should().Contain("statecode");
        result.Should().Contain("eq");
        result.Should().Contain("value=\"0\"");
        result.Should().StartWith("<filter");
    }

    [Fact]
    public void TranspileToFetchXmlFilter_GreaterThan_ReturnsFetchXmlFilter()
    {
        var result = FilterTranspiler.TranspileToFetchXmlFilter("contact", "revenue > 10000");

        result.Should().Contain("revenue");
        result.Should().Contain("gt");
    }

    [Fact]
    public void TranspileToFetchXmlFilter_LikePattern_ReturnsFetchXmlFilter()
    {
        var result = FilterTranspiler.TranspileToFetchXmlFilter("account", "name LIKE '%test%'");

        result.Should().Contain("name");
        result.Should().Contain("like");
    }

    [Fact]
    public void TranspileToFetchXmlFilter_CompoundAndCondition_ReturnsSingleFilter()
    {
        var result = FilterTranspiler.TranspileToFetchXmlFilter("account", "statecode = 0 AND name LIKE '%test%'");

        result.Should().Contain("statecode");
        result.Should().Contain("name");
        result.Should().Contain("and");
    }

    [Fact]
    public void TranspileToFetchXmlFilter_DateComparison_ReturnsFetchXmlFilter()
    {
        var result = FilterTranspiler.TranspileToFetchXmlFilter("contact", "createdon > '2024-01-01'");

        result.Should().Contain("createdon");
        result.Should().Contain("gt");
        result.Should().Contain("2024-01-01");
    }

    [Fact]
    public void TranspileToFetchXmlFilter_InvalidSql_ThrowsPpdsException()
    {
        var act = () => FilterTranspiler.TranspileToFetchXmlFilter("account", "NOT VALID SQL %%% !!!");

        act.Should().Throw<PpdsException>()
            .Which.ErrorCode.Should().Be(ErrorCodes.Query.ParseError);
    }

    [Fact]
    public void TranspileToFetchXmlFilter_InvalidEntityName_ThrowsPpdsException()
    {
        var act = () => FilterTranspiler.TranspileToFetchXmlFilter("invalid entity!", "statecode = 0");

        act.Should().Throw<PpdsException>()
            .Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    [Fact]
    public void TranspileToFetchXmlFilter_ExceptionMessage_ContainsParserDetail()
    {
        var act = () => FilterTranspiler.TranspileToFetchXmlFilter("account", "= = =");

        act.Should().Throw<PpdsException>()
            .Which.Message.Should().NotBeEmpty();
    }
}
