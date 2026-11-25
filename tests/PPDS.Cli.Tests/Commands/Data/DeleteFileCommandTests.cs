using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Progress;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

/// <summary>
/// <c>data delete --file</c> end to end with a recording bulk executor: a file that is not exactly a list of the
/// table's ids leads to no delete call at all; an exact file deletes exactly its ids (#1915).
/// </summary>
[Trait("Category", "Unit")]
public sealed class DeleteFileCommandTests : IDisposable
{
    private static readonly Guid Parent = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Child1 = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid Child2 = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ppds-deletefile-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<IBulkOperationExecutor> _bulk = new();
    private readonly List<List<Guid>> _deleteCalls = [];

    public DeleteFileCommandTests()
    {
        Directory.CreateDirectory(_directory);
        _bulk.Setup(b => b.DeleteMultipleAsync(It.IsAny<string>(), It.IsAny<IEnumerable<Guid>>(), It.IsAny<BulkOperationOptions?>(),
                It.IsAny<DataverseClientOptions?>(), It.IsAny<IProgress<ProgressSnapshot>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IEnumerable<Guid> ids, BulkOperationOptions? _, DataverseClientOptions? _,
                IProgress<ProgressSnapshot>? _, CancellationToken _) =>
            {
                var list = ids.ToList();
                _deleteCalls.Add(list);
                return new BulkOperationResult { SuccessCount = list.Count };
            });
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private Task<ServiceProvider> Provider(string? profile, string? environment, PPDS.Cli.Infrastructure.GlobalOptionValues options, CancellationToken ct)
    {
        var metadata = new Mock<IMetadataQueryService>();
        metadata.Setup(m => m.GetEntityAsync("account", false, false, false, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityMetadataDto { LogicalName = "account", SchemaName = "Account", DisplayName = "Account", PrimaryIdAttribute = "accountid" });
        return Task.FromResult(new ServiceCollection()
            .AddSingleton(new Mock<IDataQueryService>(MockBehavior.Strict).Object)
            .AddSingleton(_bulk.Object)
            .AddSingleton<IRecordIdFileService>(new RecordIdFileService(metadata.Object, NullLogger<RecordIdFileService>.Instance))
            .BuildServiceProvider());
    }

    private async Task<(int Exit, JsonDocument Output)> Run(string content, string extension)
    {
        var path = Path.Combine(_directory, "ids" + extension);
        await File.WriteAllTextAsync(path, content);
        var previousOut = Console.Out;
        var previousError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var parsed = DeleteCommand.Create(Provider)
                .Parse(["--entity", "account", "--file", path, "--force", "--output-format", "json"]);
            Assert.Empty(parsed.Errors);
            var exit = await parsed.InvokeAsync();
            return (exit, JsonDocument.Parse(stdout.ToString()));
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }

    [Fact]
    public async Task CsvWithAnUnquotedCommaBeforeTheId_NoDeleteCall()
    {
        var (exit, output) = await Run($"name,parentaccountid,accountid\nContoso, Inc,{Parent},{Child1}\n", ".csv");

        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Equal(ErrorCodes.Validation.RecordIdFileInvalid, output.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(_deleteCalls);
        _bulk.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CsvWithAnIdInsideAMultiLineQuotedField_DeletesOnlyTheRecord()
    {
        var (exit, _) = await Run($"accountid,description\n{Child1},\"Duplicate of\n{Child2},see above\"\n", ".csv");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal([Child1], Assert.Single(_deleteCalls));
    }

    [Fact]
    public async Task CsvEndingInsideAnOpenQuote_NoDeleteCall()
    {
        var (exit, _) = await Run($"accountid,description\n{Child1},\"Duplicate of\n{Child2},see above\n", ".csv");

        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Empty(_deleteCalls);
    }

    [Fact]
    public async Task JsonWithALookupBeforeTheId_DeletesTheChildrenNotTheParent()
    {
        var json = $"[{{\"parentaccountid\":\"{Parent}\",\"accountid\":\"{Child1}\"}},{{\"parentaccountid\":\"{Parent}\",\"accountid\":\"{Child2}\"}}]";

        var (exit, _) = await Run(json, ".json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal([Child1, Child2], Assert.Single(_deleteCalls));
    }

    [Fact]
    public async Task JsonWithTheRecordsKeyTwice_NoDeleteCall()
    {
        var (exit, _) = await Run($"{{\"records\":[\"{Child1}\"],\"records\":[\"{Child2}\"]}}", ".json");

        Assert.Equal(ExitCodes.InvalidArguments, exit);
        Assert.Empty(_deleteCalls);
    }
}
