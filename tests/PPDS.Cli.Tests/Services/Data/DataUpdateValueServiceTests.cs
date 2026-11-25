using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using Xunit;
using static PPDS.Cli.Tests.Services.Data.ColumnValueTestSupport;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// The values of <c>data update --set</c> and <c>--file</c> (#1917, #1936): read by one exact grammar or the strict CSV
/// reader, converted by each column's type from metadata, never guessed from the text and never in the machine's culture.
/// Each culture-sensitive test runs under every culture of <see cref="Cultures"/> and must give the same result.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataUpdateValueServiceTests : IDisposable
{
    private const string Table = "account";
    private const string ParentId = "8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab";
    private const string RecordId = "11111111-2222-3333-4444-555555555555";
    private const string OtherId = "66666666-7777-8888-9999-000000000000";

    private readonly string _directory = Directory.CreateTempSubdirectory("ppds-update-values-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AttributeMetadata[] AccountColumns()
    {
        var created = Date("createdon", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime);
        created.IsValidForUpdate = false;
        var id = Identifier("accountid");
        id.IsValidForUpdate = false;
        return
        [
            id,
            Text("name"),
            Text("description"),
            Decimal("rate", 2),
            FloatingPoint("ratio", 5),
            Currency("revenue", 2),
            WholeNumber("numberofemployees"),
            YesNo("donotemail"),
            Choice("industrycode", 1, 2, 3),
            Lookup("parentaccountid", "account"),
            Lookup("ownerid", "systemuser", "team"),
            Date("lastcontact", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime),
            Date("founded", DateTimeBehavior.DateOnly, DateTimeFormat.DateOnly),
            Identifier("legacyid"),
            created,
        ];
    }

    private static DataUpdateValueService Service() => new(PoolWithColumns(Table, AccountColumns()).Object);

    private static string DescribeValues(IEnumerable<KeyValuePair<string, object?>> values) =>
        string.Join(", ", values.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={Describe(v.Value)}"));

    private static async Task<string> SetOutcomeAsync(string set)
    {
        try
        {
            return DescribeValues(await Service().ConvertSetValuesAsync(Table, set));
        }
        catch (DataValuesRefusedException ex)
        {
            Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
            return "refused: " + ex.UserMessage;
        }
    }

    [Theory]
    // The value of #1917: before, rate=1.5 was written as 15 on a de-DE machine.
    [InlineData("rate=1.5", "rate=decimal:1.5")]
    [InlineData("ratio=1.5", "ratio=double:1.5")]
    [InlineData("revenue=1.5", "revenue=money:1.5")]
    // A number-looking text stays text, and each kind gets its own typed value.
    [InlineData("name=123", "name=text:123")]
    [InlineData("name=\"1,5\"", "name=text:1,5")]
    [InlineData("name=" + ParentId, "name=text:" + ParentId)]
    [InlineData("numberofemployees=42", "numberofemployees=int:42")]
    [InlineData("industrycode=2", "industrycode=option:2")]
    [InlineData("parentaccountid=" + ParentId, "parentaccountid=ref:account:" + ParentId)]
    [InlineData("donotemail=true", "donotemail=bool:True")]
    [InlineData("lastcontact=2024-01-15T09:30:00+01:00", "lastcontact=date:2024-01-15T08:30:00:Utc")]
    [InlineData("founded=2024-01-15", "founded=date:2024-01-15T00:00:00:Unspecified")]
    [InlineData("name=null", "name=null")]
    [InlineData("name=NULL", "name=null")]
    [InlineData("Name=Contoso,Rate=2.25", "name=text:Contoso, rate=decimal:2.25")]
    [InlineData("name=Contoso, rate=2.25", "name=text:Contoso, rate=decimal:2.25")]
    // Round 1: the grammar. A quoted value is taken exactly between the quotes, a doubled quote is one quote, and a
    // quoted null is text.
    [InlineData("description=\"He said \"\"hi\"\"\"", "description=text:He said \"hi\"")]
    [InlineData("name=\"  padded  \"", "name=text:  padded  ")]
    [InlineData("name=\"Smith, Jr\",rate=1.5", "name=text:Smith, Jr, rate=decimal:1.5")]
    [InlineData("name=\"null\"", "name=text:null")]
    [InlineData("name=a=b", "name=text:a=b")]
    public async Task A_value_is_converted_by_its_column_type_the_same_in_every_culture(string set, string expected)
    {
        var outcomes = await InEveryCultureAsync(() => SetOutcomeAsync(set));
        Assert.All(outcomes, o => Assert.Equal(expected, o));
    }

    [Theory]
    // The reviewer's examples: before, the quotes were dropped and the pair after a stray quote was swallowed.
    [InlineData("name=He said \"hi\"", "Column 'name': --set pair 1: the value holds a double quote")]
    [InlineData("name=5\" pipe,rate=1.5", "Column 'name': --set pair 1: the value holds a double quote")]
    [InlineData("name=\"unclosed,rate=1.5", "Column 'name': --set pair 1: the double quote that opens the value is not closed")]
    [InlineData("name=\"a\"b,rate=1.5", "Column 'name': --set pair 1: text follows the closing double quote")]
    [InlineData("name=", "Column 'name': --set pair 1 has no value; write null")]
    [InlineData("name=a,", "--set ends with a comma")]
    [InlineData("name=a,,rate=1", "--set pair 2 is not field=value")]
    [InlineData("=5", "--set pair 1 has no field name")]
    [InlineData("rate=\"1,5\"", "Column 'rate': --set pair 1")]
    [InlineData("rate=1,234.5", "--set pair 2 is not field=value")]
    [InlineData("ratio=\"1,5\"", "Column 'ratio'")]
    [InlineData("revenue=\"1,5\"", "Column 'revenue'")]
    [InlineData("revenue=$5", "Column 'revenue'")]
    [InlineData("rate=1.5 ", "Column 'rate'")]
    [InlineData("numberofemployees=1.5", "Column 'numberofemployees'")]
    [InlineData("industrycode=9", "Column 'industrycode'")]
    [InlineData("parentaccountid=Contoso", "Column 'parentaccountid'")]
    [InlineData("ownerid=" + ParentId, "Column 'ownerid'")]
    [InlineData("donotemail=ja", "Column 'donotemail'")]
    [InlineData("lastcontact=2024-01-15", "Column 'lastcontact'")]
    [InlineData("lastcontact=15.01.2024 09:30", "Column 'lastcontact'")]
    [InlineData("founded=01/15/2024", "Column 'founded'")]
    [InlineData("nosuchcolumn=1", "'nosuchcolumn' is not a column of the table")]
    [InlineData("createdon=2024-01-15T09:30:00Z", "'createdon' cannot be updated")]
    [InlineData("name=a,name=b", "is given more than once")]
    public async Task A_value_that_is_not_exact_is_refused_before_anything_is_read(string set, string named)
    {
        var outcomes = await InEveryCultureAsync(() => SetOutcomeAsync(set));
        Assert.All(outcomes, o =>
        {
            Assert.StartsWith("refused: ", o);
            Assert.Contains(named, o, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_refusal_names_every_bad_column_and_repeats_no_value()
    {
        var outcome = await SetOutcomeAsync("rate=\"1,5\",revenue=\"2,75\",name=fine");
        Assert.Contains("2 problems in the input", outcome, StringComparison.Ordinal);
        Assert.Contains("'rate'", outcome, StringComparison.Ordinal);
        Assert.Contains("'revenue'", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("2,75", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("He said", await SetOutcomeAsync("name=He said \"hi\""), StringComparison.Ordinal);
    }

    private string File(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        System.IO.File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    private static async Task<string> FileOutcomeAsync(string path, string? mapping = null, string? idColumn = null)
    {
        try
        {
            var rows = await Service().ReadUpdateFileAsync(Table, path, idColumn, mapping);
            return string.Join(" | ", rows.Select(r => $"{r.Id:D}: {DescribeValues(r.Attributes)}"));
        }
        catch (DataValuesRefusedException ex)
        {
            Assert.Equal(ErrorCodes.Validation.InvalidValue, ex.ErrorCode);
            return "refused: " + string.Join(" / ", ex.Problems);
        }
    }

    [Fact]
    public async Task File_cells_are_converted_by_their_column_type_the_same_in_every_culture()
    {
        var path = File($"""
            accountid,name,rate,revenue,numberofemployees,parentaccountid
            {RecordId},123,1.5,10.25,7,{ParentId}
            {OtherId},Contoso,,0.5,-1,
            """);

        var outcomes = await InEveryCultureAsync(() => FileOutcomeAsync(path));
        Assert.All(outcomes, o => Assert.Equal(
            $"{RecordId}: name=text:123, numberofemployees=int:7, parentaccountid=ref:account:{ParentId}, rate=decimal:1.5, revenue=money:10.25" +
            $" | {OtherId}: name=text:Contoso, numberofemployees=int:-1, parentaccountid=null, rate=null, revenue=money:0.5",
            o));
    }

    [Fact]
    public async Task File_text_is_kept_exactly_and_a_quoted_line_break_is_one_cell()
    {
        // Review round 1, must-fix 4 and 5: the old reader dropped quotes, trimmed padding and split a quoted line break
        // into two records.
        var path = File($"""
            accountid,description
            {RecordId},"He said ""hi"", twice
            and again"
            {OtherId},"  padded  "
            """);

        Assert.Equal(
            $"{RecordId}: description=text:He said \"hi\", twice\nand again | {OtherId}: description=text:  padded  ",
            await FileOutcomeAsync(path));
    }

    [Fact]
    public async Task One_bad_cell_or_row_refuses_the_whole_file_naming_its_row_and_column()
    {
        var path = File($"""
            accountid,name,rate
            {RecordId},first,1.5
            {OtherId},second,"1,5"
            not-a-guid,third,2.5
            {RecordId},fourth,3
             {ParentId},fifth,4
            """);

        var outcomes = await InEveryCultureAsync(() => FileOutcomeAsync(path));
        Assert.All(outcomes, o => Assert.Equal(
            "refused: Row 2, column 'rate': A decimal column takes digits with an optional sign and an optional point and digits (1234.75): no thousands separator, no comma, no exponent." +
            " / Row 3, column 'accountid': The record id is not a GUID with dashes (8-4-4-4-12), optionally in braces, with nothing around it." +
            " / Row 4, column 'accountid': The record id is also on row 1." +
            " / Row 5, column 'accountid': The record id is not a GUID with dashes (8-4-4-4-12), optionally in braces, with nothing around it.",
            o));
    }

    [Theory]
    [InlineData("accountid,name\n{0},a,b\n", "Row 1: The row has 3 field(s) and the header has 2")]
    [InlineData("accountid,name\n{0}\n", "Row 1: The row has 1 field(s) and the header has 2")]
    [InlineData("accountid,name\n{0},a\"b\n", "Row 1: The row is not valid CSV")]
    [InlineData("accountid,name,NAME\n{0},a,b\n", "Column 'name': The header names this column 2 times")]
    [InlineData("", "The file is empty")]
    [InlineData("accountid,nosuch\n{0},1\n", "Column 'nosuch': 'nosuch' is not a column of the table")]
    [InlineData("accountid,createdon\n{0},2024-01-15T09:30:00Z\n", "Column 'createdon': 'createdon' cannot be updated")]
    [InlineData("id,name\n{0},a\n", "The file has no column 'accountid' (the table's primary id column)")]
    public async Task A_file_that_is_not_exact_is_refused_before_any_row_is_converted(string content, string expected)
    {
        var outcome = await FileOutcomeAsync(File(string.Format(content, RecordId)));
        Assert.StartsWith("refused: ", outcome);
        Assert.Contains(expected, outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_id_column_is_the_one_named_or_the_tables_primary_id_never_a_guessed_header()
    {
        var path = File($"legacyid,name\n{RecordId},first\n");
        Assert.Contains("has no column 'accountid'", await FileOutcomeAsync(path), StringComparison.Ordinal);
        Assert.Equal($"{RecordId}: name=text:first", await FileOutcomeAsync(path, idColumn: "legacyid"));
        Assert.Contains("Column 'nosuch': The file has no column with this name", await FileOutcomeAsync(path, idColumn: "nosuch"),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_file_names_the_column_whose_type_decides()
    {
        var path = File($"Account ID,Rate %\n{RecordId},2.5\n");
        var mapping = Path.Combine(_directory, "mapping.json");
        System.IO.File.WriteAllText(mapping, """{"columns":{"Rate %":{"field":"rate"},"Account ID":{"field":"accountid"}}}""");

        var outcomes = await InEveryCultureAsync(() => FileOutcomeAsync(path, mapping));
        Assert.All(outcomes, o => Assert.Equal($"{RecordId}: rate=decimal:2.5", o));

        System.IO.File.WriteAllText(mapping, """{"columns":{"Rate %":{"field":"rate"},"Other":{"field":"rate"}}}""");
        var twice = File($"accountid,Rate %,Other\n{RecordId},2.5,3\n");
        Assert.Contains("Column 'Other': The column names 'rate', as column 'Rate %' does", await FileOutcomeAsync(twice, mapping),
            StringComparison.Ordinal);
    }
}
