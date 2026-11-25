using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// Issue 1705: <c>data delete --filter</c> and <c>data update --filter</c> act on the ids
/// <see cref="DataQueryService.QueryIdsByFilterAsync"/> returns. Each filter must select exactly the rows SQL Server
/// would select for the same WHERE, or be refused before any record is read. The rows are answered by
/// <see cref="FakeFetchXmlDataverse"/>, which applies Dataverse's own FetchXML semantics; the column types come from
/// <see cref="FilterTestColumns"/>.
/// </summary>
[Trait("Category", "Unit")]
public class DataFilterExactRowsTests
{
    private const string Entity = "account";
    private static readonly string Omega = ((char)0x3A9).ToString();

    private static DataQueryService Service(FakeFetchXmlDataverse fake, ICachedMetadataProvider? metadata = null)
        => new(fake.CreatePool(), metadata ?? FilterTestColumns.Provider(), NullLogger<DataQueryService>.Instance);

    /// <summary>
    /// Six accounts. r5 is named with the Greek capital omega; r6 is named '?', which is what SQL Server makes of that
    /// letter in a literal without N under a Latin code page. <c>code</c> is text holding numbers.
    /// </summary>
    private static FakeFetchXmlDataverse SixAccounts()
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        fake.Add("r1", ("name", "x"), ("accountnumber", "A1"), ("numberofemployees", 0), ("revenue", 100m),
            ("createdon", new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc)), ("code", "5"), ("price", 1m),
            ("birthdate", new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc)));
        fake.Add("r2", ("name", "abc"), ("accountnumber", "abc"), ("numberofemployees", 0), ("revenue", 1000m),
            ("createdon", new DateTime(2024, 1, 2, 3, 0, 0, DateTimeKind.Utc)), ("code", "05"), ("price", 1.5m),
            ("birthdate", new DateTime(2024, 1, 16, 0, 0, 0, DateTimeKind.Utc)));
        fake.Add("r3", ("name", null), ("accountnumber", "A3"), ("numberofemployees", 1), ("revenue", null),
            ("createdon", null), ("code", null), ("price", null), ("birthdate", null));
        fake.Add("r4", ("name", "a"), ("accountnumber", null), ("numberofemployees", 1), ("revenue", 800m),
            ("createdon", new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc)), ("code", "10"), ("price", 2m),
            ("birthdate", new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
        fake.Add("r5", ("name", Omega), ("accountnumber", "A5"), ("numberofemployees", 0), ("revenue", 5m),
            ("createdon", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)), ("code", "9"), ("price", 3m),
            ("birthdate", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        fake.Add("r6", ("name", "?"), ("accountnumber", "A6"), ("numberofemployees", 1), ("revenue", 6m),
            ("createdon", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)), ("code", "1.2"), ("price", 4m),
            ("birthdate", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        return fake;
    }

    /// <summary>
    /// The filters of the issue's reproduction and of the first review's must-fix (M1 to M4). Each is refused before any
    /// record is read: what SQL Server would do is in the comment, what the old path sent and selected is in the pull
    /// request.
    /// </summary>
    public static TheoryData<string, string> RefusedFilters => new()
    {
        // SQL: r1. A function is not sent to Dataverse; the old path dropped it and selected r1, r2, r5.
        { "numberofemployees = 0 AND UPPER(name) = 'X'", "function" },
        // SQL: r1, r2. The old path dropped LEN(...) and selected r1 only.
        { "name = 'x' OR LEN(name) = 3", "function" },
        // SQL: r2, r4. The old path sent no condition: every row.
        { "revenue * 2 > 1500", "arithmetic" },
        // SQL: r2. The old path sent no condition: every row.
        { "name = accountnumber", "column compared with a column" },
        // SQL: r3, r4, r6. The old path sent numberofemployees = 0: exactly the opposite rows.
        { "NOT (numberofemployees = 0)", "NOT" },
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
        // SQL: r1, r2, r5. The old path dropped the NOT and sent numberofemployees ne 0: exactly the opposite rows.
        { "NOT (numberofemployees <> 0)", "NOT" },
        // SQL (no time zone): r1, r4. Dataverse reads the date in the user's time zone: r1, r2, r4.
        { "createdon < '2024-01-02'", "not a date written as yyyy-MM-ddTHH:mm:ssZ" },
        // SQL (Latin code page): r6, since the letter becomes '?'. The old path sent the letter: r5.
        { "name = '" + Omega + "'", "text without N" },

        // M1: a number against a text column. SQL Server converts the column to a number (r1, r2 for = 5, and an error
        // on r6's '1.2' for a whole number); Dataverse compares text.
        { "code <> 5", "a number compared with a text column" },
        { "code > 10", "a number compared with a text column" },
        { "code < 9", "a number compared with a text column" },
        { "code NOT IN (5, '05')", "a number compared with a text column" },
        { "name = 5", "a number compared with a text column" },
        // M2: an offset other than Z. SQL Server refuses it for datetime (error 241); Dataverse reads the instant.
        { "createdon <> '2024-01-15T04:30:00-05:00'", "not a date written as yyyy-MM-ddTHH:mm:ssZ" },
        { "createdon = '2024-01-15T09:30:00.000Z'", "not a date written as yyyy-MM-ddTHH:mm:ssZ" },
        // M3: more than 28 digits: .NET rounds, SQL Server does not (or raises error 1007).
        { "price = 1.00000000000000000000000000001", "a number of more than 28 digits" },
        { "revenue = 0.000000000000000000000000000000000000001", "a number of more than 28 digits" },
        { "revenue = 4.99995", "more decimal places than the currency column keeps" },
        // M4: a number, or a date and time, against a date column.
        { "createdon < 5.5", "not a date written as yyyy-MM-ddTHH:mm:ssZ" },
        { "birthdate = '2024-01-15T09:30:00Z'", "not a date written as yyyy-MM-dd" },
        { "birthdate = 20240115", "not a date written as yyyy-MM-dd" },

        // Column kinds not yet supported (should-fix 2).
        { "statecode = 0", "a column of a kind not yet supported in a filter (choice)" },
        { "statecode = 5.0", "a column of a kind not yet supported in a filter (choice)" },
        { "donotemail <> 2", "not yet supported in a filter (yes/no)" },
        { "versionnumber > 5", "not yet supported in a filter (big whole number)" },
        { "ownerid = '00000000-0000-0000-0000-000000000001'", "not yet supported in a filter (lookup to more than one table)" },
        { "lastseen < '2024-01-01T00:00:00Z'", "not yet supported in a filter (date with this behaviour or format)" },
        { "shownasdate = '2024-01-01'", "not yet supported in a filter (date with this behaviour or format)" },
        { "primarycontactidname = N'x'", "not yet supported in a filter (computed from another column)" },
        { "processid IS NULL", "not yet supported in a filter (unique identifier other than the row id)" },
        { "numberofemployees = 2147483648", "not a whole number from -2147483648 to 2147483647" },
        { "numberofemployees = 5.0", "not a whole number" },
        { "numberofemployees = '5'", "not a whole number" },
        { "numberofemployees IN (0, '1', 2)", "not a whole number" },
        { "numberofemployees LIKE '5%'", "LIKE on a whole number column" },
        { "name > 'abc'", "a range comparison (<, <=, >, >=, BETWEEN) on a text column" },
        { "ratio IN (1, 2)", "IN on a floating-point column" },
        { "createdon IN ('2024-01-15T09:30:00Z')", "IN on a date column" },
        { "accountid > '00000000-0000-0000-0000-000000000001'", "a range comparison on a row id column" },
        { "accountid = '{00000000-0000-0000-0000-000000000001}'", "not a GUID written as 8-4-4-4-12" },
        { "primarycontactid = 5", "not a GUID written as 8-4-4-4-12" },
        // Spellings .NET's GUID parser reads as a different id (#1915): the character rule refuses them.
        { "accountid = '+0000000-0000-0000-0000-000000000001'", "not a GUID written as 8-4-4-4-12" },
        { "accountid = '0x000000-0000-0000-0000-000000000001'", "not a GUID written as 8-4-4-4-12" },
        { "primarycontactid = '00000000-+000-0000-0000-000000000001'", "not a GUID written as 8-4-4-4-12" },
        { "primarycontactid IN ('00000000-0000-0000-0000-0x0000000001')", "not a GUID written as 8-4-4-4-12" },
        { "accountid = ' 0000000-0000-0000-0000-000000000001'", "white space" },

        // A column the table does not have.
        { "nosuchcolumn = 1", "a column the table does not have" },
        { "name = N'x' OR nosuchcolumn IS NULL", "a column the table does not have" },

        // Second killing tests for single-killed rules (review's mutation table): qualified column, [ ], ESCAPE.
        { "[account].[name] = N'x'", "a column with a table or alias prefix" },
        { "name LIKE '%[0-9]'", "a LIKE pattern with [ or ]" },
        { "name LIKE 'a#%' ESCAPE '#'", "LIKE with ESCAPE" },
    };

    [Theory]
    [MemberData(nameof(RefusedFilters))]
    public async Task Filter_ThatCannotBeExpressedExactly_IsRefusedBeforeAnyRecordIsRead(string filter, string kind)
    {
        var fake = SixAccounts();
        var service = Service(fake);

        var act = async () => await service.QueryIdsByFilterAsync(Entity, filter, limit: null);

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Validation.FilterNotSupported);
        ex.Which.UserMessage.Should().Contain(kind).And.Contain("at line 1, column ");
        fake.AnythingRead.Should().BeFalse("a refused filter reads no record");
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
        { "(numberofemployees = 0 OR revenue > 700) AND name IS NOT NULL", new[] { "r1", "r2", "r4", "r5" } },
        { "numberofemployees = 1 OR name <> 'abc'", new[] { "r1", "r3", "r4", "r5", "r6" } },
        { "createdon < '2024-01-02T00:00:00Z'", new[] { "r1", "r4" } },
        { "createdon >= '2024-01-02T03:00:00Z'", new[] { "r2", "r5", "r6" } },
        { "name = N'" + ((char)0x3A9) + "'", new[] { "r5" } },
        { "name LIKE '_b%'", new[] { "r2" } },
        { "numberofemployees IN (1) AND revenue >= 6", new[] { "r4", "r6" } },
        // Text against a text column is text (should-fix 1): '1.2' and '05' are not dates or numbers here.
        { "code = '1.2'", new[] { "r6" } },
        { "code = '05'", new[] { "r2" } },
        { "code IN ('5', '9')", new[] { "r1", "r5" } },
        // Decimal and currency within their decimal places.
        { "price = 1.5", new[] { "r2" } },
        { "price > 1 AND price <= 3", new[] { "r2", "r4", "r5" } },
        { "revenue = 1000.0000", new[] { "r2" } },
        { "revenue < -1 OR revenue = 5", new[] { "r5" } },
        // Date only.
        { "birthdate = '2024-01-15'", new[] { "r1" } },
        { "birthdate BETWEEN '2024-01-01' AND '2024-12-31'", new[] { "r1", "r2" } },
    };

    [Theory]
    [MemberData(nameof(AdmittedFilters))]
    public async Task AdmittedFilter_SelectsExactlyTheRowsSqlSelects(string filter, string[] expected)
    {
        var fake = SixAccounts();
        var service = Service(fake);

        var ids = await service.QueryIdsByFilterAsync(Entity, filter, limit: null);

        ids.Select(fake.LabelOf).Should().BeEquivalentTo(expected,
            "conditions sent: {0}", string.Join("; ", fake.ConditionsSent()));
    }

    [Fact]
    public async Task NotEqual_DoesNotSelectRowsWhereTheColumnIsNull()
    {
        var fake = SixAccounts();

        var ids = await Service(fake).QueryIdsByFilterAsync(Entity, "name <> 'x'", limit: null);

        // SQL: name <> 'x' is unknown for r3 (name NULL), so r3 is not selected.
        ids.Select(fake.LabelOf).Should().BeEquivalentTo(new[] { "r2", "r4", "r5", "r6" },
            "FetchXML ne also matches NULL; conditions sent: {0}", string.Join("; ", fake.ConditionsSent()));
    }

    [Fact]
    public async Task TheIdColumn_ComesFromTheMetadata()
    {
        // An activity table's primary id is activityid, not <table>id.
        var fake = new FakeFetchXmlDataverse("email");
        fake.Add("e1", ("subject", "x"));
        var columns = new[]
        {
            new AttributeMetadataDto { LogicalName = "activityid", DisplayName = "a", SchemaName = "a", AttributeType = "Uniqueidentifier", IsPrimaryId = true },
            new AttributeMetadataDto { LogicalName = "subject", DisplayName = "s", SchemaName = "s", AttributeType = "String" },
        };

        await Service(fake, FilterTestColumns.Provider(columns)).QueryIdsByFilterAsync("email", "subject = 'x'", limit: null);

        System.Xml.Linq.XElement.Parse(fake.FetchXmls[0]).Element("entity")!.Element("attribute")!.Attribute("name")!.Value
            .Should().Be("activityid");
    }

    // --- Metadata -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task MetadataThatCannotBeRead_IsARefusal_NotAFilterWithoutTypes()
    {
        var fake = SixAccounts();
        var metadata = new Mock<ICachedMetadataProvider>();
        metadata.Setup(m => m.GetAttributesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => Service(fake, metadata.Object).QueryIdsByFilterAsync(Entity, "name = 'x'", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Query.FilterMetadataUnavailable);
        fake.AnythingRead.Should().BeFalse();
    }

    [Fact]
    public async Task MetadataForATableThatDoesNotExist_IsARefusal_AndKeepsTheCause()
    {
        var fake = new FakeFetchXmlDataverse("nosuchtable");
        var cause = new TimeoutException("metadata timed out");
        var metadata = new Mock<ICachedMetadataProvider>();
        metadata.Setup(m => m.GetAttributesAsync("nosuchtable", It.IsAny<CancellationToken>())).ThrowsAsync(cause);

        var act = () => Service(fake, metadata.Object).QueryIdsByFilterAsync("nosuchtable", "name = 'x'", limit: null);

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Query.FilterMetadataUnavailable);
        ex.InnerException.Should().BeSameAs(cause);
        fake.AnythingRead.Should().BeFalse();
    }

    [Fact]
    public async Task MetadataWithNoColumns_IsARefusal()
    {
        var fake = SixAccounts();

        var act = () => Service(fake, FilterTestColumns.Provider(Array.Empty<AttributeMetadataDto>()))
            .QueryIdsByFilterAsync(Entity, "name = 'x'", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Query.FilterMetadataUnavailable);
        fake.AnythingRead.Should().BeFalse();
    }

    [Fact]
    public async Task MetadataWithoutAPrimaryId_IsARefusal()
    {
        var fake = SixAccounts();
        var columns = FilterTestColumns.Account().Where(c => !c.IsPrimaryId).ToList();

        var act = () => Service(fake, FilterTestColumns.Provider(columns)).QueryIdsByFilterAsync(Entity, "name = 'x'", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Query.FilterMetadataUnavailable);
        fake.AnythingRead.Should().BeFalse();
    }

    [Fact]
    public async Task AStructuralRefusal_ReadsNotEvenTheMetadata()
    {
        var fake = SixAccounts();
        var metadata = new Mock<ICachedMetadataProvider>();

        var act = () => Service(fake, metadata.Object).QueryIdsByFilterAsync(Entity, "NOT (name = 'x')", limit: null);

        await act.Should().ThrowAsync<PpdsException>();
        metadata.Verify(m => m.GetAttributesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // --- Paging and --limit --------------------------------------------------------------------------------------

    private static FakeFetchXmlDataverse Matching(int count, bool withOneOther = true)
    {
        var fake = new FakeFetchXmlDataverse(Entity);
        for (var i = 0; i < count; i++)
            fake.Add($"s{i}", ("numberofemployees", 0));
        if (withOneOther)
            fake.Add("other", ("numberofemployees", 1));
        return fake;
    }

    [Fact]
    public async Task MoreThanOnePage_EveryMatchingRecordIsSelected()
    {
        var fake = Matching(12_001);

        var ids = await Service(fake).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        ids.Should().HaveCount(12_001, "every matching record, not only the first page of 5,000");
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().NotContain(fake.IdOf("other"));
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(2, 3)]
    public async Task Limit_AtNAndNPlusOne_ReturnsMoreThanTheLimitOnlyWhenMoreMatch(int limit, int expected)
    {
        // Three matching records. With --limit 3 the three come back and the command goes on; with --limit 2 three come
        // back and the command refuses with LIMIT_EXCEEDED (DeleteCommand / UpdateCommand: Count > limit).
        var fake = Matching(3);

        var ids = await Service(fake).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit);

        ids.Should().HaveCount(expected);
        (ids.Count > limit).Should().Be(limit < 3);
        ids.Should().NotContain(fake.IdOf("other"));
    }

    [Fact]
    public async Task Limit_ExactlyThePageSize_IsNotRefused_OneMore_Is()
    {
        var fake = Matching(5_001, withOneOther: false);
        var service = Service(fake);

        (await service.QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: 5_001)).Should().HaveCount(5_001);
        (await service.QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: 5_000)).Count.Should().BeGreaterThan(5_000);
    }

    [Fact]
    public async Task Limit_TwoPagesExactly_IsNotRefused_OneMore_Is()
    {
        var fake = Matching(10_001, withOneOther: false);
        var service = Service(fake);

        (await service.QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: 10_001)).Should().HaveCount(10_001);
        (await service.QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: 10_000)).Count.Should().BeGreaterThan(10_000);
    }

    [Fact]
    public async Task Limit_ReadingStopsAtTheEndOfThePageOnWhichTheLimitIsPassed()
    {
        var fake = Matching(12_001, withOneOther: false);

        var ids = await Service(fake).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: 5_000);

        // The first page (5,000) is not over the limit; the second page is read whole and shows there are more. The
        // third page is not read, so the list is a lower bound: the command says "at least".
        ids.Should().HaveCount(10_000);
        fake.FetchXmls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Limit_UnderOnePage_ReadsOnePage()
    {
        var fake = SixAccounts();

        var ids = await Service(fake).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: 2);

        ids.Should().HaveCount(3, "one more than the limit, so the command refuses");
        fake.FetchXmls.Should().HaveCount(1);
    }

    [Fact]
    public async Task MoreThanOnePage_SendsTheNextPageAndThePagingCookie()
    {
        var fake = Matching(5_001, withOneOther: false);

        await Service(fake).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        fake.FetchXmls.Should().HaveCount(2);
        var second = System.Xml.Linq.XElement.Parse(fake.FetchXmls[1]);
        second.Attribute("page")!.Value.Should().Be("2");
        second.Attribute("paging-cookie")!.Value.Should().Be("<cookie page=\"1\" />");
        fake.ClientsTaken.Should().Be(2, "a pooled client is taken for each page and given back");
    }

    [Fact]
    public async Task ThreePages_EachPageSendsTheCookieOfThePageBefore()
    {
        var fake = Matching(10_001, withOneOther: false);

        await Service(fake).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        fake.FetchXmls.Should().HaveCount(3);
        System.Xml.Linq.XElement.Parse(fake.FetchXmls[0]).Attribute("paging-cookie").Should().BeNull();
        System.Xml.Linq.XElement.Parse(fake.FetchXmls[2]).Attribute("paging-cookie")!.Value.Should().Be("<cookie page=\"2\" />");
    }

    [Fact]
    public async Task APageThatRepeatsARecord_IsRefused()
    {
        var id = Guid.NewGuid();
        var (pool, _) = PagedPool(Page(true, id), Page(false, id));

        var act = () => PagedService(pool).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.UserMessage.Should().Contain("came back twice");
    }

    [Fact]
    public async Task ARecordRepeatedWithinOnePage_IsRefused()
    {
        var id = Guid.NewGuid();
        var (pool, _) = PagedPool(Page(false, Guid.NewGuid(), id, id));

        var act = () => PagedService(pool).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.UserMessage.Should().Contain("came back twice");
    }

    [Fact]
    public async Task AnEmptyPageWithMoreRecords_IsRefused_WithoutReadingFurther()
    {
        // The fake answers at most five pages, then fails the test's way: without the check the service would page on.
        var (pool, calls) = PagedPool(Page(true, Guid.NewGuid()), Page(true), Page(true), Page(true), Page(true));

        var act = () => PagedService(pool).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
        ex.UserMessage.Should().Contain("returned an empty page");
        calls().Should().Be(2);
    }

    [Fact]
    public async Task AnEmptyFirstPageWithMoreRecords_IsRefused()
    {
        var (pool, calls) = PagedPool(Page(true), Page(true), Page(true));

        var act = () => PagedService(pool).QueryIdsByFilterAsync(Entity, "numberofemployees = 0", limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.UserMessage.Should().Contain("returned an empty page");
        calls().Should().Be(1);
    }

    [Fact]
    public async Task AnInListOverTheCap_IsRefusedBeforeAnyRecordIsRead()
    {
        var fake = SixAccounts();
        var filter = "numberofemployees IN (" + string.Join(", ", Enumerable.Range(0, DataFilter.MaxInListValues + 1)) + ")";

        var act = () => Service(fake).QueryIdsByFilterAsync(Entity, filter, limit: null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.FilterNotSupported);
        fake.AnythingRead.Should().BeFalse();
    }

    private static DataQueryService PagedService(PPDS.Dataverse.Pooling.IDataverseConnectionPool pool)
        => new(pool, FilterTestColumns.Provider(), NullLogger<DataQueryService>.Instance);

    private static Microsoft.Xrm.Sdk.EntityCollection Page(bool more, params Guid[] ids)
    {
        var page = new Microsoft.Xrm.Sdk.EntityCollection { MoreRecords = more, PagingCookie = more ? "<cookie />" : null };
        foreach (var id in ids)
            page.Entities.Add(new Microsoft.Xrm.Sdk.Entity(Entity, id));
        return page;
    }

    /// <summary>A pool whose client answers the given pages in order and fails once they are used up (no endless paging).</summary>
    private static (PPDS.Dataverse.Pooling.IDataverseConnectionPool Pool, Func<int> Calls) PagedPool(
        params Microsoft.Xrm.Sdk.EntityCollection[] pages)
    {
        var index = 0;
        var client = new Mock<PPDS.Dataverse.Pooling.IPooledClient>();
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => index < pages.Length
                ? pages[index++]
                : throw new InvalidOperationException("The fake has no more pages."));
        var pool = new Mock<PPDS.Dataverse.Pooling.IDataverseConnectionPool>();
        pool.Setup(p => p.GetClientAsync(It.IsAny<PPDS.Dataverse.Client.DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        return (pool.Object, () => index);
    }
}
