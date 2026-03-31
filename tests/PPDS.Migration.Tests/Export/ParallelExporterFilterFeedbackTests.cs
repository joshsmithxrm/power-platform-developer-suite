using FluentAssertions;
using PPDS.Migration.Export;
using Xunit;

namespace PPDS.Migration.Tests.Export;

[Trait("Category", "Unit")]
public class ParallelExporterFilterFeedbackTests
{
    #region SummarizeFilter

    [Fact]
    public void SummarizeFilter_SingleCondition_ReturnsAttributeOperatorValue()
    {
        var filter = "<filter><condition attribute='statecode' operator='eq' value='0'/></filter>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Be("statecode eq '0'");
    }

    [Fact]
    public void SummarizeFilter_MultipleConditions_JoinsWithAnd()
    {
        var filter = "<filter><condition attribute='country' operator='eq' value='US'/><condition attribute='statecode' operator='eq' value='0'/></filter>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Be("country eq 'US' AND statecode eq '0'");
    }

    [Fact]
    public void SummarizeFilter_ConditionWithoutValue_OmitsValue()
    {
        var filter = "<filter><condition attribute='name' operator='not-null'/></filter>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Be("name not-null");
    }

    [Fact]
    public void SummarizeFilter_MoreThanThreeConditions_TruncatesWithCount()
    {
        var filter = "<filter>" +
            "<condition attribute='a' operator='eq' value='1'/>" +
            "<condition attribute='b' operator='eq' value='2'/>" +
            "<condition attribute='c' operator='eq' value='3'/>" +
            "<condition attribute='d' operator='eq' value='4'/>" +
            "</filter>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Contain("a eq '1'");
        result.Should().Contain("b eq '2'");
        result.Should().Contain("c eq '3'");
        result.Should().Contain("(+1 more)");
        result.Should().NotContain("d eq '4'");
    }

    [Fact]
    public void SummarizeFilter_EmptyFilter_ReturnsNoConditionsMessage()
    {
        var filter = "<filter></filter>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Be("(filter — no conditions)");
    }

    [Fact]
    public void SummarizeFilter_NestedFilter_ExtractsConditions()
    {
        var filter = "<filter type='and'><filter type='or'><condition attribute='x' operator='eq' value='1'/><condition attribute='y' operator='eq' value='2'/></filter></filter>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Contain("x eq '1'");
        result.Should().Contain("y eq '2'");
    }

    [Fact]
    public void SummarizeFilter_MalformedXml_ReturnsFallback()
    {
        var filter = "not valid xml <<>>";

        var result = ParallelExporter.SummarizeFilter(filter);

        result.Should().Be("(filter)");
    }

    #endregion
}
