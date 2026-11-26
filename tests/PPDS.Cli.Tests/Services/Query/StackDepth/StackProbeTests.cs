using System.Runtime.InteropServices;
using FluentAssertions;
using System.Text.Json;
using PPDS.Query.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Query.StackDepth;

// #1952: no SQL text may end the process with a stack overflow. Every test here runs the stack probe as a
// child process (see StackProbe), so a regression shows as a dead child and a failed test, never as a dead
// test host. They run on every CI operating system and target framework.

/// <summary>
/// The parser's weight per counted token, measured on this platform (stack painting in a cold process). The
/// bound is sound here only while the measured cost stays within the weight the platform is charged.
/// </summary>
public sealed class StackCalibrationTests
{
    private readonly ITestOutputHelper _output;

    public StackCalibrationTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("cut-subquery")]
    [InlineData("cut-paren")]
    [InlineData("cut-case")]
    [InlineData("cut-casewhen")]
    [InlineData("cut-function")]
    [InlineData("cut-begin")]
    [InlineData("cut-rowconstructor")]
    [InlineData("closed-subquery")]
    [InlineData("closed-paren")]
    [InlineData("attimezone")]
    [InlineData("or")]
    public void Parser_stack_per_counted_token_is_within_the_platform_weight(string shape)
    {
        var low = StackProbe.Run("calibrate", shape, "150");
        var high = StackProbe.Run("calibrate", shape, "450");

        var bytes = high.GetProperty("used").GetInt64() - low.GetProperty("used").GetInt64();
        var tokens = high.GetProperty("counted").GetInt64() - low.GetProperty("counted").GetInt64();
        var perToken = (double)bytes / tokens;
        var weight = BoundedSqlParser.BytesPerToken * BoundedSqlParser.PlatformFactor;
        var report = $"STACK-CALIBRATION {shape}: {perToken:F0} bytes per counted token, " +
                     $"{perToken / BoundedSqlParser.BytesPerToken:F2} x the Linux x64 weight; charged {weight:F0} " +
                     $"(factor {BoundedSqlParser.PlatformFactor}); {high.GetProperty("os").GetString()}";
        StackProbe.Report(_output, report);

        perToken.Should().BeLessThanOrEqualTo(weight, report);
    }

    [Fact]
    public void Lexer_stack_per_nested_comment_is_within_the_platform_weight()
    {
        var low = StackProbe.Run("calibrate", "nestedcomment", "1000");
        var high = StackProbe.Run("calibrate", "nestedcomment", "3000");

        var bytes = high.GetProperty("used").GetInt64() - low.GetProperty("used").GetInt64();
        var opens = high.GetProperty("commentOpens").GetInt64() - low.GetProperty("commentOpens").GetInt64();
        var perOpen = (double)bytes / opens;
        var weight = BoundedSqlParser.BytesPerCommentOpen * BoundedSqlParser.PlatformFactor;
        var report = $"STACK-CALIBRATION nestedcomment: {perOpen:F0} bytes per /*, charged {weight:F0}; " +
                     high.GetProperty("os").GetString();
        StackProbe.Report(_output, report);

        perOpen.Should().BeLessThanOrEqualTo(weight, report);
    }
}

/// <summary>At the bound the worst shapes parse; one counted token over, they are refused before parsing.</summary>
public sealed class StackBoundTests
{
    private readonly ITestOutputHelper _output;

    public StackBoundTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("cut-subquery")]
    [InlineData("cut-paren")]
    [InlineData("cut-case")]
    [InlineData("cut-casewhen")]
    [InlineData("cut-rowconstructor")]
    [InlineData("closed-subquery")]
    [InlineData("attimezone")]
    [InlineData("or")]
    public void Text_at_the_bound_parses_and_one_token_over_is_refused(string shape)
    {
        var result = StackProbe.Run("bound", shape, "16");
        _output.WriteLine(result.ToString());

        var max = result.GetProperty("max").GetInt64();
        result.GetProperty("outcomeAt").GetString().Should().StartWith("parsed");
        result.GetProperty("countAt").GetInt64().Should().BeInRange(max - 3, max,
            "the text at the bound holds as many counted tokens as the bound admits, to within one level");
        result.GetProperty("countOver").GetInt64().Should().BeGreaterThan(max);
        result.GetProperty("outcomeOver").GetString().Should().Be("refused:TokenCount");
    }

    [Fact]
    public void Nested_comments_at_the_bound_lex_and_one_over_are_refused()
    {
        var result = StackProbe.Run("bound", "nestedcomment", "16");
        _output.WriteLine(result.ToString());

        result.GetProperty("outcomeAt").GetString().Should().StartWith("parsed");
        result.GetProperty("outcomeOver").GetString().Should().Be("refused:CommentNesting");
    }
}

