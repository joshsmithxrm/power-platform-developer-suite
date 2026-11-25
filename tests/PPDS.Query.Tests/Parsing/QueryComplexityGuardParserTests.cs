using System.Runtime.ExceptionServices;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.Tests.Parsing;

/// <summary>
/// Every entry of PPDS.Query that hands SQL text to ScriptDom checks it with <see cref="QueryComplexityGuard"/> first (#1952):
/// <see cref="QueryParser.Parse"/> and <see cref="QueryParser.TryParse"/> refuse it with <see cref="QueryTooComplexException"/>;
/// the editor services (validation, cursor context, syntax colouring) do not parse or tokenize it. Each case runs on a thread
/// with a 1 MB stack; without the guard these texts overflow it.
/// </summary>
[Trait("Category", "Unit")]
public sealed class QueryComplexityGuardParserTests
{
    private const int OneMegabyte = 1024 * 1024;

    private static readonly string SixThousandFiveHundredOrs =
        "SELECT name FROM account WHERE " + string.Join(" OR ", Enumerable.Repeat("name = 'v'", 6500));

    private static readonly string NestedComments =
        "SELECT name FROM account " + string.Concat(Enumerable.Repeat("/*", 200_000)) + string.Concat(Enumerable.Repeat("*/", 200_000));

    private static void OnStack(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, OneMegabyte);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    [Fact]
    public void ParseAndEveryEntryOnIt_RefuseATextTooDeep_BeforeTheParser() => OnStack(() =>
    {
        var parser = new QueryParser();
        Assert.Throws<QueryTooComplexException>(() => parser.Parse(SixThousandFiveHundredOrs));
        Assert.Throws<QueryTooComplexException>(() => parser.ParseStatement(SixThousandFiveHundredOrs));
        Assert.Throws<QueryTooComplexException>(() => parser.ParseBatch(SixThousandFiveHundredOrs));
        Assert.Throws<QueryTooComplexException>(() => parser.Parse(NestedComments));
    });

    /// <summary>TryParse does not report a refusal as a syntax error: it throws the same refusal.</summary>
    [Fact]
    public void TryParse_RefusesATextTooDeep_BeforeTheParser() => OnStack(() =>
    {
        var parser = new QueryParser();
        var ex = Assert.Throws<QueryTooComplexException>(() => parser.TryParse(SixThousandFiveHundredOrs, out _, out _));
        Assert.IsAssignableFrom<QueryParseException>(ex);
        Assert.True(parser.TryParse("SELECT name FROM account WHERE name = 'v'", out var fragment, out var errors));
        Assert.NotNull(fragment);
        Assert.Empty(errors);
    });

    [Fact]
    public void TheValidator_ReportsTheRefusal_WithoutParsing() => OnStack(() =>
    {
        var diagnostics = new SqlValidator(null).ValidateAsync(SixThousandFiveHundredOrs).GetAwaiter().GetResult();

        var diagnostic = Assert.Single(diagnostics);
        Assert.StartsWith("The SQL was not read: ", diagnostic.Message);
    });

    [Fact]
    public void TheCursorContext_AndTheTokenizer_DoNotReadATextTooDeep() => OnStack(() =>
    {
        SqlCursorContext.Analyze(SixThousandFiveHundredOrs, SixThousandFiveHundredOrs.Length);
        SqlCursorContext.Analyze(NestedComments, 10);
        var tokens = new SqlSourceTokenizer().Tokenize(NestedComments);
        Assert.Single(tokens);
    });
}
