using System.CommandLine;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Commands.PluginTraces;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Schema;
using PPDS.Cli.Commands.Solutions;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Query;
using Xunit;

namespace PPDS.Cli.Tests.Infrastructure;

/// <summary>
/// Covers #1076 (invalid --output-format values produce a clean error instead of leaking
/// the CLR enum type name), #1078 (--output-format Csv is rejected on commands without
/// CSV rendering and still accepted on the four commands that implement it), and #1336
/// (help usage label and completions advertise only the values each instance accepts).
/// In the console-capture collection because <see cref="CaptureStdout"/> swaps the
/// process-global <see cref="Console.Out"/>.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public class GlobalOptionsOutputFormatTests
{
    private static string JoinedErrors(ParseResult result) =>
        string.Join(" | ", result.Errors.Select(e => e.Message));

    // ---- #1076: invalid values produce a clean message ----

    [Fact]
    public void InvalidValue_OnSharedOption_ProducesCleanMessage()
    {
        var result = SolutionsCommandGroup.Create().Parse("list --output-format Yaml");

        Assert.NotEmpty(result.Errors);
        Assert.Contains("Invalid value 'Yaml' for --output-format. Valid values: Text, Json.", JoinedErrors(result));
    }

    [Fact]
    public void InvalidValue_DoesNotLeakClrTypeName()
    {
        var result = SolutionsCommandGroup.Create().Parse("list --output-format Yaml");

        Assert.DoesNotContain("PPDS.Cli.Commands.OutputFormat", JoinedErrors(result));
        Assert.DoesNotContain("Cannot parse argument", JoinedErrors(result));
    }

    [Fact]
    public void InvalidValue_OnLocalOption_ProducesCleanMessage()
    {
        // env list declares its own option instance (factory-created, not the shared one).
        var result = EnvCommandGroup.Create().Parse("list -f Yaml");

        Assert.Contains("Invalid value 'Yaml' for --output-format. Valid values: Text, Json.", JoinedErrors(result));
        Assert.DoesNotContain("PPDS.Cli.Commands.OutputFormat", JoinedErrors(result));
    }

    [Fact]
    public void InvalidValue_OnCsvCapableCommand_ListsCsvAmongValidValues()
    {
        var result = QueryCommandGroup.Create().Parse("sql \"SELECT name FROM account\" --output-format Yaml");

        Assert.Contains("Invalid value 'Yaml' for --output-format. Valid values: Text, Json, Csv.", JoinedErrors(result));
    }

    [Fact]
    public void UndefinedNumericValue_IsRejected()
    {
        // Enum.TryParse would accept out-of-range numerics like 42; the parser must not.
        var result = SolutionsCommandGroup.Create().Parse("list --output-format 42");

        Assert.Contains("Invalid value '42' for --output-format", JoinedErrors(result));
    }

    [Fact]
    public void MissingValue_ReportsRequiredArgument_WithoutCrashing()
    {
        // --output-format with no value: arity enforcement reports the missing argument
        // before the custom parser dereferences the (empty) token list. Must not throw.
        var result = SolutionsCommandGroup.Create().Parse("list --output-format");

        Assert.NotEmpty(result.Errors);
        Assert.Contains("Required argument missing", JoinedErrors(result));
    }

    // ---- #1078: Csv is rejected on commands without CSV rendering ----

    [Fact]
    public void Csv_OnSolutionsList_IsRejectedWithCommandPath()
    {
        var result = SolutionsCommandGroup.Create().Parse("list --output-format Csv");

        Assert.NotEmpty(result.Errors);
        Assert.Contains("CSV output is not supported for 'solutions list'. Use --output-format Json or Text.", JoinedErrors(result));
    }

    [Fact]
    public void Csv_OnEnvList_IsRejectedWithCommandPath()
    {
        var result = EnvCommandGroup.Create().Parse("list -f Csv");

        Assert.Contains("CSV output is not supported for 'env list'. Use --output-format Json or Text.", JoinedErrors(result));
    }

    [Fact]
    public void Csv_IsRejectedCaseInsensitively()
    {
        var result = EnvCommandGroup.Create().Parse("list -f CSV");

        Assert.Contains("CSV output is not supported for 'env list'.", JoinedErrors(result));
    }

    [Fact]
    public void Csv_OnDataLoad_IsRejected_ViaOAlias()
    {
        var result = DataCommandGroup.Create().Parse("load --entity account --file test.csv -o Csv");

        Assert.Contains("CSV output is not supported for 'data load'. Use --output-format Json or Text.", JoinedErrors(result));
    }

    [Fact]
    public void SchemaCompare_Csv_KeepsExistingRejectionMessage()
    {
        // The hand-rolled #1078 rejection in CompareCommand was subsumed by the shared
        // option validator; the message must remain byte-identical.
        var result = SchemaCommandGroup.Create().Parse("compare --data pkg.zip --environment https://x.crm.dynamics.com -f Csv");

        Assert.Contains("CSV output is not supported for 'schema compare'. Use --output-format Json or Text.", JoinedErrors(result));
    }

    // ---- #1078: the four CSV-capable commands still accept Csv ----

    [Fact]
    public void Csv_OnQuerySql_ParsesAndBindsCsv()
    {
        var result = QueryCommandGroup.Create().Parse("sql \"SELECT name FROM account\" --output-format Csv");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Csv, GlobalOptions.GetValues(result).OutputFormat);
    }

    [Theory]
    [InlineData("--explain")]
    [InlineData("--show-fetchxml")]
    public void Csv_OnQuerySql_WithExplainOrShowFetchXml_IsRejected(string subMode)
    {
        // These sub-modes render a plan / FetchXML blob, not a result set, so Csv must be
        // rejected rather than silently falling back to Text (#1078).
        var result = QueryCommandGroup.Create().Parse($"sql \"SELECT name FROM account\" {subMode} --output-format Csv");

        Assert.Contains(
            "CSV output is not supported with --explain or --show-fetchxml. Use --output-format Json or Text.",
            JoinedErrors(result));
    }

    [Fact]
    public void InvalidValue_OnQuerySql_WithExplain_StillReportsCleanMessage_NoCrash()
    {
        // The sub-mode validator must not throw when --output-format itself failed to parse.
        var result = QueryCommandGroup.Create().Parse("sql \"SELECT name FROM account\" --explain --output-format Yaml");

        Assert.Contains("Invalid value 'Yaml' for --output-format", JoinedErrors(result));
    }

    [Fact]
    public void Csv_OnQueryFetch_Parses()
    {
        var result = QueryCommandGroup.Create().Parse("fetch \"<fetch><entity name='account'/></fetch>\" -f Csv");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Csv, GlobalOptions.GetValues(result).OutputFormat);
    }

    [Fact]
    public void Csv_OnQueryHistoryExecute_Parses()
    {
        var result = QueryCommandGroup.Create().Parse("history execute 1 -f Csv");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Csv, GlobalOptions.GetValues(result).OutputFormat);
    }

    [Fact]
    public void Csv_OnPluginTracesList_Parses()
    {
        var result = PluginTracesCommandGroup.Create().Parse("list --output-format Csv");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Csv, GlobalOptions.GetValues(result).OutputFormat);
    }

    // ---- valid values keep parsing (case-insensitive, defaults intact) ----

    [Theory]
    [InlineData("Text", OutputFormat.Text)]
    [InlineData("text", OutputFormat.Text)]
    [InlineData("Json", OutputFormat.Json)]
    [InlineData("json", OutputFormat.Json)]
    [InlineData("JSON", OutputFormat.Json)]
    public void ValidValues_ParseCaseInsensitively(string token, OutputFormat expected)
    {
        var result = SolutionsCommandGroup.Create().Parse($"list --output-format {token}");

        Assert.Empty(result.Errors);
        Assert.Equal(expected, GlobalOptions.GetValues(result).OutputFormat);
    }

    [Fact]
    public void OmittedOption_DefaultsToText()
    {
        var result = SolutionsCommandGroup.Create().Parse("list");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Text, GlobalOptions.GetValues(result).OutputFormat);
    }

    [Fact]
    public void DataLoad_OutputFormatKeepsOAlias_FBelongsToFile()
    {
        var loadCommand = LoadCommand.Create();
        var formatOption = loadCommand.Options.First(o => o.Name == "--output-format");

        Assert.Contains("-o", formatOption.Aliases);
        Assert.DoesNotContain("-f", formatOption.Aliases);
    }

    [Fact]
    public void GetValues_ResolvesCommandLocalOutputFormatInstance()
    {
        // Commands like `auth list` / `env list` declare their own --output-format instance
        // (not the shared statics). GetValues must still resolve it — without the name-based
        // fallback this returns the default Text (the bug Gemini flagged on GetOutputFormatValue).
        var command = new Command("probe") { GlobalOptions.CreateOutputFormatOption() };
        GlobalOptions.AddToCommand(command, includeOutputFormat: false);

        var result = command.Parse("--output-format Json");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Json, GlobalOptions.GetValues(result).OutputFormat);
    }

    [Fact]
    public void GetValues_LocalInstanceOmitted_DefaultsToText()
    {
        var command = new Command("probe") { GlobalOptions.CreateOutputFormatOption() };
        GlobalOptions.AddToCommand(command, includeOutputFormat: false);

        var result = command.Parse("");

        Assert.Empty(result.Errors);
        Assert.Equal(OutputFormat.Text, GlobalOptions.GetValues(result).OutputFormat);
    }

    // ---- #1336: help + completions advertise only the values each instance accepts ----

    [Fact]
    public void Help_OnCsvRejectingCommand_DoesNotAdvertiseCsv()
    {
        var help = RenderHelp(SolutionsCommandGroup.Create(), "solutions list --help");

        Assert.Contains("--output-format <Text|Json>", help);
        Assert.DoesNotContain("Csv", help);
    }

    [Fact]
    public void Help_OnCsvCapableCommand_AdvertisesCsv()
    {
        var help = RenderHelp(QueryCommandGroup.Create(), "query sql --help");

        Assert.Contains("--output-format <Text|Json|Csv>", help);
    }

    [Fact]
    public void Completions_OnCsvRejectingCommand_OmitCsv()
    {
        const string commandLine = "list --output-format ";
        var completions = SolutionsCommandGroup.Create().Parse(commandLine).GetCompletions(commandLine.Length);

        Assert.Equal(new[] { "Json", "Text" }, completions.Select(c => c.Label));
    }

    [Fact]
    public void Completions_OnCsvCapableCommand_IncludeCsv()
    {
        const string commandLine = "sql --output-format ";
        var completions = QueryCommandGroup.Create().Parse(commandLine).GetCompletions(commandLine.Length);

        Assert.Equal(new[] { "Csv", "Json", "Text" }, completions.Select(c => c.Label));
    }

    /// <summary>
    /// Renders --help for a command in-process. The command group is mounted on a
    /// <see cref="RootCommand"/> because --help is the root's recursive <c>HelpOption</c>
    /// (mirroring Program.cs); standalone command groups have no help option.
    /// </summary>
    private static string RenderHelp(Command commandGroup, string commandLine)
    {
        var root = new RootCommand();
        root.Subcommands.Add(commandGroup);

        using var output = new StringWriter();
        var exitCode = root.Parse(commandLine).Invoke(new InvocationConfiguration { Output = output });

        Assert.Equal(0, exitCode);
        return output.ToString();
    }

    // ---- CSV emission still works for the shared query formatter ----

    [Fact]
    public void QueryResultFormatter_WriteCsvOutput_EmitsHeaderAndEscapedRows()
    {
        var queryResult = new QueryResult
        {
            EntityLogicalName = "account",
            Columns =
            [
                new QueryColumn { LogicalName = "name" },
                new QueryColumn { LogicalName = "revenue", Alias = "total" }
            ],
            Records =
            [
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso, Ltd \"HQ\""),
                    ["total"] = QueryValue.WithFormatting(1000.50m, "$1,000.50")
                }
            ],
            Count = 1,
            MoreRecords = false
        };

        var csv = CaptureStdout(() => QueryResultFormatter.WriteCsvOutput(queryResult));

        Assert.Contains("name,total", csv);
        Assert.Contains("\"Contoso, Ltd \"\"HQ\"\"\"", csv);
        Assert.Contains("\"$1,000.50\"", csv);
    }

    // ---- #1078: empty CSV results emit the header, never silent zero bytes ----

    [Fact]
    public void QueryResultFormatter_WriteCsvOutput_EmptyResult_EmitsHeaderNotZeroBytes()
    {
        var csv = CaptureStdout(() => QueryResultFormatter.WriteCsvOutput(EmptyResultWithColumns()));

        Assert.Equal("name,total", csv.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void FetchCommand_WriteCsvOutput_EmptyResult_EmitsHeaderNotZeroBytes()
    {
        var csv = CaptureStdout(() => FetchCommand.WriteCsvOutput(EmptyResultWithColumns()));

        Assert.Equal("name,total", csv.TrimEnd('\r', '\n'));
    }

    private static QueryResult EmptyResultWithColumns() => new()
    {
        EntityLogicalName = "account",
        Columns =
        [
            new QueryColumn { LogicalName = "name" },
            new QueryColumn { LogicalName = "revenue", Alias = "total" }
        ],
        Records = [],
        Count = 0,
        MoreRecords = false
    };

    private static string CaptureStdout(Action action)
    {
        var originalOut = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            action();
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return captured.ToString();
    }
}
