using FluentAssertions;
using PPDS.Mcp.Tools;
using Xunit;

namespace PPDS.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="DataProvidersListTool"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataProvidersListToolTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new DataProvidersListTool(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("context");
    }

    #endregion

    #region Result Shape Tests (H3 service-layer delegation)

    [Fact]
    public void DataProvidersListResult_HasSeparateCountsForSourcesAndProviders()
    {
        // The result must carry distinct counts for data sources and data providers.
        var result = new DataProvidersListResult
        {
            DataSourceCount = 3,
            DataProviderCount = 7,
            DataSources = [],
            DataProviders = []
        };

        result.DataSourceCount.Should().Be(3);
        result.DataProviderCount.Should().Be(7);
    }

    [Fact]
    public void DataProviderSummary_DoesNotExposeCreatedOnOrModifiedOn()
    {
        // entitydataprovider.createdon is not reliably queryable (shakedown H3 root cause).
        // The summary type must NOT expose these fields — they are omitted from the
        // service model (DataProviderInfo).
        var type = typeof(DataProviderSummary);
        type.GetProperty("CreatedOn").Should().BeNull(
            because: "entitydataprovider.createdon causes Dataverse error 0x80041103 — field removed from summary");
        type.GetProperty("ModifiedOn").Should().BeNull(
            because: "entitydataprovider.modifiedon causes Dataverse error 0x80041103 — field removed from summary");
    }

    #endregion
}
