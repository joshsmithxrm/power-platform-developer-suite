using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using Moq;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// Issue 1705: <c>data delete --filter</c> and <c>data update --filter</c> act on the ids
/// <see cref="DataQueryService.QueryIdsByFilterAsync"/> returns. Each filter must select exactly the rows SQL Server
/// would select for the same WHERE, or be refused before anything is read. The rows are answered by
/// <see cref="FakeFetchXmlDataverse"/>, which applies Dataverse's own FetchXML semantics.
/// </summary>
[Trait("Category", "Unit")]
public class DataFilterExactRowsTests
{
    private const string Entity = "account";

    /// <summary>
    /// Six accounts. r5 is named with the Greek capital omega; r6 is named '?', which is what SQL Server makes of that
    /// letter in a literal without N under a Latin code page.
    /// </summary>
    private static FakeFetchXmlDataverse SixAccounts()
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        fake.Add("r1", ("name", "x"), ("accountnumber", "A1"), ("statecode", 0), ("revenue", 100m),
            ("createdon", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)));
        fake.Add("r2", ("name", "abc"), ("accountnumber", "abc"), ("statecode", 0), ("revenue", 1000m),
            ("createdon", new DateTime(2024, 1, 2, 3, 0, 0, DateTimeKind.Utc)));
        fake.Add("r3", ("name", null), ("accountnumber", "A3"), ("statecode", 1), ("revenue", null), ("createdon", null));
        fake.Add("r4", ("name", "a"), ("accountnumber", null), ("statecode", 1), ("revenue", 800m),
            ("createdon", new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        fake.Add("r5", ("name", "\u03A9"), ("accountnumber", "A5"), ("statecode", 0), ("revenue", 5m),
            ("createdon", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        fake.Add("r6", ("name", "?"), ("accountnumber", "A6"), ("statecode", 1), ("revenue", 6m),
            ("createdon", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return fake;
    }

    /// <summary>
    /// The filters of the issue's reproduction. Each of these is refused now: what SQL Server would select is given in
    /// the comment, and what the old path sent and selected is in the pull request.
    /// </summary>
    public static TheoryData<string, string> RefusedFilters => new()
    {
        // SQL: r1. A function is not sent to Dataverse; the old path dropped it and selected r1, r2, r5.
        { "statecode = 0 AND UPPER(name) = 'X'", "function" },
        // SQL: r1, r2. The old path dropped LEN(...) and selected r1 only.
        { "name = 'x' OR LEN(name) = 3", "function" },
        // SQL: r2, r4. The old path sent no condition: every row.
        { "revenue * 2 > 1500", "arithmetic" },
        // SQL: r2. The old path sent no condition: every row.
        { "name = accountnumber", "column compared with a column" },
        // SQL: r3, r4, r6. The old path sent statecode = 0: exactly the opposite rows.
        { "NOT (statecode = 0)", "NOT" },
        // SQL: no row. The old path sent name eq ''.
        { "name = NULL", "comparison with NULL" },
        // SQL: r4. The old path sent an empty value in the list.
        { "name IN ('a', NULL)", "NULL in an IN list" },
        // SQL: r1. The old path sent no condition at all: every row.
        { "UPPER(name) = 'X'", "function" },
        // Nothing translates: SQL r2; the old path sent no condition and selected every row.
        { "LEN(name) = 3 AND revenue * 2 > 1500", "function" },
        // SQL: r2. Date functions: the old path sent no condition (every row).
        { "YEAR(createdon) = 2024 AND MONTH(createdon) = 1 AND DAY(createdon) = 2", "function" },
        // SQL: r1, r2. The old path sent no condition and also asked for an attribute named "year".
        { "DATEPART(year, createdon) = 2024", "function" },
        // SQL: r2, r5, r6. The old path sent no condition and asked for an attribute named "day".
        { "DATEADD(day, 1, createdon) > '2024-01-03T00:00:00Z'", "function" },
        // SQL: r1, r2, r5. The old path dropped the NOT and sent statecode ne 0: exactly the opposite rows.
        { "NOT (statecode <> 0)", "NOT" },
        // SQL (no time zone): r1, r4. Dataverse reads the date in the user's time zone: r1, r2, r4.
        { "createdon < '2024-01-02'", "date and time without a time zone" },
        // SQL (Latin code page): r6, since the letter becomes '?'. The old path sent the letter: r5.
        { "name = '\u03A9'", "text without N" },
    };

    [Theory]
    [MemberData(nameof(RefusedFilters))]
    public async Task Filter_TheFetchXmlCannotExpressExactly_IsRefusedBeforeAnythingIsRead(string filter, string kind)
    {
        var fake = SixAccounts();
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        IReadOnlyList<Guid>? ids = null;
        var act = async () => { ids = await service.QueryIdsByFilterAsync(Entity, filter, limit: null); };

        var ex = await act.Should().ThrowAsync<PpdsException>(
            "the filter must be refused; the old path sent [{0}] and selected [{1}]",
            string.Join("; ", fake.ConditionsSent()),
            ids is null ? "" : string.Join(", ", ids.Select(fake.LabelOf)));
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Validation.FilterNotSupported);
        ex.Which.UserMessage.Should().Contain(kind);
        fake.AnythingRead.Should().BeFalse("a refused filter reads nothing");
    }

    [Fact]
    public async Task NotEqual_DoesNotSelectRowsWhereTheColumnIsNull()
    {
        var fake = SixAccounts();
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        var ids = await service.QueryIdsByFilterAsync(Entity, "name <> 'x'", limit: null);

        // SQL: name <> 'x' is unknown for r3 (name NULL), so r3 is not selected.
        ids.Select(fake.LabelOf).Should().BeEquivalentTo(new[] { "r2", "r4", "r5", "r6" },
            "FetchXML ne also matches NULL; conditions sent: {0}", string.Join("; ", fake.ConditionsSent()));
    }

    [Fact]
    public async Task MoreThanOnePage_EveryMatchingRecordIsSelected()
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        for (var i = 0; i < 12_001; i++)
            fake.Add($"s{i}", ("statecode", 0));
        fake.Add("inactive", ("statecode", 1));
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        var ids = await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: null);

        ids.Should().HaveCount(12_001, "every matching record, not only the first page of 5,000");
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().NotContain(fake.IdOf("inactive"));
    }

    /// <summary>Admitted filters on the six accounts, with the rows SQL Server selects for the same WHERE.</summary>
    public static TheoryData<string, string[]> AdmittedFilters => new()
    {
        // r3's name is NULL: <> is unknown there in SQL.
        { "name <> 'x'", new[] { "r2", "r4", "r5", "r6" } },
        { "name NOT IN ('x', 'a')", new[] { "r2", "r5", "r6" } },
        { "name NOT LIKE 'a%'", new[] { "r1", "r5", "r6" } },
        { "revenue NOT BETWEEN 50 AND 900", new[] { "r2", "r5", "r6" } },
        { "accountnumber IS NULL", new[] { "r4" } },
        { "(statecode = 0 OR revenue > 700) AND name IS NOT NULL", new[] { "r1", "r2", "r4", "r5" } },
        { "statecode = 1 OR name <> 'abc'", new[] { "r1", "r3", "r4", "r5", "r6" } },
        { "createdon < '2024-01-02T00:00:00Z'", new[] { "r1", "r4" } },
        { "createdon < '2024-01-02T05:00:00+01:00'", new[] { "r1", "r2", "r4" } },
        { "name = N'\u03A9'", new[] { "r5" } },
        { "name LIKE '_b%'", new[] { "r2" } },
        { "statecode IN (1) AND revenue >= 6", new[] { "r4", "r6" } },
    };

    [Theory]
    [MemberData(nameof(AdmittedFilters))]
    public async Task AdmittedFilter_SelectsExactlyTheRowsSqlSelects(string filter, string[] expected)
    {
        var fake = SixAccounts();
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        var ids = await service.QueryIdsByFilterAsync(Entity, filter, limit: null);

        ids.Select(fake.LabelOf).Should().BeEquivalentTo(expected,
            "conditions sent: {0}", string.Join("; ", fake.ConditionsSent()));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 3)]
    public async Task Limit_AtNAndNPlusOne_ReturnsAtMostOneMoreThanTheLimit(int limit, int expected)
    {
        // Three matching records. With --limit 3 the three come back and the command goes on; with --limit 2 three come
        // back (limit + 1) and the command refuses with LIMIT_EXCEEDED (DeleteCommand / UpdateCommand: Count > limit).
        var fake = new FakeFetchXmlDataverse(Entity);
        for (var i = 0; i < 3; i++)
            fake.Add($"s{i}", ("statecode", 0));
        fake.Add("inactive", ("statecode", 1));
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        var ids = await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit);

        ids.Should().HaveCount(expected);
        (ids.Count > limit).Should().Be(limit < 3);
        ids.Should().NotContain(fake.IdOf("inactive"));
    }

    [Fact]
    public async Task Limit_ExactlyThePageSize_IsNotRefused_OneMore_Is()
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        for (var i = 0; i < 5_001; i++)
            fake.Add($"s{i}", ("statecode", 0));
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        (await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: 5_001)).Should().HaveCount(5_001);
        (await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: 5_000)).Should().HaveCount(5_001);
    }

    [Fact]
    public async Task Limit_ReadingStopsOnceMoreThanTheLimitIsFound()
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        for (var i = 0; i < 12_001; i++)
            fake.Add($"s{i}", ("statecode", 0));
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        var ids = await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: 5_000);

        // 5,000 on the first page is not over the limit; the second page shows there are more, and the command refuses.
        ids.Count.Should().BeGreaterThan(5_000);
        fake.FetchXmls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_UnderOnePage_ReadsOnePage()
    {
        var fake = SixAccounts();
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        var ids = await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: 2);

        ids.Should().HaveCount(3, "one more than the limit, so the command refuses");
        fake.FetchXmls.Should().HaveCount(1);
    }

    [Fact]
    public async Task MoreThanOnePage_SendsTheNextPageAndThePagingCookie()
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        for (var i = 0; i < 5_001; i++)
            fake.Add($"s{i}", ("statecode", 0));
        var service = new DataQueryService(fake.CreatePool(), NullLogger<DataQueryService>.Instance);

        await service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: null);

        fake.FetchXmls.Should().HaveCount(2);
        var second = System.Xml.Linq.XElement.Parse(fake.FetchXmls[1]);
        second.Attribute("page")!.Value.Should().Be("2");
        second.Attribute("paging-cookie")!.Value.Should().Be("<cookie page=\"1\" />");
        fake.ClientsTaken.Should().Be(2, "a pooled client is taken for each page and given back");
    }

    [Fact]
    public async Task APageThatRepeatsARecord_IsRefused()
    {
        var id = Guid.NewGuid();
        var pool = PagedPool(
            Page(true, id),
            Page(false, id));
        var service = new DataQueryService(pool, NullLogger<DataQueryService>.Instance);

        var act = () => service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
    }

    [Fact]
    public async Task AnEmptyPageWithMoreRecords_IsRefused()
    {
        var pool = PagedPool(Page(true, Guid.NewGuid()), Page(true));
        var service = new DataQueryService(pool, NullLogger<DataQueryService>.Instance);

        var act = () => service.QueryIdsByFilterAsync(Entity, "statecode = 0", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
    }

    private static Microsoft.Xrm.Sdk.EntityCollection Page(bool more, params Guid[] ids)
    {
        var page = new Microsoft.Xrm.Sdk.EntityCollection { MoreRecords = more, PagingCookie = more ? "<cookie />" : null };
        foreach (var id in ids)
            page.Entities.Add(new Microsoft.Xrm.Sdk.Entity(Entity, id));
        return page;
    }

    private static PPDS.Dataverse.Pooling.IDataverseConnectionPool PagedPool(params Microsoft.Xrm.Sdk.EntityCollection[] pages)
    {
        var index = 0;
        var client = new Moq.Mock<PPDS.Dataverse.Pooling.IPooledClient>();
        client.Setup(c => c.RetrieveMultipleAsync(Moq.It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => pages[Math.Min(index++, pages.Length - 1)]);
        var pool = new Moq.Mock<PPDS.Dataverse.Pooling.IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(Moq.It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), Moq.It.IsAny<string?>(), Moq.It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return pool.Object;
    }
}
