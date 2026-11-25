using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using Xunit;
using static PPDS.Cli.Tests.Services.Data.ColumnValueTestSupport;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// The values of <c>data update --set</c> and <c>--file</c> (#1917): converted by each column's type from metadata,
/// never guessed from the text and never in the machine's culture. Each test runs under the invariant culture, de-DE
/// and fr-FR and must give the same result.
/// </summary>
[Trait("Category", "Unit")]
public sealed class DataUpdateValueServiceTests : IDisposable
{
    private const string Table = "account";
    private const string ParentId = "8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab";
    private const string RecordId = "11111111-2222-3333-4444-555555555555";

    private readonly string _directory = Directory.CreateTempSubdirectory("ppds-update-values-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static AttributeMetadata[] AccountColumns()
    {
        var created = Date("createdon", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime);
        created.IsValidForUpdate = false;
        return
        [
            Identifier("accountid"),
            Text("name"),
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
        catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Validation.InvalidValue)
        {
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
    [InlineData("Name=Contoso,Rate=2.25", "name=text:Contoso, rate=decimal:2.25")]
    public async Task A_value_is_converted_by_its_column_type_the_same_in_every_culture(string set, string expected)
    {
        var outcomes = await InEveryCultureAsync(() => SetOutcomeAsync(set));
        Assert.All(outcomes, o => Assert.Equal(expected, o));
    }

    [Theory]
    [InlineData("rate=\"1,5\"", "--set column 'rate'")]
    [InlineData("rate=1,234.5", "--set pair 2")]
    [InlineData("rate=1.234,5", "--set pair 2")]
    [InlineData("ratio=\"1,5\"", "--set column 'ratio'")]
    [InlineData("revenue=\"1,5\"", "--set column 'revenue'")]
    [InlineData("revenue=$5", "--set column 'revenue'")]
    [InlineData("numberofemployees=1.5", "--set column 'numberofemployees'")]
    [InlineData("industrycode=9", "--set column 'industrycode'")]
    [InlineData("parentaccountid=Contoso", "--set column 'parentaccountid'")]
    [InlineData("ownerid=" + ParentId, "--set column 'ownerid'")]
    [InlineData("donotemail=ja", "--set column 'donotemail'")]
    [InlineData("lastcontact=2024-01-15", "--set column 'lastcontact'")]
    [InlineData("lastcontact=15.01.2024 09:30", "--set column 'lastcontact'")]
    [InlineData("founded=01/15/2024", "--set column 'founded'")]
    [InlineData("nosuchcolumn=1", "'nosuchcolumn' is not a column of the table")]
    [InlineData("createdon=2024-01-15T09:30:00Z", "'createdon' cannot be updated")]
    [InlineData("name=a,name=b", "is given more than once")]
    [InlineData("=5", "--set pair 1")]
    public async Task A_value_that_is_not_exact_for_its_column_is_refused_before_anything_is_read(string set, string named)
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
        Assert.Contains("2 values are not valid", outcome, StringComparison.Ordinal);
        Assert.Contains("'rate'", outcome, StringComparison.Ordinal);
        Assert.Contains("'revenue'", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("1,5", outcome, StringComparison.Ordinal);
        Assert.DoesNotContain("2,75", outcome, StringComparison.Ordinal);
    }

    private string File(string content)
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.csv");
        System.IO.File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return path;
    }

    private static async Task<string> FileOutcomeAsync(string path, string? mapping = null)
    {
        try
        {
            var rows = await Service().ReadUpdateFileAsync(Table, path, null, mapping);
            return string.Join(" | ", rows.Select(r => $"{r.Id:D}: {DescribeValues(r.Attributes)}"));
        }
        catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Validation.InvalidValue)
        {
            return "refused: " + ex.UserMessage;
        }
    }

    [Fact]
    public async Task File_cells_are_converted_by_their_column_type_the_same_in_every_culture()
    {
        var path = File($"""
            accountid,name,rate,revenue,numberofemployees,parentaccountid
            {RecordId},123,1.5,10.25,7,{ParentId}
            {RecordId},Contoso,,0.5,-1,
            """);

        var outcomes = await InEveryCultureAsync(() => FileOutcomeAsync(path));
        Assert.All(outcomes, o => Assert.Equal(
            $"{RecordId}: name=text:123, numberofemployees=int:7, parentaccountid=ref:account:{ParentId}, rate=decimal:1.5, revenue=money:10.25" +
            $" | {RecordId}: name=text:Contoso, numberofemployees=int:-1, parentaccountid=null, rate=null, revenue=money:0.5",
            o));
    }

    [Fact]
    public async Task One_bad_cell_refuses_the_whole_file_naming_its_row_and_column()
    {
        var path = File($"""
            accountid,name,rate
            {RecordId},first,1.5
            {RecordId},second,"1,5"
            not-a-guid,third,2.5
            {RecordId},fourth
            """);

        var outcomes = await InEveryCultureAsync(() => FileOutcomeAsync(path));
        Assert.All(outcomes, o =>
        {
            Assert.StartsWith("refused: 3 values are not valid", o);
            Assert.Contains("Row 2, column 'rate'", o, StringComparison.Ordinal);
            Assert.Contains("Row 3, column 'accountid': the record id is not a GUID", o, StringComparison.Ordinal);
            Assert.Contains("Row 4: the row has 2 cell(s) and the header has 3", o, StringComparison.Ordinal);
            Assert.DoesNotContain("1,5", o, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_header_that_is_not_a_column_is_refused_before_any_row_is_read()
    {
        var path = File($"accountid,nosuch\n{RecordId},1\n");
        var outcome = await FileOutcomeAsync(path);
        Assert.StartsWith("refused: 1 value is not valid", outcome);
        Assert.Contains("Header 'nosuch'", outcome, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_mapping_file_names_the_column_whose_type_decides()
    {
        var path = File($"accountid,Rate %\n{RecordId},2.5\n");
        var mapping = Path.Combine(_directory, "mapping.json");
        System.IO.File.WriteAllText(mapping, """{"columns":{"Rate %":{"field":"rate"}}}""");

        var outcomes = await InEveryCultureAsync(() => FileOutcomeAsync(path, mapping));
        Assert.All(outcomes, o => Assert.Equal($"{RecordId}: rate=decimal:2.5", o));
    }
}
