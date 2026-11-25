using System.Runtime.ExceptionServices;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.Tui.Screens;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query;

/// <summary>
/// The pre-parse guard (#1952): SQL text too long or nested too deeply for the parser and the engine's own recursive walks is
/// refused with <c>Query.TooComplex</c> (exit code 3) before the parser runs, for every statement kind; at each bound the
/// text parses, and a SELECT plans and runs. Every case runs on a thread created with a 1 MB stack, the smallest default
/// stack .NET documents (see <see cref="QueryComplexityGuard"/>), so the outcome does not depend on the test runner's thread:
/// on the Windows CI job these cases are the measurement of the Windows margin. Without the guard, the cases over a bound
/// overflow that stack (a crash of the test host is a failure).
/// </summary>
[Trait("Category", "Unit")]
public sealed class QueryComplexityGuardTests
{
    private const int OneMegabyte = 1024 * 1024;

    private static readonly DmlSafetyOptions Confirmed = new() { IsConfirmed = true };

    /// <summary>Runs <paramref name="body"/> to its end on a new thread with a stack of <paramref name="stackSize"/> bytes.</summary>
    internal static void OnStack(int stackSize, Func<Task> body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, stackSize);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private static string Repeat(string unit, int n, string separator) => string.Join(separator, Enumerable.Repeat(unit, n));

    /// <summary>A condition of each nesting kind, <paramref name="n"/> deep (the shapes the bounds were measured with).</summary>
    private static string Condition(string kind, int n) => kind switch
    {
        "or" => Repeat("name = 'v'", n, " OR "),
        "and" => Repeat("name = 'v'", n, " AND "),
        "not" => string.Concat(Enumerable.Repeat("NOT ", n)) + "name = 'v'",
        "parentheses" => new string('(', n) + "name = 'v'" + new string(')', n),
        "value parentheses" => "numberofemployees = " + new string('(', n) + "1" + new string(')', n),
        "arithmetic" => "numberofemployees = " + Repeat("1", n, " + "),
        "concatenation" => "name = " + Repeat("'a'", n, " + "),
        "unary minus" => "numberofemployees = " + string.Concat(Enumerable.Repeat("- ", n)) + "1",
        "case" => "numberofemployees = " + string.Concat(Enumerable.Repeat("CASE WHEN 1 = 1 THEN ", n)) + "1" + string.Concat(Enumerable.Repeat(" ELSE 0 END", n)),
        "subquery" => "numberofemployees = " + string.Concat(Enumerable.Repeat("(SELECT ", n)) + "1" + new string(')', n),
        "function" => "numberofemployees = " + string.Concat(Enumerable.Repeat("ABS(", n)) + "1" + new string(')', n),
        _ => throw new ArgumentException(kind)
    };

    private static string Statement(string statement, string condition) => statement switch
    {
        "SELECT" => $"SELECT name FROM account WHERE {condition}",
        "DELETE" => $"DELETE FROM account WHERE {condition}",
        "UPDATE" => $"UPDATE account SET description = 'x' WHERE {condition}",
        "INSERT ... SELECT" => $"INSERT INTO account (description) SELECT description FROM account WHERE {condition}",
        _ => throw new ArgumentException(statement)
    };

    /// <summary>
    /// Each kind of nesting at its bound (the deepest the guard admits) and one deeper, measured for these shapes. A SELECT
    /// chains at most <see cref="QueryComplexityGuard.MaxSelectConditionChain"/> conditions; a write's chain is bounded by
    /// the stack budget (its 200-level write limit applies after the parse, as before).
    /// </summary>
    public static TheoryData<string, string, int> Bounds()
    {
        var data = new TheoryData<string, string, int>();
        foreach (var statement in new[] { "SELECT", "DELETE", "UPDATE", "INSERT ... SELECT" })
        {
            var write = statement != "SELECT";
            data.Add(statement, "or", write ? 3698 : 200);
            data.Add(statement, "and", write ? 3698 : 200);
            data.Add(statement, "not", 1349);
            data.Add(statement, "parentheses", 453);
            data.Add(statement, "value parentheses", 221);
            data.Add(statement, "arithmetic", 650);
            data.Add(statement, "concatenation", 650);
            data.Add(statement, "unary minus", 649);
            data.Add(statement, "case", 198);
            data.Add(statement, "subquery", 103);
            data.Add(statement, "function", 174);
        }
        return data;
    }

    [Fact]
    public void TheBounds_AreTheMeasuredOnes()
    {
        Assert.Equal(200, QueryComplexityGuard.MaxSelectConditionChain);
        Assert.Equal(440_000, QueryComplexityGuard.StackBudget);
        Assert.Equal(1_000, QueryComplexityGuard.MaxBlockComments);
        Assert.Equal(8_388_608, QueryComplexityGuard.MaxTextLength);
    }

