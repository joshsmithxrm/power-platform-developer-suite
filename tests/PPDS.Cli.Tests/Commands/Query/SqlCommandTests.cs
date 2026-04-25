using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
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
public class SqlCommandTests
{
    #region DML Exit Code Logic

    [Fact]
    public void DmlExitCode_NormalSelectResults_ReturnsSuccess()
    {
        // Normal SELECT result with multiple rows - not DML shape
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "name" },
                new QueryColumn { LogicalName = "accountid" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso"),
                    ["accountid"] = QueryValue.Simple(Guid.NewGuid())
                },
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Fabrikam"),
                    ["accountid"] = QueryValue.Simple(Guid.NewGuid())
                }
            },
            Count = 2
        };

        // DmlExitCode returns Success when Records.Count != 1
        Assert.NotEqual(1, result.Records.Count);
        // The exit code would be ExitCodes.Success (0)
        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void DmlExitCode_DmlWithZeroFailedRows_ReturnsSuccess()
    {
        // DML result: exactly 1 row, exactly 2 columns: affected_rows and failed_rows
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "affected_rows" },
                new QueryColumn { LogicalName = "failed_rows" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["affected_rows"] = QueryValue.Simple(50L),
                    ["failed_rows"] = QueryValue.Simple(0L)
                }
            },
            Count = 1
        };

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void DmlExitCode_DmlWithFailedAndSucceeded_ReturnsPartialSuccess()
    {
        // DML result with both failed_rows > 0 and affected_rows > 0
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "affected_rows" },
                new QueryColumn { LogicalName = "failed_rows" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["affected_rows"] = QueryValue.Simple(45L),
                    ["failed_rows"] = QueryValue.Simple(5L)
                }
            },
            Count = 1
        };

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.PartialSuccess, exitCode);
    }

    [Fact]
    public void DmlExitCode_DmlWithOnlyFailures_ReturnsFailure()
    {
        // DML result with failed_rows > 0 and affected_rows = 0
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "affected_rows" },
                new QueryColumn { LogicalName = "failed_rows" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["affected_rows"] = QueryValue.Simple(0L),
                    ["failed_rows"] = QueryValue.Simple(10L)
                }
            },
            Count = 1
        };

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Failure, exitCode);
    }

    [Fact]
    public void DmlExitCode_SingleRowWithOnlyOneColumn_ReturnsSuccess()
    {
        // Single row but only 1 column - not DML shape
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "name" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso")
                }
            },
            Count = 1
        };

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void DmlExitCode_SingleRowTwoColumnsButNotDmlShape_ReturnsSuccess()
    {
        // Single row, 2 columns, but not named affected_rows/failed_rows
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "name" },
                new QueryColumn { LogicalName = "revenue" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["name"] = QueryValue.Simple("Contoso"),
                    ["revenue"] = QueryValue.Simple(1000000L)
                }
            },
            Count = 1
        };

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void DmlExitCode_EmptyResult_ReturnsSuccess()
    {
        var result = QueryResult.Empty("account");

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

    [Fact]
    public void DmlExitCode_FailedRowsNotLong_ReturnsSuccess()
    {
        // DML shape but failed_rows value is not a long (e.g., string)
        var result = new QueryResult
        {
            EntityLogicalName = "account",
            Columns = new[]
            {
                new QueryColumn { LogicalName = "affected_rows" },
                new QueryColumn { LogicalName = "failed_rows" }
            },
            Records = new[]
            {
                new Dictionary<string, QueryValue>
                {
                    ["affected_rows"] = QueryValue.Simple(10L),
                    ["failed_rows"] = QueryValue.Simple("not-a-number")
                }
            },
            Count = 1
        };

        var exitCode = SqlCommand.DmlExitCode(result);
        Assert.Equal(ExitCodes.Success, exitCode);
    }

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
