using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.CsvLoader;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Progress;
using Xunit;
using static PPDS.Cli.Tests.Services.Data.ColumnValueTestSupport;

namespace PPDS.Cli.Tests.CsvLoader;

/// <summary>
/// <c>data load</c> end to end through <see cref="CsvDataLoader.LoadAsync"/> with a recording bulk executor (#1916): the
/// file is read exactly, a cell is converted by its column's type in the invariant culture, and one problem anywhere
/// refuses the whole file before any row is written. Each culture-sensitive test runs under every culture of
/// <see cref="Cultures"/>.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CsvDataLoaderValueTests : IDisposable
{
    private const string Table = "test_widget";
    private const string AccountId = "8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab";
    private const string ContactId = "0d5c3b1a-2e4f-4a6b-8c9d-abcdef012345";

    private readonly string _directory = Directory.CreateTempSubdirectory("ppds-load-values-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AttributeMetadata[] WidgetColumns() =>
    [
        Text("test_name"),
        Decimal("test_rate", 2),
        FloatingPoint("test_ratio", 5),
        Currency("test_amount", 2),
        WholeNumber("test_count"),
        Choice("test_kind", 1, 2),
        Lookup("test_accountid", "account"),
        Lookup("test_customerid", "account", "contact"),
        Date("test_day", DateTimeBehavior.DateOnly, DateTimeFormat.DateOnly),
    ];

    private string Csv(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    /// <summary>The result, or the refusal, and every batch sent to the bulk executor.</summary>
    private sealed record Run(LoadResult? Result, DataValuesRefusedException? Refusal, List<List<Entity>> Upserts)
    {
        public string[] Where => Refusal!.Problems.Select(p => $"{p.Row}:{p.Column}").ToArray();
    }

    private static async Task<Run> LoadAsync(string csvPath, bool dryRun = false, string? key = null,
        CsvMappingConfig? mapping = null, bool force = false)
    {
        var pool = PoolWithColumns(Table, WidgetColumns());
        var upserts = new List<List<Entity>>();
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Strict);
        bulk.Setup(b => b.UpsertMultipleAsync(Table, It.IsAny<IEnumerable<Entity>>(), It.IsAny<BulkOperationOptions?>(),
                It.IsAny<DataverseClientOptions?>(), It.IsAny<IProgress<ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IEnumerable<Entity> entities, BulkOperationOptions? _, DataverseClientOptions? _,
                IProgress<ProgressSnapshot>? _, CancellationToken _) =>
            {
                var batch = entities.ToList();
                upserts.Add(batch);
                return new BulkOperationResult { SuccessCount = batch.Count };
            });

        var loader = new CsvDataLoader(pool.Object, bulk.Object);
        try
        {
            var result = await loader.LoadAsync(csvPath, new CsvLoadOptions
            {
                EntityLogicalName = Table,
                AlternateKeyFields = key,
                DryRun = dryRun,
                Mapping = mapping,
                Force = force,
            });
            return new Run(result, null, upserts);
        }
        catch (DataValuesRefusedException ex)
        {
            return new Run(null, ex, upserts);
        }
    }

    private static void AssertRefused(Run run, params string[] where)
    {
        Assert.Empty(run.Upserts);
        Assert.NotNull(run.Refusal);
        Assert.Equal(ErrorCodes.Validation.InvalidValue, run.Refusal!.ErrorCode);
        Assert.Equal(where, run.Where);
    }

    private static string DescribeRows(IEnumerable<Entity> rows) => string.Join(" | ", rows.Select(e =>
        string.Join(", ", e.Attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={Describe(a.Value)}"))));

    [Fact]
    public async Task A_comma_decimal_cell_refuses_the_whole_file_and_nothing_is_written()
    {
        // The cells of #1916: before, "1,5" was written as 15 and "2,75" as 275, with success.
        var csv = Csv("""
            test_name,test_rate,test_ratio,test_amount
            first,1.5,0.5,10.00
            second,"1,5","1,5","1,5"
            third,"2,75",2.5,3.25
            """);

        var runs = await InEveryCultureAsync(() => LoadAsync(csv));

        Assert.All(runs, run =>
        {
            AssertRefused(run, "2:test_rate", "2:test_ratio", "2:test_amount", "3:test_rate");
            Assert.All(run.Refusal!.Problems, p =>
            {
                Assert.DoesNotContain("1,5", p.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("2,75", p.Message, StringComparison.Ordinal);
            });
            Assert.DoesNotContain("1,5", run.Refusal.UserMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_unquoted_comma_that_shifts_the_fields_refuses_the_file()
    {
        // Review round 1, must-fix 1: "Acme,1,5,3" under a three-column header was written as rate 1 and count 5, with
        // the 3 dropped; a row with fewer fields was written shifted.
        var csv = Csv("""
            test_name,test_rate,test_count
            Acme,1,5,3
            Beta,2
            Gamma,2.5,7
            """);

        var runs = await InEveryCultureAsync(() => LoadAsync(csv));
        Assert.All(runs, run =>
        {
            AssertRefused(run, "1:", "2:");
            Assert.Contains("4 field(s) and the header has 3", run.Refusal!.Problems[0].Message, StringComparison.Ordinal);
            Assert.Contains("2 field(s) and the header has 3", run.Refusal.Problems[1].Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("test_name,test_rate\nfirst,1.5\nsecond,1\"5\n", "2:")]
    [InlineData("test_name,test_rate\nfirst,\"1.5\n", "1:")]
    [InlineData("", ":")]
    [InlineData("test_name,test_rate,TEST_NAME\na,1.5,b\n", ":test_name")]
    [InlineData("test_name,,test_rate\na,b,1.5\n", ":")]
    public async Task A_file_that_is_not_exact_CSV_is_refused_with_a_coded_error(string content, string where)
    {
        var run = await LoadAsync(Csv(content));
        AssertRefused(run, where);
    }

    [Fact]
    public async Task Quoted_commas_line_breaks_and_doubled_quotes_are_one_cell_each()
    {
        var csv = Csv("""
            test_name,test_rate
            "Smith, ""Jr""
            second line",1.5
            "  padded  ",2
            """);

        var run = await LoadAsync(csv);
        Assert.Null(run.Refusal);
        Assert.Equal(
            "test_name=text:Smith, \"Jr\"\nsecond line, test_rate=decimal:1.5 | test_name=text:  padded  , test_rate=decimal:2",
            DescribeRows(Assert.Single(run.Upserts)));
    }

    [Theory]
    [InlineData("1,234.5")]
    [InlineData("1.234,5")]
    [InlineData("1e3")]
    [InlineData(" 1.5")]
    [InlineData("$1.50")]
    public async Task A_number_not_in_the_exact_form_is_refused_for_every_numeric_column(string cell)
    {
        foreach (var column in new[] { "test_rate", "test_ratio", "test_amount" })
        {
            var csv = Csv($"test_name,{column}\nfirst,\"{cell}\"\n");
            var runs = await InEveryCultureAsync(() => LoadAsync(csv));
            Assert.All(runs, run => AssertRefused(run, $"1:{column}"));
        }
    }

    [Fact]
    public async Task Exact_cells_are_written_as_the_same_typed_values_in_every_culture()
    {
        var csv = Csv($"""
            test_name,test_rate,test_ratio,test_amount,test_count,test_kind,test_accountid,test_day
            first,1.5,0.1,"1234.56",-7,2,{AccountId},2024-01-15
            1.5,-2.25,1,0.5,0,1,,
            """);

        var runs = await InEveryCultureAsync(() => LoadAsync(csv));

        Assert.All(runs, run =>
        {
            Assert.Null(run.Refusal);
            Assert.True(run.Result!.Success);
            var rows = Assert.Single(run.Upserts);
            Assert.Equal(
                "test_accountid=ref:account:" + AccountId + ", test_amount=money:1234.56, test_count=int:-7, " +
                "test_day=date:2024-01-15T00:00:00:Unspecified, test_kind=option:2, test_name=text:first, " +
                "test_rate=decimal:1.5, test_ratio=double:0.1" +
                " | test_amount=money:0.5, test_count=int:0, test_kind=option:1, test_name=text:1.5, " +
                "test_rate=decimal:-2.25, test_ratio=double:1",
                DescribeRows(rows));
        });
    }

    [Fact]
    public async Task A_dry_run_reports_the_same_refusal()
    {
        var csv = Csv("test_name,test_rate\nfirst,\"1,5\"\n");
        var runs = await InEveryCultureAsync(() => LoadAsync(csv, dryRun: true));
        Assert.All(runs, run => AssertRefused(run, "1:test_rate"));
    }

    [Fact]
    public async Task A_lookup_that_does_not_resolve_refuses_the_file_instead_of_writing_the_row_without_it()
    {
        // Before, the row was written with the lookup left out, and the failure was only reported.
        var csv = Csv($"test_name,test_accountid\nfirst,{AccountId}\nsecond,Contoso\nthird, {AccountId}\n" +
                      "fourth,+ccccccc-0000-0000-0000-000000000001\nfifth,8b0f6c2e-0x1d-4a5b-9e7f-0123456789ab\n");
        var run = await LoadAsync(csv);

        AssertRefused(run, "2:test_accountid", "3:test_accountid", "4:test_accountid", "5:test_accountid");
        Assert.All(run.Refusal!.Problems, p => Assert.DoesNotContain("Contoso", p.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_lookup_to_several_tables_is_refused_until_the_mapping_names_one_of_them()
    {
        // Review round 1, must-fix 2: auto-mapping gave a customer column its first target (account).
        var csv = Csv($"test_name,test_customerid\nfirst,{ContactId}\n");

        AssertRefused(await LoadAsync(csv), ":test_customerid");

        CsvMappingConfig Mapping(string table) => new()
        {
            Columns = new Dictionary<string, ColumnMappingEntry>(StringComparer.OrdinalIgnoreCase)
            {
                ["test_name"] = new() { Field = "test_name" },
                ["test_customerid"] = new() { Field = "test_customerid", Lookup = new LookupConfig { Entity = table, MatchBy = "guid" } },
            }
        };

        AssertRefused(await LoadAsync(csv, mapping: Mapping("systemuser")), ":test_customerid");
        AssertRefused(await LoadAsync(csv, mapping: Mapping("")), ":test_customerid");

        var named = await LoadAsync(csv, mapping: Mapping("contact"));
        Assert.Null(named.Refusal);
        Assert.Equal($"test_customerid=ref:contact:{ContactId}, test_name=text:first", DescribeRows(Assert.Single(named.Upserts)));
    }

    [Fact]
    public async Task A_header_that_maps_to_no_column_with_force_is_refused_instead_of_creating_empty_records()
    {
        // Another delimiter: the whole header is one unmatched name, and every record would be empty.
        var csv = Csv("test_name;test_rate\nfirst;1.5\n");
        AssertRefused(await LoadAsync(csv, force: true), ":");
    }

    [Fact]
    public async Task A_row_with_no_value_for_any_mapped_column_is_refused()
    {
        var csv = Csv("test_name,test_rate\nfirst,1.5\n,\n");
        AssertRefused(await LoadAsync(csv), "2:");
    }

    [Fact]
    public async Task An_alternate_key_cell_is_converted_by_the_same_rule()
    {
        var csv = Csv("test_name,test_rate\nfirst,\"1,5\"\n");
        var runs = await InEveryCultureAsync(() => LoadAsync(csv, key: "test_rate"));
        Assert.All(runs, run => AssertRefused(run, "1:test_rate"));

        var good = await InEveryCultureAsync(() => LoadAsync(Csv("test_name,test_rate\nfirst,1.5\n"), key: "test_rate"));
        Assert.All(good, run =>
        {
            var entity = Assert.Single(Assert.Single(run.Upserts));
            Assert.Equal("decimal:1.5", Describe(entity.KeyAttributes["test_rate"]));
        });
    }

    [Fact]
    public async Task An_alternate_key_field_that_is_not_a_column_of_the_table_or_of_the_file_is_refused()
    {
        AssertRefused(await LoadAsync(Csv("test_name,test_code\nfirst,42\n"), key: "test_code", force: true), ":test_code");
        AssertRefused(await LoadAsync(Csv("test_name\nfirst\n"), key: "test_rate"), ":");
    }
}
