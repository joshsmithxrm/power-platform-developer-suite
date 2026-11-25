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
/// <c>data delete --file</c> takes the ids only from <c>--id-column</c> or the table's primary id column, for JSON and
/// CSV alike, and refuses an entry without the id, a value that is not an id and a repeated id, with their count and
/// positions (#1915).
/// </summary>
[Trait("Category", "Unit")]
public sealed class RecordIdFileServiceTests : IDisposable
{
    private static readonly Guid Parent = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Child1 = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Child2 = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private readonly Mock<IMetadataQueryService> _metadata = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-idfile-" + Guid.NewGuid().ToString("N"));

    public RecordIdFileServiceTests()
    {
        Directory.CreateDirectory(_directory);
        PrimaryIdIs("account", "accountid");
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private void PrimaryIdIs(string entity, string primaryId)
        => _metadata.Setup(m => m.GetEntityAsync(entity, false, false, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadataDto { LogicalName = entity, SchemaName = entity, DisplayName = entity, PrimaryIdAttribute = primaryId });

    private string WriteFile(string content, string extension = ".json")
    {
        var path = Path.Combine(_directory, "ids" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, content);
        return path;
    }

    private Task<IReadOnlyList<Guid>> Read(string content, string? idColumn = null, string entity = "account", string extension = ".json")
        => new RecordIdFileService(_metadata.Object, NullLogger<RecordIdFileService>.Instance)
            .ReadIdsAsync(WriteFile(content, extension), entity, idColumn);

    private async Task<PpdsException> Refused(string content, string? idColumn = null, string extension = ".json")
    {
        var act = () => Read(content, idColumn, extension: extension);
        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.RecordIdFileInvalid);
        return ex;
    }

    private static string ChildrenJson =>
        $"[{{\"parentaccountid\":\"{Parent}\",\"accountid\":\"{Child1}\",\"name\":\"Child 1\"}}," +
        $" {{\"parentaccountid\":\"{Parent}\",\"accountid\":\"{Child2}\",\"name\":\"Child 2\"}}]";

    [Fact]
    public async Task Json_LookupBeforeThePrimaryId_TakesThePrimaryId()
    {
        var ids = await Read(ChildrenJson);

        ids.Should().Equal(Child1, Child2);
    }

    [Fact]
    public async Task Json_IdColumnOption_IsHonoured_EvenWhenItNamesTheLookup()
    {
        // The user named the column, so that column is read: here the parent, twice, which is refused as a repeat.
        var ex = await Refused(ChildrenJson, idColumn: "parentaccountid");

        ex.UserMessage.Should().Contain("1 entry repeats an id given earlier in the file (entry 2)");
    }

    [Fact]
    public async Task Json_IdColumnOption_TakesThatColumn()
    {
        var json = $"[{{\"accountid\":\"{Parent}\",\"new_targetid\":\"{Child1}\"}},{{\"accountid\":\"{Parent}\",\"new_targetid\":\"{Child2}\"}}]";

        var ids = await Read(json, idColumn: "new_targetid");

        ids.Should().Equal(Child1, Child2);
        _metadata.Verify(m => m.GetEntityAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
            It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Json_PrimaryIdComesFromMetadata_NotFromTheTableName()
    {
        PrimaryIdIs("email", "activityid");
        var json = $"[{{\"emailid\":\"{Parent}\",\"activityid\":\"{Child1}\"}}]";

        var ids = await Read(json, entity: "email");

        ids.Should().Equal(Child1);
    }

    [Fact]
    public async Task Json_RecordWithoutThePrimaryId_IsRefusedWithItsPosition()
    {
        var json = $"[{{\"accountid\":\"{Child1}\"}},{{\"parentaccountid\":\"{Parent}\"}},{{\"accountid\":\"{Child2}\"}}]";

        var ex = await Refused(json);

        ex.UserMessage.Should().Contain("1 entry has no 'accountid' (entry 2)").And.Contain("Nothing was deleted");
        ex.UserMessage.Should().NotContain(Parent.ToString());
    }

    [Fact]
    public async Task Json_ValuesThatAreNotIds_AreRefusedWithCountAndPositions()
    {
        var json = $"[\"{Child1}\", \"not-a-guid\", 42, null, \"{Guid.Empty}\", {{\"accountid\":\"x\"}}]";

        var ex = await Refused(json);

        ex.UserMessage.Should().Contain("5 entries hold a value that is not a record id (entries 2, 3, 4, 5, 6)");
        ex.UserMessage.Should().NotContain("not-a-guid");
    }

    [Fact]
    public async Task Json_RepeatedId_IsRefused()
    {
        var ex = await Refused($"[\"{Child1}\", \"{Child2}\", \"{Child1}\"]");

        ex.UserMessage.Should().Contain("1 entry repeats an id given earlier in the file (entry 3)");
    }

    [Fact]
    public async Task Json_PropertyNamedTwiceInOtherCase_IsRefused()
    {
        var ex = await Refused($"[{{\"accountid\":\"{Child1}\",\"AccountId\":\"{Child2}\"}}]");

        ex.UserMessage.Should().Contain("names 'accountid' more than once");
    }

    [Fact]
    public async Task Json_RecordsWrapper_IsRead()
    {
        var ids = await Read($"{{\"records\":[{{\"accountid\":\"{Child1}\"}}]}}");

        ids.Should().Equal(Child1);
    }

    [Fact]
    public async Task Json_ObjectThatIsNotAList_IsRefused()
    {
        var ex = await Refused($"{{\"accountid\":\"{Child1}\"}}");

        ex.UserMessage.Should().Contain("neither an array");
    }

    [Fact]
    public async Task Json_Malformed_IsRefused()
    {
        var ex = await Refused("[\"a\",");

        ex.UserMessage.Should().Contain("not valid JSON");
    }

    [Fact]
    public async Task Json_BareIdList_IsRead()
    {
        var ids = await Read($"[\"{Child1}\",\"{Child2}\"]");

        ids.Should().Equal(Child1, Child2);
    }

    [Fact]
    public async Task Csv_PrimaryIdColumn_IsRead()
    {
        var ids = await Read($"parentaccountid,accountid,name\n{Parent},{Child1},\"Child, 1\"\r\n{Parent},{Child2},Child 2\n", extension: ".csv");

        ids.Should().Equal(Child1, Child2);
    }

    [Fact]
    public async Task Csv_InvalidCells_AreRefusedWithTheirLineNumbers()
    {
        var csv = $"accountid,name\n{Child1},a\n\nnot-a-guid,b\n,c\n{Child1},d\n{Guid.Empty},e";

        var ex = await Refused(csv, extension: ".csv");

        // Line 3 is blank and skipped; line numbers are the file's own.
        ex.UserMessage.Should().Contain("3 entries hold a value that is not a record id (lines 4, 5, 7)")
            .And.Contain("1 entry repeats an id given earlier in the file (line 6)");
    }

    [Fact]
    public async Task Csv_GenericIdHeader_IsNotGuessed()
    {
        var ex = await Refused($"id,name\n{Child1},a\n", extension: ".csv");

        ex.UserMessage.Should().Contain("has no column 'accountid'").And.Contain("--id-column");
    }

    [Fact]
    public async Task Csv_IdColumnOption_IsUsed()
    {
        var ids = await Read($"id,name\n{Child1},a\n", idColumn: "id", extension: ".csv");

        ids.Should().Equal(Child1);
    }

    [Fact]
    public async Task PrimaryIdCannotBeRead_IsRefusedBeforeAnyRecordIsRead()
    {
        _metadata.Setup(m => m.GetEntityAsync("account", false, false, false, false, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("no such table"));

        var act = () => Read(ChildrenJson);

        var ex = (await act.Should().ThrowAsync<PpdsException>()).Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Query.ExecutionFailed);
        ex.UserMessage.Should().Contain("--id-column");
    }

    [Fact]
    public async Task MissingFile_IsRefused()
    {
        var act = () => new RecordIdFileService(_metadata.Object, NullLogger<RecordIdFileService>.Instance)
            .ReadIdsAsync(Path.Combine(_directory, "missing.json"), "account", null);

        (await act.Should().ThrowAsync<PpdsException>()).Which.ErrorCode.Should().Be(ErrorCodes.Validation.FileNotFound);
    }

    // ---- Review round 1: the CSV file is read by the CSV rules and refused whole when any record is not exact.

    [Fact]
    public async Task Csv_UnquotedCommaShiftsTheParentUnderTheIdColumn_FileIsRefused()
    {
        // "Contoso, Inc" unquoted makes four fields: the parent's id would sit under the id column.
        var ex = await Refused($"name,parentaccountid,accountid\nContoso, Inc,{Parent},{Child1}\nFabrikam,{Parent},{Child2}\n", extension: ".csv");

        ex.UserMessage.Should().Contain("1 record has a number of fields other than the header's 3 (line 2: 4)")
            .And.Contain("Nothing was deleted");
        ex.UserMessage.Should().NotContain(Parent.ToString()).And.NotContain("Contoso");
    }

    [Fact]
    public async Task Csv_FieldLeftOutBeforeTheId_FileIsRefused()
    {
        var ex = await Refused($"name,parentaccountid,accountid\n{Parent},{Child1}\n", extension: ".csv");

        ex.UserMessage.Should().Contain("(line 2: 2)");
    }

    [Fact]
    public async Task Csv_IdLookingTextInsideAMultiLineQuotedField_IsNotARecord()
    {
        // The second line is text inside the first record's description, not a record of its own.
        var csv = $"accountid,description\n{Child1},\"Duplicate of\n{Child2},see above\"\n";

        var ids = await Read(csv, extension: ".csv");

        ids.Should().Equal(Child1);
    }

    [Fact]
    public async Task Csv_FileEndsInsideAnOpenQuote_IsRefused()
    {
        var ex = await Refused($"accountid,description\n{Child1},\"Duplicate of\n{Child2},see above\n", extension: ".csv");

        ex.UserMessage.Should().Contain("a quoted field opened on line 2 is never closed");
        ex.UserMessage.Should().NotContain(Child2.ToString());
    }

    [Fact]
    public async Task Csv_QuoteInsideAnUnquotedField_IsRefused()
    {
        var ex = await Refused($"accountid,name\n{Child1},Contoso \"East\"\n{Child2},b\n", extension: ".csv");

        ex.UserMessage.Should().Contain("1 record has a quote that is not allowed there (line 2)");
    }

    [Fact]
    public async Task Csv_QuotedFieldsDoubledQuotesCrLfAndByteOrderMark_AreRead()
    {
        var csv = $"\uFEFFaccountid,name\r\n\"{Child1}\",\"Contoso, \"\"East\"\"\"\r\n{Child2},\"two\r\nlines\"\r\n";

        var ids = await Read(csv, extension: ".csv");

        ids.Should().Equal(Child1, Child2);
    }

    [Fact]
    public async Task Csv_LineNumbersCountTheLinesOfMultiLineRecords()
    {
        var csv = $"accountid,name\n{Child1},\"a\nb\"\n\nnot-an-id,c\n";

        var ex = await Refused(csv, extension: ".csv");

        ex.UserMessage.Should().Contain("(line 5)");
    }

    [Theory]
    [InlineData("accountid,name,accountid")]
    [InlineData("accountid,name,AccountId")]
    public async Task Csv_HeaderNamesTheIdColumnTwice_IsRefused(string header)
    {
        var ex = await Refused($"{header}\n{Child1},a,{Child2}\n", extension: ".csv");

        ex.UserMessage.Should().Contain("has the column 'accountid' 2 times");
    }

    [Fact]
    public async Task Csv_RowShorterThanTheHeader_IsRefused()
    {
        var ex = await Refused($"name,accountid\na,{Child1}\nb\n", extension: ".csv");

        ex.UserMessage.Should().Contain("(line 3: 1)");
    }

    [Fact]
    public async Task Csv_FirstLineIsARecordNotAHeader_ItsValuesAreNotRepeated()
    {
        var ex = await Refused($"{Child1},Contoso\n{Child2},Fabrikam\n", extension: ".csv");

        ex.UserMessage.Should().Contain("(2 columns found)");
        ex.UserMessage.Should().NotContain(Child1.ToString()).And.NotContain("Contoso");
    }

    [Fact]
    public async Task Json_RecordsKeyTwice_IsRefused()
    {
        var ex = await Refused($"{{\"records\":[\"{Child1}\"],\"records\":[\"{Child2}\"]}}");

        ex.UserMessage.Should().Contain("'records' key more than once");
    }

    [Fact]
    public async Task Json_RecordsKeyInTwoCasings_IsRefused()
    {
        var ex = await Refused($"{{\"Records\":[\"{Child1}\"],\"records\":[\"{Child2}\"]}}");

        ex.UserMessage.Should().Contain("'records' key more than once");
    }

    [Fact]
    public async Task Json_RecordWithAnotherPropertyTwice_IsRefused()
    {
        var ex = await Refused($"[{{\"accountid\":\"{Child1}\",\"name\":\"a\",\"name\":\"b\"}}]");

        ex.UserMessage.Should().Contain("1 entry holds another property more than once (entry 1)");
    }

    // ---- Review round 2: the id spelling rule, white space, lone surrogates, a semicolon file, no echo of cells.

    [Theory]
    [InlineData("+ccccccc-0000-0000-0000-000000000001")]
    [InlineData("0xcccccc-0000-0000-0000-000000000001")]
    [InlineData("cccccccc-0x12-0000-0000-000000000001")]
    [InlineData(" cccccccc-0000-0000-0000-000000000001")]
    [InlineData("cccccccc-0000-0000-0000-000000000001 ")]
    [InlineData("cccccccc000000000000000000000001")]
    [InlineData("(cccccccc-0000-0000-0000-000000000001)")]
    public async Task Json_DamagedOrPaddedId_IsRefused(string text)
    {
        var ex = await Refused($"[\"{text}\"]");

        ex.UserMessage.Should().Contain("1 entry holds a value that is not a record id (entry 1)");
    }

    [Theory]
    [InlineData("+ccccccc-0000-0000-0000-000000000001")]
    [InlineData("0Xcccccc-0000-0000-0000-000000000001")]
    [InlineData(" cccccccc-0000-0000-0000-000000000001")]
    [InlineData("\"cccccccc-0000-0000-0000-000000000001 \"")]
    [InlineData("\"cccccccc-0000-0000-0000-000000000001\n\"")]
    public async Task Csv_DamagedOrPaddedId_IsRefused(string cell)
    {
        var ex = await Refused($"accountid,name\n{cell},a\n", extension: ".csv");

        ex.UserMessage.Should().Contain("1 entry holds a value that is not a record id (line 2)");
    }

    [Fact]
    public async Task UpperCaseAndBracedIds_AreRead()
    {
        var ids = await Read($"[\"{Child1.ToString().ToUpperInvariant()}\", \"{{{Child2}}}\"]");

        ids.Should().Equal(Child1, Child2);
    }

    [Fact]
    public async Task Json_LoneSurrogateInAValue_IsRefusedAsAnInvalidFile()
    {
        var ex = await Refused("[\"\\ud800\"]");

        ex.UserMessage.Should().Contain("not valid Unicode");
    }

    [Fact]
    public async Task Json_LoneSurrogateInAPropertyName_IsRefusedAsAnInvalidFile()
    {
        var ex = await Refused($"[{{\"accountid\":\"{Child1}\",\"\\udc00\":1}}]");

        ex.UserMessage.Should().Contain("not valid Unicode");
    }

    [Fact]
    public async Task Csv_SemicolonFile_IsRefused_DelimiterIsNotDetected()
    {
        var ex = await Refused($"accountid;name\n{Child1};a\n{Child2};b\n", extension: ".csv");

        ex.UserMessage.Should().Contain("has no column 'accountid' (1 column found)");
    }

    [Fact]
    public async Task Csv_FirstRowThatLooksLikeColumnNames_IsNotEchoed()
    {
        var ex = await Refused("contoso,fabrikam\nnorthwind,adventureworks\n", extension: ".csv");

        ex.UserMessage.Should().Contain("(2 columns found)").And.NotContain("contoso").And.NotContain("fabrikam");
    }
}
