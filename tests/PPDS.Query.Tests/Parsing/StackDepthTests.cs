using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Sql.Intellisense;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using PPDS.Query.Planning.Nodes;
using Xunit;

namespace PPDS.Query.Tests.Parsing;

// #1952: the large-stack thread, the parser's bound and the coded refusal. Deep texts are tested in a child
// process (PPDS.Cli.Tests StackDepth); here a small parse thread makes the bound tiny, so every input is
// refused or parsed well inside the thread's stack.

public sealed class QueryStackTests
{
    private const int MiB = 1 << 20;

    [Fact]
    public void Run_uses_a_thread_with_the_requested_stack_and_reports_it()
    {
        QueryStack.CurrentThreadStackSize.Should().Be(0);
        QueryStack.Run(() => QueryStack.CurrentThreadStackSize, 8 * MiB).Should().Be(8 * MiB);
        QueryStack.Run(() => QueryStack.CurrentThreadStackSize).Should().Be(QueryStack.DefaultStackSize);
    }

    [Fact]
    public void Nested_run_stays_on_the_same_thread()
    {
        var (outer, inner) = QueryStack.Run(() =>
            (Environment.CurrentManagedThreadId, QueryStack.Run(() => Environment.CurrentManagedThreadId)), 8 * MiB);

        inner.Should().Be(outer);
        outer.Should().NotBe(Environment.CurrentManagedThreadId);
    }

    [Fact]
    public void Run_rethrows_the_work_exception_on_the_caller()
    {
        var act = () => QueryStack.Run<int>(() => throw new InvalidOperationException("from the parse thread"), 8 * MiB);

        act.Should().Throw<InvalidOperationException>().WithMessage("from the parse thread");
    }

    [Fact]
    public async Task RunAsync_continues_off_the_parse_thread()
    {
        var onParseThread = await QueryStack.RunAsync(() => QueryStack.CurrentThreadStackSize > 0);

        onParseThread.Should().BeTrue();
        QueryStack.CurrentThreadStackSize.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_cancellation_returns_at_once_while_the_work_finishes_on_its_thread()
    {
        using var release = new ManualResetEventSlim(false);
        using var cancel = new CancellationTokenSource();
        var task = QueryStack.RunAsync(() => release.Wait(TimeSpan.FromSeconds(30)), cancel.Token);

        cancel.Cancel();
        var act = async () => await task;

        await act.Should().ThrowAsync<OperationCanceledException>();
        release.Set();
    }

    [Fact]
    public void EnsureSufficientStack_refuses_with_the_coded_error_before_the_stack_ends()
    {
        var act = () => QueryStack.Run(() => Recurse(0), 1 * MiB);

        act.Should().Throw<QueryNestedTooDeeplyException>()
            .Which.Should().Match<QueryNestedTooDeeplyException>(e =>
                e.Limit == QueryDepthLimit.ExecutionStack && e.ErrorCode == QueryErrorCode.NestedTooDeeply);
    }

    [Fact]
    public void Runtime_stack_refusal_converts_to_the_coded_error()
    {
        var converted = QueryStack.AsNestedTooDeeply(new InsufficientExecutionStackException());

        QueryStack.IsDepthRefusal(new InsufficientExecutionStackException()).Should().BeTrue();
        converted.ErrorCode.Should().Be("Query.NestedTooDeeply");
        converted.Message.Should().Be(QueryNestedTooDeeplyException.DefaultMessage);
    }

    private static int Recurse(int depth)
    {
        QueryStack.EnsureSufficientStack();
        return Recurse(depth + 1) + 1;
    }
}

public sealed class BoundedSqlParserTests
{
    private const int MiB = 1 << 20;

    private static long Count(string sql)
    {
        var tokens = QueryStack.Run(() => BoundedSqlParser.GetTokenStream(new TSql160Parser(true), sql, out _));
        return BoundedSqlParser.CountTokens(tokens);
    }

    [Fact]
    public void Literals_commas_white_space_and_comments_are_not_counted()
    {
        Count("SELECT 1, 'a', N'b', 0x1, $1, 1.5, 1e1 /* note */ -- note\n FROM t").Should().Be(3,
            "only SELECT, FROM and t are counted");
    }

    [Fact]
    public void Names_keywords_operators_parentheses_and_quoted_names_are_counted()
    {
        Count("SELECT [a] + \"b\" FROM t WHERE (x = @v)").Should().Be(12);
    }

    [Theory]
    [InlineData("SELECT 1", 0)]
    [InlineData("/*/*", 2)]
    [InlineData("SELECT '/*' -- /*", 2)]
    [InlineData("/**/", 1)]
    public void Every_comment_open_is_counted_wherever_it_is(string sql, long expected)
    {
        BoundedSqlParser.CountCommentOpens(sql).Should().Be(expected);
    }

    [Fact]
    public void The_bound_is_the_stack_less_the_reserve_over_the_charge()
    {
        BoundedSqlParser.MaxTokens(16 * MiB).Should().Be(
            (long)(15 * MiB / (BoundedSqlParser.Margin * BoundedSqlParser.PlatformFactor * BoundedSqlParser.BytesPerToken)));
        BoundedSqlParser.MaxTokens(QueryStack.DefaultStackSize).Should().BeGreaterThan(77_000,
            "the default 1 GiB thread admits about 77,700 counted tokens even with the unmeasured-platform factor");
    }

