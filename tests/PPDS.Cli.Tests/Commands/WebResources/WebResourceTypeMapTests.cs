using PPDS.Cli.Commands.WebResources;
using Xunit;

namespace PPDS.Cli.Tests.Commands.WebResources;

/// <summary>
/// Tests for the single-code resolution used by 'webresources create' type
/// inference and override (#1207).
/// </summary>
[Trait("Category", "Unit")]
public class WebResourceTypeMapTests
{
    [Theory]
    [InlineData("html", 1)]
    [InlineData("htm", 1)]
    [InlineData("css", 2)]
    [InlineData("js", 3)]
    [InlineData("javascript", 3)]
    [InlineData("xml", 4)]
    [InlineData("png", 5)]
    [InlineData("jpg", 6)]
    [InlineData("jpeg", 6)]
    [InlineData("gif", 7)]
    [InlineData("xap", 8)]
    [InlineData("xsl", 9)]
    [InlineData("xslt", 9)]
    [InlineData("ico", 10)]
    [InlineData("svg", 11)]
    [InlineData("SVG", 11)]
    [InlineData("resx", 12)]
    public void TryGetSingleCode_ResolvesSingleTypeAliases(string alias, int expectedCode)
    {
        var found = WebResourceTypeMap.TryGetSingleCode(alias, out var code);

        Assert.True(found);
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("image")]
    [InlineData("data")]
    [InlineData("exe")]
    [InlineData("")]
    public void TryGetSingleCode_RejectsMultiTypeAndUnknownAliases(string alias)
    {
        var found = WebResourceTypeMap.TryGetSingleCode(alias, out _);

        Assert.False(found);
    }
}
