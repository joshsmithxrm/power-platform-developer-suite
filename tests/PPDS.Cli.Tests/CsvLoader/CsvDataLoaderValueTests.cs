using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Cli.CsvLoader;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Progress;
using Xunit;
using static PPDS.Cli.Tests.Services.Data.ColumnValueTestSupport;

namespace PPDS.Cli.Tests.CsvLoader;

/// <summary>
/// <c>data load</c> end to end through <see cref="CsvDataLoader.LoadAsync"/> with a recording bulk executor (#1916): a
/// cell is converted by its column's type in the invariant culture, and one cell that is not exact for its column
/// refuses the whole file before any row is written. Each test runs under the invariant culture, de-DE and fr-FR.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CsvDataLoaderValueTests : IDisposable
{
    private const string Table = "test_widget";
    private const string AccountId = "8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab";

    private readonly string _directory = Directory.CreateTempSubdirectory("ppds-load-values-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Microsoft.Xrm.Sdk.Metadata.AttributeMetadata[] WidgetColumns() =>
    [
        Text("test_name"),
        Decimal("test_rate", 2),
        FloatingPoint("test_ratio", 5),
        Currency("test_amount", 2),
        WholeNumber("test_count"),
        Choice("test_kind", 1, 2),
        Lookup("test_accountid", "account"),
        Date("test_day", Microsoft.Xrm.Sdk.Metadata.DateTimeBehavior.DateOnly, Microsoft.Xrm.Sdk.Metadata.DateTimeFormat.DateOnly),
    ];

    private string Csv(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    private sealed record Run(LoadResult Result, List<List<Entity>> Upserts);

    private static async Task<Run> LoadAsync(string csvPath, bool dryRun = false, string? key = null)
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
        var result = await loader.LoadAsync(csvPath, new CsvLoadOptions
        {
            EntityLogicalName = Table,
            AlternateKeyFields = key,
            DryRun = dryRun,
        });
        return new Run(result, upserts);
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
            Assert.Empty(run.Upserts);
            Assert.True(run.Result.Refused);
            Assert.False(run.Result.Success);
            Assert.Equal(0, run.Result.SuccessCount);
            Assert.Equal(2, run.Result.FailureCount);
            Assert.Equal(
                ["2:test_rate", "2:test_ratio", "2:test_amount", "3:test_rate"],
                run.Result.Errors.Select(e => $"{e.RowNumber}:{e.Column}").ToArray());
            Assert.All(run.Result.Errors, e =>
            {
                Assert.Equal(LoadErrorCodes.TypeCoercionFailed, e.ErrorCode);
                Assert.DoesNotContain("1,5", e.Message, StringComparison.Ordinal);
                Assert.DoesNotContain("2,75", e.Message, StringComparison.Ordinal);
            });
        });
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
            Assert.All(runs, run =>
            {
                Assert.Empty(run.Upserts);
                Assert.True(run.Result.Refused);
                Assert.Equal($"1:{column}", Assert.Single(run.Result.Errors).RowNumber + ":" + run.Result.Errors[0].Column);
            });
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
            Assert.False(run.Result.Refused);
            Assert.True(run.Result.Success);
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
        Assert.All(runs, run =>
        {
            Assert.Empty(run.Upserts);
            Assert.True(run.Result.Refused);
            Assert.Equal("1:test_rate", $"{Assert.Single(run.Result.Errors).RowNumber}:{run.Result.Errors[0].Column}");
        });
    }

    [Fact]
    public async Task A_lookup_that_does_not_resolve_refuses_the_file_instead_of_writing_the_row_without_it()
    {
        // Before, the row was written with the lookup left out, and the failure was only reported.
        var csv = Csv($"test_name,test_accountid\nfirst,{AccountId}\nsecond,Contoso\n");
        var run = await LoadAsync(csv);

        Assert.Empty(run.Upserts);
        Assert.True(run.Result.Refused);
        var error = Assert.Single(run.Result.Errors);
        Assert.Equal((2, "test_accountid", LoadErrorCodes.LookupNotResolved), (error.RowNumber, error.Column, error.ErrorCode));
        Assert.Equal(1, run.Result.FailureCount);
        Assert.Equal(1, run.Result.SkippedCount); // the valid first row, not written either
    }

    [Fact]
    public async Task An_alternate_key_cell_is_converted_by_the_same_rule()
    {
        var csv = Csv("test_name,test_rate\nfirst,\"1,5\"\n");
        var runs = await InEveryCultureAsync(() => LoadAsync(csv, key: "test_rate"));
        Assert.All(runs, run =>
        {
            Assert.Empty(run.Upserts);
            Assert.True(run.Result.Refused);
            Assert.Equal("1:test_rate", $"{Assert.Single(run.Result.Errors).RowNumber}:{run.Result.Errors[0].Column}");
        });

        var good = await InEveryCultureAsync(() => LoadAsync(Csv("test_name,test_rate\nfirst,1.5\n"), key: "test_rate"));
        Assert.All(good, run =>
        {
            var entity = Assert.Single(Assert.Single(run.Upserts));
            Assert.Equal("decimal:1.5", Describe(entity.KeyAttributes["test_rate"]));
        });
    }

    [Fact]
    public async Task An_alternate_key_field_that_is_not_a_column_is_refused_instead_of_sent_as_text()
    {
        var csv = Csv("test_name,test_code\nfirst,42\n");
        var pool = PoolWithColumns(Table, WidgetColumns());
        var bulk = new Mock<IBulkOperationExecutor>(MockBehavior.Strict);
        var loader = new CsvDataLoader(pool.Object, bulk.Object);

        var result = await loader.LoadAsync(csv, new CsvLoadOptions
        {
            EntityLogicalName = Table,
            AlternateKeyFields = "test_code",
            Force = true,
        });

        Assert.True(result.Refused);
        var error = Assert.Single(result.Errors);
        Assert.Equal((1, "test_code", LoadErrorCodes.ColumnNotFound), (error.RowNumber, error.Column, error.ErrorCode));
    }
}
