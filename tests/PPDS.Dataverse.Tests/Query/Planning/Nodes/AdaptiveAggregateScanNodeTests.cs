using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Dataverse.Tests.Query.Planning.Nodes;

[Trait("Category", "PlanUnit")]
public class AdaptiveAggregateScanNodeTests
{
    private const string TemplateFetchXml =
        "<fetch aggregate=\"true\">" +
        "<entity name=\"contact\">" +
        "<attribute name=\"contactid\" aggregate=\"count\" alias=\"cnt\" />" +
        "</entity>" +
        "</fetch>";

    private static readonly DateTime RangeStart = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime RangeEnd = new(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Creates a <see cref="QueryResult"/> with a single aggregate row containing the given count.
    /// </summary>
    private static QueryResult MakeCountResult(long count)
    {
        var record = new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase)
        {
            ["cnt"] = QueryValue.Simple(count)
        };
        return new QueryResult
        {
            EntityLogicalName = "contact",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "contactid", Alias = "cnt", IsAggregate = true }
            },
            Records = new[] { record },
            Count = 1,
            MoreRecords = false,
            IsAggregate = true
        };
    }

    #region Structural Tests

    [Fact]
    public void Description_IncludesEntityAndDateRange()
    {
        var node = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd);

        Assert.Contains("contact", node.Description);
        Assert.Contains("2024-01-01", node.Description);
        Assert.Contains("2024-07-01", node.Description);
    }

    [Fact]
    public void Description_ShowsDepthWhenNonZero()
    {
        var nodeDepthZero = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd, depth: 0);
        var nodeDepthThree = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd, depth: 3);

        Assert.DoesNotContain("depth=", nodeDepthZero.Description);
        Assert.Contains("depth=3", nodeDepthThree.Description);
    }

    [Fact]
    public async Task InjectDateRangeFilter_InsertsFilterBeforeEntityClose()
    {
        // Capture the FetchXML that gets passed to ExecuteFetchXmlAsync to verify
        // the date range filter injection (InjectDateRangeFilter is internal).
        string? capturedFetchXml = null;
        var mockExecutor = new Mock<IQueryExecutor>();
        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, int?, string?, bool, CancellationToken>((xml, _, _, _, _) =>
                capturedFetchXml = xml)
            .ReturnsAsync(MakeCountResult(1L));

        var context = new QueryPlanContext(mockExecutor.Object);
        var node = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd);

        await foreach (var _ in node.ExecuteAsync(context)) { }

        Assert.NotNull(capturedFetchXml);

        // Verify the ge/lt conditions are present
        Assert.Contains("operator=\"ge\"", capturedFetchXml);
        Assert.Contains("operator=\"lt\"", capturedFetchXml);
        Assert.Contains("attribute=\"createdon\"", capturedFetchXml);

        // Verify the date values are formatted correctly
        var expectedStart = RangeStart.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var expectedEnd = RangeEnd.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        Assert.Contains(expectedStart, capturedFetchXml);
        Assert.Contains(expectedEnd, capturedFetchXml);

        // Verify the filter is inserted before </entity>
        var filterIndex = capturedFetchXml.IndexOf("<filter", StringComparison.Ordinal);
        var entityCloseIndex = capturedFetchXml.IndexOf("</entity>", StringComparison.Ordinal);
        Assert.True(filterIndex >= 0, "Filter element should be present");
        Assert.True(filterIndex < entityCloseIndex, "Filter should appear before </entity>");
    }

    [Fact]
    public async Task InjectDateRangeFilter_ThrowsForInvalidFetchXml()
    {
        // FetchXML without </entity> should cause InjectDateRangeFilter to throw
        // InvalidOperationException during ExecuteAsync.
        var badFetchXml = "<fetch><no-entity-here /></fetch>";
        var mockExecutor = new Mock<IQueryExecutor>();
        var context = new QueryPlanContext(mockExecutor.Object);

        var node = new AdaptiveAggregateScanNode(
            badFetchXml, "contact", RangeStart, RangeEnd);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context)) { }
        });
    }

    #endregion

    #region Execution Tests

    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttempt_WhenUnderLimit()
    {
        var mockExecutor = new Mock<IQueryExecutor>();

        // Any FetchXML call returns a single count row
        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCountResult(12345L));

        var context = new QueryPlanContext(mockExecutor.Object);
        var node = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        Assert.Single(rows);
        Assert.Equal(12345L, rows[0].Values["cnt"].Value);

        // Verify exactly one call was made (no splits)
        mockExecutor.Verify(
            e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesWithSplit_WhenAggregateLimitExceeded()
    {
        var mockExecutor = new Mock<IQueryExecutor>();

        // Determine whether the FetchXML covers a "wide" or "narrow" date range.
        // The original range is 2024-01-01 to 2024-07-01 (6 months).
        // After one split: left = Jan 1 - Apr 1 (approx), right = Apr 1 - Jul 1 (approx).
        // We fail for ranges > 90 days and succeed for <= 90 days.
        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns<string, int?, string?, bool, CancellationToken>((fetchXml, _, _, _, _) =>
            {
                // Parse date conditions from the injected filter using regex
                var geMatch = Regex.Match(fetchXml, @"operator=""ge""\s+value=""([^""]+)""");
                var ltMatch = Regex.Match(fetchXml, @"operator=""lt""\s+value=""([^""]+)""");

                if (!geMatch.Success || !ltMatch.Success)
                {
                    // No date filter — fail with aggregate limit
                    throw new InvalidOperationException(
                        "The aggregate operation exceeded the maximum record limit of 50000.");
                }

                var geValue = DateTime.Parse(
                    geMatch.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var ltValue = DateTime.Parse(
                    ltMatch.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                var rangeDays = (ltValue - geValue).TotalDays;

                if (rangeDays > 90)
                {
                    // "Wide" range — simulate the 50K limit error
                    throw new InvalidOperationException(
                        "The aggregate operation exceeded the maximum record limit of 50000.");
                }

                // "Narrow" range — return a count result
                return Task.FromResult(MakeCountResult(5000L));
            });

        var context = new QueryPlanContext(mockExecutor.Object);
        var node = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd);

        var rows = new List<QueryRow>();
        await foreach (var row in node.ExecuteAsync(context))
        {
            rows.Add(row);
        }

        // The original range (182 days) will split recursively until each sub-range is <= 90 days.
        // We should get multiple result rows (one per successfully executed sub-partition).
        Assert.True(rows.Count > 1,
            $"Expected multiple rows from sub-partitions but got {rows.Count}");

        // All rows should have the count value
        Assert.All(rows, row => Assert.Equal(5000L, row.Values["cnt"].Value));

        // More than one call to the executor (proving splits happened)
        mockExecutor.Verify(
            e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeast(3));
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesNonAggregateErrors()
    {
        var mockExecutor = new Mock<IQueryExecutor>();

        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Network error: connection timed out"));

        var context = new QueryPlanContext(mockExecutor.Object);
        var node = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context))
            {
                // Should not reach here
            }
        });

        Assert.Contains("Network error", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellation()
    {
        var mockExecutor = new Mock<IQueryExecutor>();

        // Set up executor to return a result (should never be reached due to cancellation)
        mockExecutor
            .Setup(e => e.ExecuteFetchXmlAsync(
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeCountResult(100L));

        var context = new QueryPlanContext(mockExecutor.Object);
        var node = new AdaptiveAggregateScanNode(
            TemplateFetchXml, "contact", RangeStart, RangeEnd);

        // Pre-cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in node.ExecuteAsync(context, cts.Token))
            {
                // Should not reach here
            }
        });
    }

    #endregion
}