    [Fact]
    public void A_text_one_token_over_the_bound_is_refused_and_one_at_it_parses()
    {
        var stack = 2 * MiB;
        var max = (int)BoundedSqlParser.MaxTokens(stack);
        // "SELECT a, a, ..., a" counts SELECT and every a.
        string Text(int names) => "SELECT " + string.Join(", ", Enumerable.Repeat("a", names));

        var at = QueryStack.Run(() => new QueryParser().TryParse(Text(max - 1), out _, out _), stack);
        var over = () => QueryStack.Run(() => new QueryParser().Parse(Text(max)), stack);

        at.Should().BeTrue();
        over.Should().Throw<QueryNestedTooDeeplyException>().Which.Limit.Should().Be(QueryDepthLimit.TokenCount);
    }

    [Fact]
    public void Too_many_comment_opens_are_refused_before_lexing()
    {
        var stack = 2 * MiB;
        var text = "SELECT 1 /*" + string.Concat(Enumerable.Repeat(" /*", (int)BoundedSqlParser.MaxCommentOpens(stack))) + " */";

        var act = () => QueryStack.Run(() => new QueryParser().Parse(text), stack);

        act.Should().Throw<QueryNestedTooDeeplyException>().Which.Limit.Should().Be(QueryDepthLimit.CommentNesting);
    }

    [Fact]
    public void A_large_flat_list_counts_almost_nothing_and_parses()
    {
        var sql = "SELECT name FROM account WHERE name IN (" + string.Join(", ", Enumerable.Repeat("'value'", 200_000)) + ")";

        Count(sql).Should().BeLessThan(20);
        new QueryParser().TryParse(sql, out _, out var errors).Should().BeTrue();
        errors.Should().BeEmpty();
    }

    [Theory]
    [InlineData("SELECT name FROM account WHERE name = 'unterminated")]
    [InlineData("SELECT name FROM account /* unterminated")]
    [InlineData("SELECT FROM WHERE")]
    [InlineData("SELECT name FROM account WHERE name = 'a'")]
    public void Results_and_errors_are_the_same_as_the_parser_alone(string sql)
    {
        var direct = new TSql160Parser(true).Parse(new StringReader(sql), out var directErrors);

        var ok = new QueryParser().TryParse(sql, out var fragment, out var errors);

        ok.Should().Be(directErrors.Count == 0);
        errors.Select(e => (e.Number, e.Offset, e.Message)).Should().Equal(directErrors.Select(e => (e.Number, e.Offset, e.Message)));
        (fragment == null).Should().Be(directErrors.Count > 0);
        (direct == null).Should().BeFalse();
    }
}

public sealed class IntellisenseStackDepthTests
{
    private const int MiB = 1 << 20;
    private static readonly int SmallStack = 2 * MiB;

    private static string OverTheTokenBound() =>
        "SELECT " + string.Join(", ", Enumerable.Repeat("a", (int)BoundedSqlParser.MaxTokens(SmallStack) + 10));

    private static string OverTheCommentBound() =>
        "SELECT 1 " + string.Concat(Enumerable.Repeat("/*", (int)BoundedSqlParser.MaxCommentOpens(SmallStack) + 10));

    [Fact]
    public void Validator_reports_a_text_over_the_bound_as_one_diagnostic()
    {
        var sql = OverTheTokenBound();

        var diagnostics = QueryStack.Run(() => new SqlValidator(null).ValidateAsync(sql).GetAwaiter().GetResult(), SmallStack);

        diagnostics.Should().ContainSingle()
            .Which.Should().Match<SqlDiagnostic>(d =>
                d.Message == QueryNestedTooDeeplyException.DefaultMessage && d.Severity == SqlDiagnosticSeverity.Error);
    }

    [Fact]
    public void Cursor_context_falls_back_for_a_text_over_the_bound()
    {
        var tokens = OverTheTokenBound();
        var comments = OverTheCommentBound();

        var byTokens = QueryStack.Run(() => SqlCursorContext.Analyze(tokens, tokens.Length), SmallStack);
        var byComments = QueryStack.Run(() => SqlCursorContext.Analyze(comments, comments.Length), SmallStack);

        byTokens.Should().NotBeNull();
        byComments.Kind.Should().Be(SqlCompletionContextKind.Keyword);
    }

    [Fact]
    public void Tokenizer_shows_a_text_over_the_comment_bound_as_one_error_span()
    {
        var sql = OverTheCommentBound();

        var spans = QueryStack.Run(() => new SqlSourceTokenizer().Tokenize(sql), SmallStack);

        spans.Should().ContainSingle().Which.Type.Should().Be(SourceTokenType.Error);
    }
}

public sealed class ScriptRefusalMessageTests
{
    [Fact]
    public void A_refusal_after_writes_names_their_lines_and_no_text()
    {
        var refusal = ScriptExecutionNode.RefusedAfterWrites(
            new QueryNestedTooDeeplyException(QueryDepthLimit.ExecutionStack), new[] { 1, 3 });

        refusal.ErrorCode.Should().Be("Query.NestedTooDeeply");
        refusal.Message.Should().StartWith(QueryNestedTooDeeplyException.DefaultMessage)
            .And.Contain("the statements starting at lines 1, 3")
            .And.Contain("are not undone");
    }
}
