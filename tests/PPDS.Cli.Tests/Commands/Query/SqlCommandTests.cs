using System.Linq;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.TestHelpers;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Dataverse.Query.Planning.Nodes;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

/// <summary>
/// CLI unit tests for the SQL command structure and argument parsing.
/// Tests command metadata, option definitions, input source validation,
/// and flag parsing. Does not execute real queries.
/// </summary>
[Trait("Category", "Unit")]
public class SqlCommandStructureTests
{
    private readonly Command _command;

    public SqlCommandStructureTests()
    {
        _command = SqlCommand.Create();
    }

    #region Command Structure

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("sql", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithCorrectDescription()
    {
        Assert.Equal("Execute a SQL query against Dataverse (transpiled to FetchXML)", _command.Description);
    }

    [Fact]
    public void Create_HasSqlArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "sql");
        Assert.NotNull(argument);
    }

    [Fact]
    public void Create_SqlArgumentHasZeroOrOneArity()
    {
        var argument = _command.Arguments.First(a => a.Name == "sql");
        Assert.Equal(ArgumentArity.ZeroOrOne, argument.Arity);
    }

    [Fact]
    public void Create_HasFileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--file");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasStdinOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--stdin");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasShowFetchXmlOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--show-fetchxml");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasExplainOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--explain");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasTdsOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--tds");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasConfirmOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--confirm");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasDryRunOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--dry-run");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasNoLimitOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--no-limit");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasProfileOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--profile");
        Assert.NotNull(option);
        Assert.Contains("-p", option!.Aliases);
    }

    [Fact]
    public void Create_HasEnvironmentOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--environment");
        Assert.NotNull(option);
        Assert.Contains("-env", option!.Aliases);
    }

    [Fact]
    public void Create_HasTopOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--top");
        Assert.NotNull(option);
        Assert.Contains("-t", option!.Aliases);
    }

    [Fact]
    public void Create_HasPageOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--page");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasPagingCookieOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--paging-cookie");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasCountOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--count");
        Assert.NotNull(option);
        Assert.Contains("-c", option!.Aliases);
    }

    [Fact]
    public void Create_HasOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasVerboseOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
    }

    [Fact]
    public void Create_HasDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    #endregion

    #region Argument Parsing

    [Fact]
    public void Parse_WithSqlArgument_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\"");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithoutAnyInputSource_HasValidationError()
    {
        var result = _command.Parse("");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithFileOption_Succeeds()
    {
        var result = _command.Parse("--file query.sql");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithStdinOption_Succeeds()
    {
        var result = _command.Parse("--stdin");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMultipleInputSources_SqlAndFile_HasValidationError()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --file query.sql");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithMultipleInputSources_SqlAndStdin_HasValidationError()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --stdin");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithMultipleInputSources_FileAndStdin_HasValidationError()
    {
        var result = _command.Parse("--file query.sql --stdin");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithShowFetchXmlFlag_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --show-fetchxml");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithExplainFlag_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --explain");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithTdsFlag_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --tds");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithConfirmFlag_Succeeds()
    {
        var result = _command.Parse("\"DELETE FROM account WHERE statecode = 1\" --confirm");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRunFlag_Succeeds()
    {
        var result = _command.Parse("\"DELETE FROM account WHERE statecode = 1\" --dry-run");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDryRunAndCsv_HasValidationError()
    {
        var result = _command.Parse(
            "\"DELETE FROM account WHERE statecode = 1\" --dry-run --output-format Csv");

        var error = Assert.Single(result.Errors);
        Assert.Contains("CSV output is not supported", error.Message);
        Assert.Contains("--dry-run", error.Message);
    }

    [Fact]
    public void Parse_WithNoLimitFlag_Succeeds()
    {
        var result = _command.Parse("\"DELETE FROM account WHERE statecode = 1\" --no-limit");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithOutputFormatJson_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMutuallyExclusiveVerbosity_HasError()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --verbose --debug");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_WithProfileOption_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --profile dev");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithEnvironmentOption_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --environment https://org.crm.dynamics.com");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithTopOption_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --top 10");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithPageOption_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --page 2");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithCountOption_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --count");
        Assert.Empty(result.Errors);
    }

    #endregion
}

/// <summary>
/// Tests for SQL command DML exit code logic and error mapping.
/// </summary>
[Trait("Category", "Unit")]
[Collection(nameof(ConsoleCaptureCollection))]
public class SqlCommandTests
{
    #region DML Dry-Run Output

    [Fact]
    public void WriteDryRunOutput_Json_IncludesPlanFetchXmlAndExecutionGate()
    {
        using var output = new StringWriter();
        var writer = new JsonOutputWriter(output);
        var result = CreateDryRunResult();

        SqlCommand.WriteDryRunOutput(result, OutputFormat.Json, writer);

        using var json = JsonDocument.Parse(output.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal("UPDATE account SET name = 'preview' WHERE accountid = 'id'",
            data.GetProperty("sql").GetString());
        Assert.Equal("<fetch />", data.GetProperty("fetchXml").GetString());
        Assert.Equal(DmlSafetyGuard.DefaultRowCap, data.GetProperty("rowCap").GetInt32());
        Assert.True(data.GetProperty("requiresConfirmationForExecution").GetBoolean());
        Assert.Equal("DmlExecuteNode", data.GetProperty("plan").GetProperty("nodeType").GetString());
    }

    [Fact]
    public void WriteDryRunOutput_Csv_IsRejectedAsNonTabular()
    {
        var exception = Assert.Throws<PpdsValidationException>(() =>
            SqlCommand.WriteDryRunOutput(
                CreateDryRunResult(), OutputFormat.Csv, new TextOutputWriter()));

        Assert.Contains("CSV output is not supported", exception.Message);
    }

    [Fact]
    public void WriteDryRunOutput_Text_WritesPlanToStdoutAndStatusToStderr()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;

        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            SqlCommand.WriteDryRunOutput(
                CreateDryRunResult(), OutputFormat.Text, new TextOutputWriter());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Contains("Execution Plan:", stdout.ToString());
        Assert.Contains("DmlExecute: UPDATE account", stdout.ToString());
        Assert.Contains("Planned FetchXML:", stdout.ToString());
        Assert.Contains("No Dataverse records were changed", stderr.ToString());
        Assert.Contains("Actual execution requires --confirm", stderr.ToString());
    }

    private static SqlQueryResult CreateDryRunResult() => new()
    {
        OriginalSql = "UPDATE account SET name = 'preview' WHERE accountid = 'id'",
        TranspiledFetchXml = "<fetch />",
        Result = QueryResult.Empty("dry-run"),
        DmlSafetyResult = new DmlSafetyResult
        {
            ContainsDml = true,
            IsDryRun = true,
            RequiresConfirmation = true,
            RowCap = DmlSafetyGuard.DefaultRowCap
        },
        DryRunPlan = new QueryPlanDescription
        {
            NodeType = "DmlExecuteNode",
            Description = "DmlExecute: UPDATE account"
        }
    };

    #endregion

    #region DML failure reporting (WriteResult)

    private static DmlStatementOutcome Stmt(DmlOperation op, string entity, long ok, long failed, string? error = null)
        => new(op, entity, ok, failed, error);

    private static SqlQueryResult DmlQueryResult(params DmlStatementOutcome[] statements) => new()
    {
        OriginalSql = "UPDATE x",
        TranspiledFetchXml = null,
        Result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[] { new QueryColumn { LogicalName = "affected_rows" }, new QueryColumn { LogicalName = "failed_rows" } },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["affected_rows"] = QueryValue.Simple(statements.Sum(x => x.AffectedRows)),
                    ["failed_rows"] = QueryValue.Simple(statements.Sum(x => x.FailedRows))
                }
            },
            Count = 1
        },
        DmlOutcome = DmlOutcome.From(statements)
    };

    private static (int exit, string stdout, string stderr) Run(SqlQueryResult result, OutputFormat format)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            IOutputWriter writer = format == OutputFormat.Json ? new JsonOutputWriter(stdout) : new TextOutputWriter();
            var exit = SqlCommand.WriteResult(result, format, verbose: false, writer, fetchXml: null, pagingHint: null);
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public void WriteResult_Json_AllRowsFailed_IsNotSuccess_WithTopLevelErrorResultsAndUnchangedData()
    {
        var (exit, stdout, stderr) = Run(
            DmlQueryResult(Stmt(DmlOperation.Insert, "solution", 0, 1, "The 'CreateMultiple' method does not support entities of type 'solution'.")),
            OutputFormat.Json);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Equal(string.Empty, stderr);
        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(ErrorCodes.Query.ExecutionFailed, root.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("does not support entities of type 'solution'", root.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(1, root.GetProperty("results").GetArrayLength());
        Assert.False(root.GetProperty("results")[0].GetProperty("success").GetBoolean());
        // data shape is unchanged: the QueryResult with its single summary row
        var record = root.GetProperty("data").GetProperty("records")[0];
        Assert.Equal(0, record.GetProperty("affected_rows").GetProperty("value").GetInt64());
        Assert.Equal(1, record.GetProperty("failed_rows").GetProperty("value").GetInt64());
    }

    [Fact]
    public void WriteResult_Json_Partial_UsesPartialFailureCodeAndExitOne()
    {
        var (exit, stdout, _) = Run(DmlQueryResult(Stmt(DmlOperation.Update, "account", 45, 5, "boom")), OutputFormat.Json);

        Assert.Equal(ExitCodes.PartialSuccess, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(ErrorCodes.Operation.PartialFailure, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("45 row(s) affected", message);
        Assert.Contains("5 row(s) failed", message);
    }

    [Theory]
    [InlineData(DmlOperation.Insert)]
    [InlineData(DmlOperation.Update)]
    [InlineData(DmlOperation.Delete)]
    public void WriteResult_Json_NoFailures_IsSuccess_ExitZero(DmlOperation op)
    {
        var (exit, stdout, _) = Run(DmlQueryResult(Stmt(op, "account", 3, 0)), OutputFormat.Json);

        Assert.Equal(ExitCodes.Success, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("error", out _));
    }

    [Theory]
    [InlineData(OutputFormat.Text)]
    [InlineData(OutputFormat.Csv)]
    public void WriteResult_TextAndCsv_FailureGoesToStderrNotStdout(OutputFormat format)
    {
        var (exit, stdout, stderr) = Run(DmlQueryResult(Stmt(DmlOperation.Delete, "account", 0, 2, "plugin threw")), format);

        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("Error: DML failed (DELETE account): all 2 row(s) failed.", stderr);
        Assert.Contains("plugin threw", stderr);
        Assert.DoesNotContain("plugin threw", stdout);
        Assert.Contains("affected_rows", stdout);
    }

    [Fact]
    public void WriteResult_NonDmlResult_IsSuccess_EvenWithDmlShapedColumns()
    {
        // A SELECT that happens to alias columns affected_rows/failed_rows has no DmlOutcome.
        var result = DmlQueryResult(Stmt(DmlOperation.Update, "account", 0, 4));
        var select = new SqlQueryResult { OriginalSql = "SELECT", TranspiledFetchXml = null, Result = result.Result };

        var (exit, stdout, _) = Run(select, OutputFormat.Json);

        Assert.Equal(ExitCodes.Success, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void WriteResult_Script_EarlierStatementFailed_LaterSucceeded_IsStillAFailure()
    {
        var result = DmlQueryResult(
            Stmt(DmlOperation.Update, "account", 0, 3, "plugin X threw"),
            Stmt(DmlOperation.Update, "contact", 2, 0));

        var (exit, stdout, _) = Run(result, OutputFormat.Json);

        Assert.Equal(ExitCodes.PartialSuccess, exit);
        using var json = JsonDocument.Parse(stdout);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        var message = json.RootElement.GetProperty("error").GetProperty("message").GetString();
        Assert.Contains("#1 UPDATE account: 0 affected, 3 failed", message);
        Assert.Contains("plugin X threw", message);
        Assert.DoesNotContain("#2", message);
    }

    #endregion

    #region --top and DML

    [Fact]
    public void BuildRequest_MarksAnExplicitTopAsUserSpecified()
    {
        var withTop = SqlCommand.BuildRequest("DELETE FROM account WHERE name = 'x'", 5, null, null, false, false, new DmlSafetyOptions());
        var withoutTop = SqlCommand.BuildRequest("DELETE FROM account WHERE name = 'x'", null, null, null, false, false, new DmlSafetyOptions());

        Assert.True(withTop.TopIsUserSpecified);
        Assert.False(withoutTop.TopIsUserSpecified);
    }

    [Fact]
    public void TopHelp_SaysItDoesNotApplyToDml()
    {
        Assert.Contains("INSERT, UPDATE or DELETE", QueryCommandGroup.TopOption.Description);
        Assert.Contains("INSERT, UPDATE and DELETE", ExecuteTopDescription());
    }

    private static string ExecuteTopDescription()
        => PPDS.Cli.Commands.Query.History.ExecuteCommand.Create().Options.Single(o => o.Name == "--top").Description!;

    #endregion

    #region Error Handling

    [Fact]
    public void ErrorHandling_QueryParseException_ProducesStructuredError()
    {
        var ex = new QueryParseException("Unexpected token 'INVALID'");
        var error = StructuredError.Create(
            "SQL_PARSE_ERROR",
            ex.Message,
            target: null,
            debug: false);

        Assert.Equal("SQL_PARSE_ERROR", error.Code);
        Assert.Contains("Unexpected token", error.Message);
    }

    [Fact]
    public void ErrorHandling_GenericException_MapsViaExceptionMapper()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var error = ExceptionMapper.Map(ex, context: "executing SQL query", debug: true);

        Assert.Equal(ErrorCodes.Operation.Internal, error.Code);
        Assert.Contains("Something went wrong", error.Message);
        Assert.NotNull(error.Details);
    }

    [Fact]
    public void ErrorHandling_QueryExecutionException_MapsToFailureExitCode()
    {
        var ex = new QueryExecutionException(QueryErrorCode.ParseError, "Parse failed");
        var exitCode = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.Failure, exitCode);
    }

    [Fact]
    public void ErrorHandling_QueryExecutionException_MapsToStructuredError()
    {
        var ex = new QueryExecutionException(QueryErrorCode.ExecutionFailed, "Execution failed");
        var error = ExceptionMapper.Map(ex, context: "executing SQL query");

        Assert.Equal(QueryErrorCode.ExecutionFailed, error.Code);
        Assert.Contains("Execution failed", error.Message);
    }

    #endregion
}
