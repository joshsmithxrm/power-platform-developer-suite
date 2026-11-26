using FluentAssertions;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.StackDepth;

/// <summary>
/// #1952: a statement too deep or too large to process is a coded refusal on every surface: the service throws
/// <c>Query.NestedTooDeeply</c>, the CLI exits 3, and the message never contains the statement. A small parse
/// thread makes the bound tiny, so these inputs are refused well inside the thread's stack.
/// </summary>
public sealed class StackRefusalMappingTests
{
    private const int SmallStack = 2 << 20;

    private static readonly string OverTheBound =
        "SELECT name FROM account WHERE name IN (" +
        string.Join(", ", Enumerable.Range(0, (int)BoundedSqlParser.MaxTokens(SmallStack) + 10).Select(i => $"secret{i}")) + ")";

    private static SqlQueryService Service() =>
        new(new Mock<IQueryExecutor>().Object, guard: new InactiveFakeShakedownGuard());

    [Fact]
    public async Task Execute_refuses_with_the_coded_error_and_no_text_from_the_statement()
    {
        var task = QueryStack.Run(() => Service().ExecuteAsync(new SqlQueryRequest { Sql = OverTheBound }), SmallStack);

        var refusal = (await FluentActions.Awaiting(() => task).Should().ThrowAsync<PpdsException>()).Which;
        refusal.ErrorCode.Should().Be(ErrorCodes.Query.NestedTooDeeply);
        refusal.UserMessage.Should().Be(QueryNestedTooDeeplyException.DefaultMessage).And.NotContain("secret");
        ExceptionMapper.ToExitCode(refusal).Should().Be(ExitCodes.InvalidArguments);
    }

    [Fact]
    public async Task Explain_refuses_with_the_coded_error()
    {
        var task = QueryStack.Run(() => Service().ExplainAsync(OverTheBound), SmallStack);

        (await FluentActions.Awaiting(() => task).Should().ThrowAsync<PpdsException>())
            .Which.ErrorCode.Should().Be(ErrorCodes.Query.NestedTooDeeply);
    }

    [Fact]
    public void Transpile_refuses_with_the_coded_error()
    {
        var act = () => QueryStack.Run(() => Service().TranspileSql(OverTheBound), SmallStack);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Query.NestedTooDeeply);
    }

    [Fact]
    public void Library_and_runtime_refusals_map_to_the_code_and_exit_3()
    {
        var library = new QueryNestedTooDeeplyException(QueryDepthLimit.TokenCount);
        var runtime = new InsufficientExecutionStackException("runtime text");

        ExceptionMapper.ToExitCode(library).Should().Be(ExitCodes.InvalidArguments);
        ExceptionMapper.ToExitCode(runtime).Should().Be(ExitCodes.InvalidArguments);
        ExceptionMapper.Map(library).Code.Should().Be(ErrorCodes.Query.NestedTooDeeply);
        var mapped = ExceptionMapper.Map(runtime);
        mapped.Code.Should().Be(ErrorCodes.Query.NestedTooDeeply);
        mapped.Message.Should().Be(QueryNestedTooDeeplyException.DefaultMessage);
    }

    [Fact]
    public void Service_and_library_codes_are_the_same()
    {
        QueryErrorCode.NestedTooDeeply.Should().Be(ErrorCodes.Query.NestedTooDeeply);
    }
}
