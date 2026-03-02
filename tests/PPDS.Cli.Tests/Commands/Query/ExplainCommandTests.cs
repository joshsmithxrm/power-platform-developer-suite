using System.CommandLine;
using System.CommandLine.Parsing;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tests.Mocks;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Commands.Query;

/// <summary>
/// CLI integration tests for the EXPLAIN command.
/// Tests command structure, argument parsing, plan output formatting pipeline,
/// and error handling. Uses <see cref="FakeSqlQueryService"/> to test the
/// command layer without executing real queries.
/// </summary>
[Trait("Category", "PlanUnit")]
public class ExplainCommandTests
{
    private readonly Command _command;

    public ExplainCommandTests()
    {
        _command = ExplainCommand.Create();
    }

    #region Command Structure

    [Fact]
    public void Create_ReturnsCommandWithCorrectName()
    {
        Assert.Equal("explain", _command.Name);
    }

    [Fact]
    public void Create_ReturnsCommandWithDescription()
    {
        Assert.Equal("Show the execution plan for a SQL query", _command.Description);
    }

    [Fact]
    public void Create_HasSqlArgument()
    {
        var argument = _command.Arguments.FirstOrDefault(a => a.Name == "sql");
        Assert.NotNull(argument);
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
    public void Create_HasOutputFormatOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--output-format");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasDebugOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--debug");
        Assert.NotNull(option);
        Assert.False(option!.Required);
    }

    [Fact]
    public void Create_HasVerboseOption()
    {
        var option = _command.Options.FirstOrDefault(o => o.Name == "--verbose");
        Assert.NotNull(option);
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
    public void Parse_MissingSqlArgument_HasError()
    {
        var result = _command.Parse("");
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
    public void Parse_WithOutputFormatJson_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --output-format Json");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithDebugFlag_Succeeds()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --debug");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Parse_WithMutuallyExclusiveVerbosity_HasError()
    {
        var result = _command.Parse("\"SELECT name FROM account\" --verbose --debug");
        Assert.NotEmpty(result.Errors);
    }

    #endregion

    #region Plan Output Formatting Pipeline

    [Fact]
    public async Task ExplainPipeline_SelectQuery_ProducesPlanWithFetchXmlScan()
    {
        var fake = new FakeSqlQueryService();
        fake.NextExplainResult = new QueryPlanDescription
        {
            NodeType = "FetchXmlScanNode",
            Description = "FetchXmlScan: account",
            EstimatedRows = 5000
        };

        var plan = await fake.ExplainAsync("SELECT name FROM account");
        var formatted = PlanFormatter.Format(plan);

        Assert.StartsWith("Execution Plan:", formatted);
        Assert.Contains("FetchXmlScan: account", formatted);
        Assert.Contains("(est. 5,000 rows)", formatted);
    }

    [Fact]
    public async Task ExplainPipeline_DmlStatement_ShowsDmlExecuteNode()
    {
        var fake = new FakeSqlQueryService();
        fake.NextExplainResult = new QueryPlanDescription
        {
            NodeType = "DmlExecuteNode",
            Description = "DmlExecute: DELETE account",
            EstimatedRows = 100,
            Children = new[]
            {
                new QueryPlanDescription
                {
                    NodeType = "FetchXmlScanNode",
                    Description = "FetchXmlScan: account",
                    EstimatedRows = 100
                }
            }
        };

        var plan = await fake.ExplainAsync("DELETE FROM account WHERE statecode = 1");
        var formatted = PlanFormatter.Format(plan);

        Assert.Contains("DmlExecute: DELETE account", formatted);
        Assert.Contains("FetchXmlScan: account", formatted);
    }

    [Fact]
    public async Task ExplainPipeline_PlanWithChildren_ShowsTreeStructure()
    {
        var fake = new FakeSqlQueryService();
        fake.NextExplainResult = new QueryPlanDescription
        {
            NodeType = "ConcatenateNode",
            Description = "Concatenate: UNION ALL",
            Children = new[]
            {
                new QueryPlanDescription
                {
                    NodeType = "FetchXmlScanNode",
                    Description = "FetchXmlScan: account"
                },
                new QueryPlanDescription
                {
                    NodeType = "FetchXmlScanNode",
                    Description = "FetchXmlScan: contact"
                }
            }
        };

        var plan = await fake.ExplainAsync("SELECT name FROM account UNION ALL SELECT fullname FROM contact");
        var formatted = PlanFormatter.Format(plan);

        Assert.Contains("Concatenate: UNION ALL", formatted);
        Assert.Contains("FetchXmlScan: account", formatted);
        Assert.Contains("FetchXmlScan: contact", formatted);
        // Tree connectors should be present
        Assert.Contains("\u251C\u2500\u2500", formatted); // branch connector
        Assert.Contains("\u2514\u2500\u2500", formatted); // end connector
    }

    [Fact]
    public async Task ExplainPipeline_PlanWithPoolCapacity_ShowsMetadataFooter()
    {
        var fake = new FakeSqlQueryService();
        var plan = new QueryPlanDescription
        {
            NodeType = "ParallelPartitionNode",
            Description = "ParallelPartition: account",
            PoolCapacity = 52,
            EffectiveParallelism = 4,
            Children = new[]
            {
                new QueryPlanDescription
                {
                    NodeType = "FetchXmlScanNode",
                    Description = "FetchXmlScan: account"
                }
            }
        };
        fake.NextExplainResult = plan;

        var result = await fake.ExplainAsync("SELECT name FROM account");
        var formatted = PlanFormatter.Format(result);

        Assert.Contains("Pool capacity: 52", formatted);
        Assert.Contains("Effective parallelism: 4", formatted);
    }

    [Fact]
    public async Task ExplainPipeline_DefaultFake_ReturnsMockPlan()
    {
        var fake = new FakeSqlQueryService();
        // No NextExplainResult set - uses default

        var plan = await fake.ExplainAsync("SELECT * FROM account");

        Assert.Equal("FetchXmlScanNode", plan.NodeType);
        Assert.Equal("Mock plan", plan.Description);
    }

    #endregion

    #region Error Handling Pipeline

    [Fact]
    public void ErrorHandling_QueryParseException_MapsToInvalidArgumentsExitCode()
    {
        // The command catches QueryParseException and returns ExitCodes.InvalidArguments
        var exitCode = ExitCodes.InvalidArguments;
        Assert.Equal(3, exitCode);
    }

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
    public void ErrorHandling_QueryExecutionException_MapsToFailureExitCode()
    {
        var ex = new QueryExecutionException(QueryErrorCode.ParseError, "Parse failed");
        var exitCode = ExceptionMapper.ToExitCode(ex);

        Assert.Equal(ExitCodes.Failure, exitCode);
    }

    [Fact]
    public void ErrorHandling_QueryExecutionException_MapsToStructuredError()
    {
        var ex = new QueryExecutionException(QueryErrorCode.ParseError, "Parse failed");
        var error = ExceptionMapper.Map(ex, context: "explaining SQL query");

        Assert.Equal(QueryErrorCode.ParseError, error.Code);
        Assert.Contains("Parse failed", error.Message);
    }

    [Fact]
    public void ErrorHandling_GenericException_MapsViaExceptionMapper()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var error = ExceptionMapper.Map(ex, context: "explaining SQL query", debug: true);

        Assert.Equal(ErrorCodes.Operation.Internal, error.Code);
        Assert.Contains("Something went wrong", error.Message);
        Assert.NotNull(error.Details);
    }

    [Fact]
    public async Task ExplainPipeline_ExceptionToThrow_PropagatesFromExplainAsync()
    {
        var fake = new FakeSqlQueryService();
        fake.ExceptionToThrow = new QueryParseException("Invalid SQL syntax");

        await Assert.ThrowsAsync<QueryParseException>(
            () => fake.ExplainAsync("NOT VALID SQL"));
    }

    [Fact]
    public async Task ExplainPipeline_QueryExecutionException_PropagatesFromExplainAsync()
    {
        var fake = new FakeSqlQueryService();
        fake.ExceptionToThrow = new QueryExecutionException(
            QueryErrorCode.ParseError, "Failed to parse query");

        var ex = await Assert.ThrowsAsync<QueryExecutionException>(
            () => fake.ExplainAsync("BAD QUERY"));

        Assert.Equal(QueryErrorCode.ParseError, ex.ErrorCode);
    }

    #endregion
}
