using System.CommandLine;
using System.IO.Compression;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure.Errors;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Data;

/// <summary>
/// The whole <c>data import</c> and <c>data copy</c> commands, run with no profile: a schema the
/// import cannot apply is refused with exit 3 before anything connects (#1934 r3 should-fix 1). These
/// pin the commands' own call sites: the pre-connect schema check, and the outer catch's exit code.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ImportCopySchemaCheckCommandTests : IDisposable
{
    private const string SchemaWithSkipUpdate =
        @"<?xml version=""1.0""?><entities><entity name=""account"" displayname=""Account"" primaryidfield=""accountid"" skipupdate=""true""><fields><field name=""accountid"" displayname=""Account"" type=""uniqueidentifier"" primaryKey=""true"" /></fields></entity></entities>";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ppds-schema-cmd-" + Guid.NewGuid().ToString("N")[..8]);

    public ImportCopySchemaCheckCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task DataImport_ArchiveWhoseSchemaTheImportCannotApply_Exits3_BeforeConnecting()
    {
        var archive = Path.Combine(_dir, "data.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(zip.CreateEntry("data_schema.xml").Open());
            writer.Write(SchemaWithSkipUpdate);
        }

        // No profile and no environment: had the command connected, it would fail with another code.
        var exit = await ImportCommand.Create().Parse(new[] { "--data", archive }).InvokeAsync();

        Assert.Equal(ExitCodes.InvalidArguments, exit);
    }

    [Fact]
    public async Task DataCopy_SchemaFileTheImportCannotApply_Exits3_BeforeConnecting()
    {
        var schema = Path.Combine(_dir, "schema.xml");
        File.WriteAllText(schema, SchemaWithSkipUpdate);

        var exit = await CopyCommand.Create()
            .Parse(new[] { "--schema", schema, "--source-env", "source", "--target-env", "target" })
            .InvokeAsync();

        Assert.Equal(ExitCodes.InvalidArguments, exit);
    }
}