/// <summary>
/// The hosts' 64 MiB default thread stack, and the 1 GiB parse thread, reserve address space only: memory is
/// committed as a stack is used. Windows reports commit as private bytes; Linux and macOS report resident memory.
/// </summary>
public sealed class StackReservationTests
{
    private const long OneMiB = 1 << 20;
    private readonly ITestOutputHelper _output;

    public StackReservationTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(200, 0)]
    [InlineData(8, 1024)]
    public void Blocked_threads_commit_far_less_than_their_stack(int threads, int stackMiB)
    {
        var result = StackProbe.Run("reserve", threads.ToString(), stackMiB.ToString());
        var report = $"STACK-RESERVATION {threads} threads of {(stackMiB == 0 ? "the default 64 MiB" : stackMiB + " MiB")}: " +
                     $"{result.GetProperty("privatePerThread").GetInt64() / 1024} KB private, " +
                     $"{result.GetProperty("workingSetPerThread").GetInt64() / 1024} KB resident per thread; " +
                     result.GetProperty("os").GetString();
        StackProbe.Report(_output, report);

        result.GetProperty("defaultStackSetting").GetString().Should().Be("0x4000000",
            "the probe ships the hosts' System.Threading.DefaultStackSize");
        var committedPerThread = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? result.GetProperty("privatePerThread").GetInt64()
            : result.GetProperty("workingSetPerThread").GetInt64();
        committedPerThread.Should().BeLessThan(OneMiB, report);
    }
}

/// <summary>
/// The reviewer's bypass table (round 3 of #1698): each of these killed the process on a 1 MB stack. Through
/// every entry point, on a 512 KB caller thread, each now runs or is refused with the coded error.
/// </summary>
public sealed class StackBypassTests
{
    private const string Entries = "parse,execute,explain,validate,cursor,tokenize,mcp";
    private readonly ITestOutputHelper _output;

    public StackBypassTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("bypass-or-mergejoin", 20_000)]
    [InlineData("bypass-leftnested", 5_000)]
    [InlineData("bypass-isdistinct", 20_000)]
    [InlineData("bypass-delete-isdistinct", 10_000)]
    [InlineData("bypass-mult-hex", 20_000)]
    [InlineData("bypass-mult-case", 10_000)]
    [InlineData("bypass-mult-timestamp", 20_000)]
    [InlineData("bypass-update-mult-hex", 10_000)]
    [InlineData("bypass-insert-mult-hex", 10_000)]
    [InlineData("bypass-shift-left", 20_000)]
    [InlineData("bypass-shift-right", 20_000)]
    [InlineData("bypass-attimezone", 20_000)]
    [InlineData("bypass-join", 10_000)]
    [InlineData("bypass-crossjoin", 10_000)]
    [InlineData("bypass-endconversation", 5_000)]
    [InlineData("bypass-intersect", 5_000)]
    [InlineData("bypass-except", 5_000)]
    [InlineData("bypass-case-in-when", 2_000)]
    [InlineData("bypass-case-paren", 2_000)]
    [InlineData("bypass-begintry", 2_000)]
    public void Bypass_runs_or_is_refused_with_the_coded_error_and_never_ends_the_process(string shape, int n)
    {
        var result = StackProbe.Run("run", shape, n.ToString(), "512", Entries);
        _output.WriteLine(result.ToString());

        foreach (var entry in result.GetProperty("results").EnumerateObject())
        {
            var outcome = entry.Value.GetString()!;
            outcome.Should().NotContain("InsufficientExecutionStack",
                $"{entry.Name} must turn a stack refusal into the coded error");
            outcome.Should().NotContain("StackOverflow", entry.Name);
        }
    }
}

