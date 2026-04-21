using FluentAssertions;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="WebResourcesListTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WebResourcesListToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new WebResourcesListTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Result Shape Tests (H6 pagination)

    [Fact]
    public void WebResourcesListResult_HasPaginationFields()
    {
        // The result type must expose nextPageToken for H6 pagination contract.
        var result = new WebResourcesListResult
        {
            TotalCount = 200,
            ReturnedCount = 100,
            Offset = 0,
            NextPageToken = "MTAw",
            Resources = []
        };

        result.TotalCount.Should().Be(200);
        result.ReturnedCount.Should().Be(100);
        result.Offset.Should().Be(0);
        result.NextPageToken.Should().Be("MTAw");
    }

    [Fact]
    public void WebResourcesListResult_LastPage_HasNullNextPageToken()
    {
        // When no more records exist, NextPageToken must be null.
        var result = new WebResourcesListResult
        {
            TotalCount = 50,
            ReturnedCount = 50,
            Offset = 0,
            NextPageToken = null,
            Resources = []
        };

        result.NextPageToken.Should().BeNull();
    }

    #endregion
}
