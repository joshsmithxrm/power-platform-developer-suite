using FluentAssertions;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Planning;

[Trait("Category", "Unit")]
public class IndexSpoolNodeTests
{
    [Fact]
    public async Task Lookup_CachesResultsByKey()
    {
        var executionCount = 0;

        IAsyncEnumerable<QueryRow> InnerFactory(object keyValue)
        {
            executionCount++;
            return AsyncEnumerable(
                TestSourceNode.MakeRow("contact", ("name", $"Contact for {keyValue}")));
        }

        var spool = new IndexSpoolNode(InnerFactory);
        var context = TestHelpers.CreateTestContext();

        var rows1 = await spool.LookupAsync("1", context);
        rows1.Should().HaveCount(1);

        var rows2 = await spool.LookupAsync("1", context);
        rows2.Should().HaveCount(1);

        executionCount.Should().Be(1);
    }

    [Fact]
    public async Task Lookup_DifferentKeys_CallsFactoryPerKey()
    {
        var executionCount = 0;

        IAsyncEnumerable<QueryRow> InnerFactory(object keyValue)
        {
            executionCount++;
            return AsyncEnumerable(
                TestSourceNode.MakeRow("contact", ("name", $"Contact for {keyValue}")));
        }

        var spool = new IndexSpoolNode(InnerFactory);
        var context = TestHelpers.CreateTestContext();

        await spool.LookupAsync("1", context);
        await spool.LookupAsync("2", context);
        await spool.LookupAsync("1", context); // cached

        executionCount.Should().Be(2);
    }

    private static async IAsyncEnumerable<QueryRow> AsyncEnumerable(params QueryRow[] rows)
    {
        foreach (var row in rows)
            yield return row;
        await Task.CompletedTask;
    }
}