    /// <summary>
    /// At the bound, on a 1 MB stack: the text is not refused by the guard. A SELECT parses, plans and runs; a write parses and
    /// is then decided as before (most of these shapes are refused by the write allowlist or its 200-level limit).
    /// </summary>
    [Theory]
    [MemberData(nameof(Bounds))]
    public void AtTheBound_TheTextIsParsed_AndASelectPlansAndRuns_OnA1MbStack(string statement, string kind, int bound)
    {
        var sql = Statement(statement, Condition(kind, bound));
        OnStack(OneMegabyte, async () =>
        {
            // No rows: the read is planned, generated and sent; what comes back does not matter here.
            var h = DmlTestHarness.Create(rows: 0);
            try
            {
                await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed });
                if (statement == "SELECT")
                    Assert.Single(h.Reads);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                // Not the guard: a SELECT with NOT or a subquery is refused by the engine, a write by its allowlist or limit.
                Assert.False(ex is PpdsException { ErrorCode: ErrorCodes.Query.TooComplex }, ex.Message);
                Assert.True(statement != "SELECT" || kind is "not" or "subquery", $"{statement} {kind}: {ex.GetType().Name} {ex.Message}");
            }
        });
    }

    /// <summary>One level over the bound, on a 1 MB stack: <c>Query.TooComplex</c>, exit code 3, before anything is parsed or read.</summary>
    [Theory]
    [MemberData(nameof(Bounds))]
    public void OneOverTheBound_IsRefusedBeforeTheParser_WithExitCode3_OnA1MbStack(string statement, string kind, int bound)
    {
        var sql = Statement(statement, Condition(kind, bound + 1));
        OnStack(OneMegabyte, async () =>
        {
            foreach (var safety in new[] { Confirmed, new DmlSafetyOptions(), new DmlSafetyOptions { IsDryRun = true } })
            {
                var h = DmlTestHarness.Create(rows: 1);
                var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = safety }));
                Assert.Equal(ErrorCodes.Query.TooComplex, ex.ErrorCode);
                Assert.Equal(ExitCodes.InvalidArguments, ExceptionMapper.ToExitCode(ex));
                Assert.IsType<QueryTooComplexException>(ex.InnerException);
                Assert.StartsWith("The SQL was not read: ", ex.Message);
                Assert.Contains("Nothing was executed.", ex.Message);
                Assert.Empty(h.Reads);
                h.AssertNothingChanged();
            }
        });
    }

    /// <summary>The SELECT chain refusal says what to write instead.</summary>
    [Fact]
    public void ASelectChainOverTheBound_SaysWhatToWriteInstead()
    {
        var ex = Assert.Throws<QueryTooComplexException>(() => new QueryParser().Parse(Statement("SELECT", Condition("or", 201))));

        Assert.Contains("a SELECT chains more than 200 conditions with AND or OR", ex.Message);
        Assert.Contains("Compare one column with many values with IN (...), or split the statement into several.", ex.Message);
    }

    /// <summary>A SELECT may chain 200 conditions where the 1 MB stack allows more; an IN list of many values is not bounded.</summary>
    [Fact]
    public void AnInListOf20000Values_IsParsedAndRun_OnA1MbStack()
    {
        var values = string.Join(", ", Enumerable.Range(0, 20_000).Select(i => $"'v{i}'"));
        OnStack(OneMegabyte, async () =>
        {
            var h = DmlTestHarness.Create(rows: 1);
            await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = $"SELECT name FROM account WHERE name IN ({values})" });
            Assert.Single(h.Reads);
        });
    }

    public static TheoryData<string, int> ScriptBounds() => new()
    {
        { "IF", 189 },
        { "ELSE IF", 188 },
        { "BEGIN ... END", 241 },
        { "UNION ALL", 2604 },
    };

    private static string Script(string kind, int n) => kind switch
    {
        "IF" => string.Concat(Enumerable.Repeat("IF 1 = 1 ", n)) + "SELECT 1",
        "ELSE IF" => "IF 1 = 0 SELECT 0 " + string.Concat(Enumerable.Repeat("ELSE IF 1 = 0 SELECT 0 ", n)) + "ELSE SELECT 1",
        "BEGIN ... END" => string.Concat(Enumerable.Repeat("BEGIN ", n)) + "SELECT 1 " + string.Concat(Enumerable.Repeat("END ", n)),
        "UNION ALL" => Repeat("SELECT name FROM account WHERE name = 'v'", n, " UNION ALL "),
        _ => throw new ArgumentException(kind)
    };

    /// <summary>Statements nested in IF, ELSE IF and BEGIN ... END, and a UNION chain: at the bound they run; one more is refused.</summary>
    [Theory]
    [MemberData(nameof(ScriptBounds))]
    public void ScriptNesting_AtTheBoundRuns_AndOneMoreIsRefused_OnA1MbStack(string kind, int bound)
    {
        OnStack(OneMegabyte, async () =>
        {
            var h = DmlTestHarness.Create(rows: 1);
            await h.Service.ExecuteAsync(new SqlQueryRequest { Sql = Script(kind, bound) });

            var ex = await Assert.ThrowsAsync<PpdsException>(() => DmlTestHarness.Create(rows: 1).Service.ExecuteAsync(new SqlQueryRequest { Sql = Script(kind, bound + 1) }));
            Assert.Equal(ErrorCodes.Query.TooComplex, ex.ErrorCode);
        });
    }

    /// <summary>
    /// Nested block comments overflow the tokenizer itself: the count of <c>/*</c> is checked by a character scan first. 1,000
    /// are read (as 500 nested pairs, each closing <c>*/*/</c> holds another <c>/*</c>); 200,000 nested are refused.
    /// </summary>
    [Fact]
    public void NestedBlockComments_AreBoundedBeforeTheTokenizerRuns_OnA1MbStack()
    {
        static string Nested(int n) => "SELECT name FROM account WHERE name = 'v' " + string.Concat(Enumerable.Repeat("/*", n)) + string.Concat(Enumerable.Repeat("*/", n));

        OnStack(OneMegabyte, async () =>
        {
            await DmlTestHarness.Create(rows: 1).Service.ExecuteAsync(new SqlQueryRequest { Sql = Nested(500) });
            var ex = await Assert.ThrowsAsync<PpdsException>(() => DmlTestHarness.Create(rows: 1).Service.ExecuteAsync(new SqlQueryRequest { Sql = Nested(200_000) }));
            Assert.Equal(ErrorCodes.Query.TooComplex, ex.ErrorCode);
            Assert.Contains("block comments", ex.Message);
        });
    }

    [Fact]
    public async Task ATextLongerThanTheCap_IsRefused()
    {
        var sql = "SELECT name FROM account WHERE name = '" + new string('a', QueryComplexityGuard.MaxTextLength) + "'";

        var ex = await Assert.ThrowsAsync<PpdsException>(() => DmlTestHarness.Create(rows: 1).Service.ExecuteAsync(new SqlQueryRequest { Sql = sql }));

        Assert.Equal(ErrorCodes.Query.TooComplex, ex.ErrorCode);
        Assert.Contains("characters long", ex.Message);
    }

    /// <summary>The write limits are unchanged: every text they admit passes the guard (200 levels; 1,000 conditions).</summary>
    [Theory]
    [InlineData("DELETE FROM account WHERE {0}")]
    [InlineData("UPDATE account SET description = 'x' WHERE {0}")]
    [InlineData("INSERT INTO account (description) SELECT description FROM account WHERE {0}")]
    public void EveryTextTheWriteLimitsAdmit_PassesTheGuard(string write)
    {
        static string Balanced(int from, int count) => count == 1
            ? $"name = 'n{from}'"
            : $"({Balanced(from, count / 2)} OR {Balanced(from + count / 2, count - count / 2)})";

        foreach (var condition in new[]
                 {
                     Repeat("name = 'n'", 200, " OR "), Repeat("name = 'n'", 200, " AND "), Balanced(0, 1000),
                     new string('(', 199) + "name = 'n'" + new string(')', 199),
                     new string('(', 100) + Repeat("name = 'n'", 100, " OR ") + new string(')', 100),
                 })
        {
            Assert.Null(QueryComplexityGuard.FindRefusal(string.Format(System.Globalization.CultureInfo.InvariantCulture, write, condition),
                new Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser(true)));
        }
    }

    /// <summary>
    /// The size the report saw exhaust the stack (about 6,500 ORed conditions; it crashed the Windows test host before the
    /// guard), in the first statement and in a later one: refused before the parser runs, on a 1 MB stack.
    /// </summary>
    [Theory]
    [InlineData("DELETE FROM account WHERE {0}")]
    [InlineData("UPDATE account SET description = 'x' WHERE name = 'n1'; DELETE FROM account WHERE {0}")]
    [InlineData("SELECT name FROM account WHERE {0}")]
    public void SixThousandFiveHundredConditions_AreRefusedBeforeTheParser_OnA1MbStack(string template)
    {
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Repeat("name = 'v'", 6500, " OR "));
        OnStack(OneMegabyte, async () =>
        {
            var h = DmlTestHarness.Create(rows: 1);
            var ex = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ExecuteAsync(new SqlQueryRequest { Sql = sql, DmlSafety = Confirmed }));
            Assert.Equal(ErrorCodes.Query.TooComplex, ex.ErrorCode);
            Assert.Empty(h.Reads);
            h.AssertNothingChanged();
        });
    }

    /// <summary>The TUI's confirmation listing parses the text itself: a text the guard refuses is not listed, and nothing overflows.</summary>
    [Fact]
    public void TheTuiConfirmationListing_DoesNotParseATextTheGuardRefuses_OnA1MbStack()
    {
        OnStack(OneMegabyte, () =>
        {
            Assert.Null(SqlQueryPresenter.DataChangingStatements("DELETE FROM account WHERE " + Repeat("name = 'v'", 50_000, " OR ")));
            Assert.NotNull(SqlQueryPresenter.DataChangingStatements("DELETE FROM account WHERE name = 'v'"));
            return Task.CompletedTask;
        });
    }
}
