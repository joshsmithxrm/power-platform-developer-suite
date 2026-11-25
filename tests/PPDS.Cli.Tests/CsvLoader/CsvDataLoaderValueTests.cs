using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using System.Text;
using System.Text.Json;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.CsvLoader;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Progress;
using PPDS.Cli.Tests.Services.Data;
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
        Date("test_when", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime),
        Text("test_code"),
        Lookup("test_parentid", "test_parent"),
        new DateTimeAttributeMetadata { LogicalName = "createdon", IsValidForCreate = false, IsValidForUpdate = false },
        new UniqueIdentifierAttributeMetadata { LogicalName = "test_widgetid", IsValidForCreate = true, IsValidForUpdate = false },
        new IntegerAttributeMetadata { LogicalName = "test_stage", IsValidForCreate = false, IsValidForUpdate = true },
        Text("test_first_name"),
        Text("test_firstname"),
    ];

    /// <summary>The lookup table of <c>test_parentid</c>: a text key, a date key and a whole-number key, and records.</summary>
    private static TestTable Parents(params (string Id, string Code)[] records) =>
        new("test_parent", "test_parentid",
        [
            Text("test_code"),
            new MemoAttributeMetadata { LogicalName = "test_notes" },
            Date("test_since", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime),
            WholeNumber("test_number"),
        ])
        {
            Records = records.Select(r => new Entity("test_parent", Guid.Parse(r.Id)) { ["test_code"] = r.Code }).ToList()
        };

    /// <summary>A mapping file read as the command reads it: JSON text through the command's own serializer options.</summary>
    internal static CsvMappingConfig MappingJson(string json) =>
        JsonSerializer.Deserialize<CsvMappingConfig>(json, LoadCommand.JsonOptions)!;

    private string Csv(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    private string CsvBytes(byte[] content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>The result, or the refusal, and every batch sent to the bulk executor.</summary>
    internal sealed record Run(LoadResult? Result, DataValuesRefusedException? Refusal, List<List<Entity>> Upserts)
    {
        public string[] Where => Refusal!.Problems.Select(p => $"{p.Row}:{p.Column}").ToArray();
    }

    internal static async Task<Run> LoadAsync(string csvPath, bool dryRun = false, string? key = null,
        CsvMappingConfig? mapping = null, bool force = false, TestTable? parents = null)
    {
        var pool = PoolWithTables(new TestTable(Table, Table + "id", WidgetColumns()), parents ?? Parents());
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

    internal static void AssertRefused(Run run, params string[] where)
    {
        Assert.Empty(run.Upserts);
        Assert.NotNull(run.Refusal);
        Assert.Equal(ErrorCodes.Validation.InvalidValue, run.Refusal!.ErrorCode);
        Assert.Equal(where, run.Where);
    }

    internal static string DescribeRows(IEnumerable<Entity> rows) => string.Join(" | ", rows.Select(e =>
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
        AssertRefused(await LoadAsync(Csv("test_name,test_other\nfirst,42\n"), key: "test_other", force: true), ":test_other");
        AssertRefused(await LoadAsync(Csv("test_name\nfirst\n"), key: "test_rate"), ":");
    }

    // ---- Review round 2 ----

    [Fact]
    public async Task A_file_that_is_not_valid_UTF8_is_refused_and_a_valid_non_ASCII_cell_is_sent_exactly()
    {
        // Must-fix 1: Excel's "CSV (Comma delimited)" writes Windows-1252; "Müller" was sent as "M\uFFFDller", with success.
        var header = Encoding.ASCII.GetBytes("test_name,test_rate\n");
        byte[] Bytes(params byte[][] parts) => parts.SelectMany(b => b).ToArray();

        var windows1252 = CsvBytes(Bytes(header, [0x4D, 0xFC, 0x6C, 0x6C, 0x65, 0x72], Encoding.ASCII.GetBytes(",1.5\n")));
        var cutOff = CsvBytes(Bytes(header, Encoding.ASCII.GetBytes("first,1.5\nM"), [0xC3]));
        foreach (var path in new[] { windows1252, cutOff })
        {
            var run = await LoadAsync(path);
            AssertRefused(run, ":");
            Assert.Contains("not valid UTF-8", run.Refusal!.Problems[0].Message, StringComparison.Ordinal);
            Assert.Null(run.Refusal.InnerException);
        }

        foreach (var bom in new[] { Array.Empty<byte>(), new byte[] { 0xEF, 0xBB, 0xBF } })
        {
            var utf8 = await LoadAsync(CsvBytes(Bytes(bom, header, Encoding.UTF8.GetBytes("Müller Œ 東京,1.5\n"))));
            Assert.Null(utf8.Refusal);
            Assert.Equal("test_name=text:Müller Œ 東京, test_rate=decimal:1.5", DescribeRows(Assert.Single(utf8.Upserts)));
        }
    }

    [Fact]
    public async Task A_quote_refusal_keeps_no_library_exception_that_holds_the_row()
    {
        // Should-fix 16: the library's exception repeats the row's text.
        var run = await LoadAsync(Csv("test_name,test_rate\nfirst,1\"5\n"));
        AssertRefused(run, "1:");
        Assert.Null(run.Refusal!.InnerException);
    }

    [Fact]
    public async Task A_date_only_cell_is_read_in_the_Gregorian_calendar_under_a_Persian_calendar_culture()
    {
        // Mutant m17: read in the current culture, 2024-01-15 was sent as 2645-04-04 under fa-IR.
        Assert.IsType<System.Globalization.PersianCalendar>(System.Globalization.CultureInfo.GetCultureInfo("fa-IR").Calendar);
        var runs = await InEveryCultureAsync(() => LoadAsync(Csv("test_name,test_day\nfirst,2024-01-15\n")));
        Assert.All(runs, run => Assert.Equal(
            "test_day=date:2024-01-15T00:00:00:Unspecified, test_name=text:first", DescribeRows(Assert.Single(run.Upserts))));
    }

    [Fact]
    public async Task A_mapping_key_that_differs_from_the_header_only_in_case_still_maps_the_column()
    {
        // Must-fix 3: the deserialised mapping was case-sensitive per row, so Test_Name was dropped with success.
        var csv = Csv("Test_Name,test_rate\nfirst,1.5\n");
        var mapping = MappingJson("""{ "columns": { "test_name": { "field": "test_name" }, "test_rate": { "field": "test_rate" } } }""");

        var run = await LoadAsync(csv, mapping: mapping);
        Assert.Null(run.Refusal);
        Assert.Equal("test_name=text:first, test_rate=decimal:1.5", DescribeRows(Assert.Single(run.Upserts)));
    }

    [Fact]
    public async Task Two_mapping_keys_equal_ignoring_case_are_refused()
    {
        var csv = Csv("test_name,test_rate\nfirst,1.5\n");
        var mapping = MappingJson("""
            { "columns": { "test_name": { "field": "test_name" }, "TEST_NAME": { "field": "test_code" }, "test_rate": { "field": "test_rate" } } }
            """);
        AssertRefused(await LoadAsync(csv, mapping: mapping), ":TEST_NAME");
    }

    [Fact]
    public async Task Two_headers_mapped_to_one_column_are_refused()
    {
        // Mutant m61: the last header won.
        var csv = Csv("test_name,other_name\nfirst,second\n");
        var mapping = MappingJson("""{ "columns": { "test_name": { "field": "test_name" }, "other_name": { "field": "test_name" } } }""");
        AssertRefused(await LoadAsync(csv, mapping: mapping), ":other_name");
    }

    [Fact]
    public async Task A_column_the_mapping_skips_is_not_written_even_with_a_field()
    {
        // Mutant x8.
        var csv = Csv("test_name,test_rate\nfirst,1.5\n");
        var mapping = MappingJson("""{ "columns": { "test_name": { "field": "test_name" }, "test_rate": { "field": "test_rate", "skip": true } } }""");
        var run = await LoadAsync(csv, mapping: mapping);
        Assert.Equal("test_name=text:first", DescribeRows(Assert.Single(run.Upserts)));
    }

    [Fact]
    public async Task An_option_label_that_is_a_number_is_read_as_the_label()
    {
        // Mutant m80: the label "1" maps to option 2, and is checked before the integer.
        var csv = Csv("test_name,test_kind\nfirst,1\nsecond,2\n");
        var mapping = MappingJson("""{ "columns": { "test_name": { "field": "test_name" }, "test_kind": { "field": "test_kind", "optionsetMap": { "1": 2 } } } }""");
        var run = await LoadAsync(csv, mapping: mapping);
        Assert.Equal("test_kind=option:2, test_name=text:first | test_kind=option:2, test_name=text:second",
            DescribeRows(Assert.Single(run.Upserts)));
    }

    [Fact]
    public async Task A_good_dry_run_writes_nothing()
    {
        // Mutant x4.
        var run = await LoadAsync(Csv("test_name,test_rate\nfirst,1.5\n"), dryRun: true);
        Assert.Null(run.Refusal);
        Assert.True(run.Result!.Success);
        Assert.Equal(1, run.Result.TotalRows);
        Assert.Empty(run.Upserts);
    }

    [Theory]
    [InlineData("createdon", "2024-01-15T00:00:00Z")]  // valid for neither create nor update
    [InlineData("test_widgetid", "8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab")] // valid for create only
    [InlineData("test_stage", "3")]                    // valid for update only
    public async Task A_column_that_takes_no_value_on_create_or_on_update_is_refused(string column, string cell)
    {
        // Must-fix 6: the value was sent, with success; data update refuses the same column.
        var run = await LoadAsync(Csv($"test_name,{column}\nfirst,{cell}\n"));
        AssertRefused(run, $":{column}");
    }

    [Fact]
    public async Task An_empty_alternate_key_cell_refuses_the_file()
    {
        // Must-fix 5: the row was sent with no key, or with part of the key.
        AssertRefused(await LoadAsync(Csv("test_code,test_name\n,first\n"), key: "test_code"), "1:test_code");
        AssertRefused(await LoadAsync(Csv("test_code,test_count,test_name\nK1,,first\n"), key: "test_code,test_count"), "1:test_count");

        var whole = await LoadAsync(Csv("test_code,test_count,test_name\nK1,4,first\n"), key: "test_code,test_count");
        var entity = Assert.Single(Assert.Single(whole.Upserts));
        Assert.Equal("test_code=text:K1, test_count=int:4",
            string.Join(", ", entity.KeyAttributes.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={Describe(k.Value)}")));
        Assert.Equal("test_name=text:first", DescribeRows([entity]));
    }

    [Fact]
    public async Task A_lookup_typed_alternate_key_is_sent_as_the_key()
    {
        // Mutant x11: it was sent as a column, not as the key.
        var parent = "1a2b3c4d-0000-4000-8000-000000000001";
        var run = await LoadAsync(Csv($"test_parentid,test_name\n{parent},first\n"), key: "test_parentid");
        var entity = Assert.Single(Assert.Single(run.Upserts));
        Assert.Equal($"ref:test_parent:{parent}", Describe(entity.KeyAttributes["test_parentid"]));
        Assert.False(entity.Contains("test_parentid"));
    }

    private static CsvMappingConfig ParentByField(string keyField) => MappingJson($$"""
        { "columns": { "test_name": { "field": "test_name" },
                       "test_parentid": { "field": "test_parentid", "lookup": { "entity": "test_parent", "matchBy": "field", "keyField": "{{keyField}}" } } } }
        """);

    [Fact]
    public async Task A_lookup_by_key_field_resolves_the_one_record_with_the_text_reading_every_page()
    {
        // Mutant x5: only the first page was read. The pool returns one record per page.
        var parents = Parents(("1a2b3c4d-0000-4000-8000-000000000001", "P-1"), ("1a2b3c4d-0000-4000-8000-000000000002", "P-2"),
            ("1a2b3c4d-0000-4000-8000-000000000003", "P-3"));
        var run = await LoadAsync(Csv("test_name,test_parentid\nfirst,P-3\nsecond,P-1\n"), mapping: ParentByField("test_code"), parents: parents);
        Assert.Null(run.Refusal);
        Assert.Equal("test_name=text:first, test_parentid=ref:test_parent:1a2b3c4d-0000-4000-8000-000000000003 | " +
                     "test_name=text:second, test_parentid=ref:test_parent:1a2b3c4d-0000-4000-8000-000000000001",
            DescribeRows(Assert.Single(run.Upserts)));
    }

    [Fact]
    public async Task A_lookup_by_key_field_refuses_a_GUID_cell_several_records_or_none()
    {
        // Mutants m69 (a GUID-shaped cell taken as the id), m70 and m71 (last or first of several taken), m72 (an
        // unresolved key dropped the row with no problem).
        var shared = "1a2b3c4d-0000-4000-8000-000000000002";
        var parents = Parents(("1a2b3c4d-0000-4000-8000-000000000001", "P-1"), (shared, "TWIN"),
            ("1a2b3c4d-0000-4000-8000-000000000003", "TWIN"));
        var csv = Csv($"test_name,test_parentid\nfirst,P-1\nsecond,{shared}\nthird,TWIN\nfourth,NOPE\n");

        var run = await LoadAsync(csv, mapping: ParentByField("test_code"), parents: parents);
        AssertRefused(run, "2:test_parentid", "3:test_parentid", "4:test_parentid");
        Assert.Contains("2 test_parent records", run.Refusal!.Problems[1].Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test_since")]  // a date: its text depends on the machine's culture
    [InlineData("test_number")] // a whole number
    [InlineData("test_missing")] // not a column of the lookup's table
    public async Task A_lookup_key_field_that_is_not_a_text_column_is_refused(string keyField)
    {
        // Must-fix 4: a date key resolved to a different record by culture.
        var run = await LoadAsync(Csv("test_name,test_parentid\nfirst,01/02/2024 00:00:00\n"), mapping: ParentByField(keyField));
        AssertRefused(run, ":test_parentid");
    }

    [Fact]
    public async Task A_lookup_key_field_that_is_a_multiple_line_text_column_is_admitted()
    {
        var run = await LoadAsync(Csv("test_name,test_parentid\nfirst,P-1\n"), mapping: ParentByField("test_notes"));
        AssertRefused(run, "1:test_parentid"); // admitted as a key field; no record has the text
    }

    [Fact]
    public async Task Auto_mapping_by_normalised_name_with_two_candidate_columns_is_refused()
    {
        // Should-fix 2: "First Name" went to the first of test_first_name and test_firstname.
        var run = await LoadAsync(Csv("test_name,First Name\nfirst,Ann\n"), force: true);
        AssertRefused(run, ":First Name");
        Assert.Contains("test_first_name", run.Refusal!.Problems[0].Message, StringComparison.Ordinal);
        Assert.Contains("test_firstname", run.Refusal.Problems[0].Message, StringComparison.Ordinal);
    }
}

/// <summary>
/// The mapping's <c>dateFormat</c> under several time zones as well as cultures (review round 2, must-fix 2). The machine's
/// time zone is process-wide, so this class runs alone.
/// </summary>
[Collection(MachineTimeZone.Collection)]
[Trait("Category", "Unit")]
public sealed class CsvDataLoaderDateFormatTests : IDisposable
{
    private readonly TempCsvFiles _files = new();

    public void Dispose() => _files.Dispose();

    [Theory]
    [InlineData("dd/MM", "15/01")]                // no year: the current year was taken
    [InlineData("dd/MM/yy", "15/01/45")]          // two-digit year: the century was taken from a setting
    [InlineData("HH:mm", "00:00")]                // no date: today was taken
    [InlineData("MM/yyyy", "01/2024")]            // no day
    [InlineData("dd/yyyy", "15/2024")]            // no month
    [InlineData("yyyy-MM-dd HH:mm:ss.fff", "2024-01-15 00:00:00.000")] // a fraction of a second
    [InlineData("d", "1/15/2024")]                // a one-letter standard format
    public async Task A_date_format_that_lacks_a_part_is_refused_once_for_the_column(string format, string cell)
    {
        // Must-fix 2: a missing part came from the machine's clock or settings.
        var csv = _files.Csv($"test_name,test_day\nfirst,{cell}\nsecond,{cell}\n");
        var mapping = CsvDataLoaderValueTests.MappingJson($$"""
            { "columns": { "test_name": { "field": "test_name" }, "test_day": { "field": "test_day", "dateFormat": "{{format}}" } } }
            """);

        var runs = await MachineTimeZone.InEachAsync(() => InEveryCultureAsync(() => CsvDataLoaderValueTests.LoadAsync(csv, mapping: mapping)));
        Assert.All(runs.SelectMany(r => r), run => CsvDataLoaderValueTests.AssertRefused(run, ":test_day"));
    }

    [Fact]
    public async Task A_date_format_with_every_part_is_read_the_same_in_every_time_zone_and_culture()
    {
        var csv = _files.Csv("test_name,test_day,test_when\nfirst,15/01/2024,15/01/2024 09:30 +02:00\n");
        var mapping = CsvDataLoaderValueTests.MappingJson("""
            { "columns": { "test_name": { "field": "test_name" },
                           "test_day": { "field": "test_day", "dateFormat": "dd/MM/yyyy" },
                           "test_when": { "field": "test_when", "dateFormat": "dd/MM/yyyy HH:mm zzz" } } }
            """);

        var runs = await MachineTimeZone.InEachAsync(() => InEveryCultureAsync(() => CsvDataLoaderValueTests.LoadAsync(csv, mapping: mapping)));
        Assert.All(runs.SelectMany(r => r), run =>
        {
            Assert.Null(run.Refusal);
            Assert.Equal(
                "test_day=date:2024-01-15T00:00:00:Unspecified, test_name=text:first, test_when=date:2024-01-15T07:30:00:Utc",
                CsvDataLoaderValueTests.DescribeRows(Assert.Single(run.Upserts)));
        });
    }

}

/// <summary>CSV files written to a temporary folder, deleted with it.</summary>
internal sealed class TempCsvFiles : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ppds-load-values-").FullName;

    public string Csv(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