/// <summary>
/// The reviewer's false-refusal table, and large flat texts: each ran before and must still run, called the way
/// the hosts call: a query from the main thread (1 MB on Windows), validation from the thread pool (the daemon).
/// </summary>
public sealed class StackFalseRefusalTests
{
    private readonly ITestOutputHelper _output;

    public StackFalseRefusalTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("ok-comments-in-string", 1_001)]
    [InlineData("ok-comment-statements", 1_001)]
    [InlineData("ok-insert-comment-values", 1_000)]
    [InlineData("ok-line-comments-update", 1_001)]
    [InlineData("ok-sequential-if", 190)]
    [InlineData("ok-or-arithmetic", 180)]
    [InlineData("ok-between", 101)]
    [InlineData("ok-union-block", 2_700)]
    [InlineData("ok-insert-long-rows", 1_000)]
    [InlineData("ok-update-long-text", 8_400_000)]
    [InlineData("ok-in-list", 100_000)]
    [InlineData("ok-in-list-strings", 100_000)]
    [InlineData("ok-insert-short-rows", 20_000)]
    [InlineData("ok-or-chain", 6_000)]
    [InlineData("issue-delete-or", 6_500)]
    public void Statement_that_ran_before_still_runs(string shape, int n)
    {
        var result = StackProbe.Run("run", shape, n.ToString(), "0", "execute@main,validate@pool");
        _output.WriteLine(result.ToString());

        var results = result.GetProperty("results");
        results.GetProperty("execute").GetString().Should().Be("ok");
        results.GetProperty("validate").GetString().Should().Be("ok");
    }
}

/// <summary>
/// Every recursive walk of ours checks the stack: a tree too deep for a small thread is refused with the coded
/// error. Removing the check from the walk makes its row end the child process, which fails the row.
/// </summary>
public sealed class StackWalkTests
{
    private readonly ITestOutputHelper _output;

    public StackWalkTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("fetchxml", "or", 20_000, 2048)]
    [InlineData("fetchxml", "bypass-leftnested", 5_000, 2048)]
    [InlineData("plan", "bypass-intersect", 5_000, 2048)]
    [InlineData("plan", "bypass-crossjoin", 10_000, 2048)]
    [InlineData("plan", "bypass-join", 10_000, 512)]
    [InlineData("plan", "derived", 2_000, 2048)]
    [InlineData("compile", "or", 20_000, 512)]
    [InlineData("eval", "bypass-isdistinct", 20_000, 256)]
    [InlineData("evalscalar", "eval-arith", 50_000, 512)]
    [InlineData("guard", "script-begin-delete", 5_000, 512)]
    [InlineData("describe", "bypass-intersect", 5_000, 256)]
    [InlineData("execute", "bypass-intersect", 5_000, 512)]
    [InlineData("execute", "script-if", 5_000, 512)]
    [InlineData("execute", "script-try-deep", 50_000, 512)]
    [InlineData("validate", "bypass-join", 10_000, 512)]
    public void Walk_too_deep_for_the_thread_is_refused_with_the_coded_error(string walk, string shape, int n, int stackKb)
    {
        var result = StackProbe.Run("walk", walk, shape, n.ToString(), stackKb.ToString());
        _output.WriteLine(result.ToString());

        result.GetProperty("outcome").GetString()!.Split(',').Should().OnlyContain(o => o == "refused");
    }

    [Fact]
    public void Script_refused_after_a_write_names_the_write_that_already_ran()
    {
        var result = StackProbe.Run("walk", "executemessage", "script-write-then-deep", "50000", "512");
        _output.WriteLine(result.ToString());

        result.GetProperty("outcome").GetString().Should()
            .StartWith(PPDS.Dataverse.Query.Execution.QueryNestedTooDeeplyException.DefaultMessage)
            .And.Contain("The script stopped at this statement")
            .And.Contain("the statement starting at line 1.");
    }
}
