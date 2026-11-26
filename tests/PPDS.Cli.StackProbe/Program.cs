// Child process for the #1952 stack tests (see PPDS.Cli.Tests StackProbeTests). One job per process; the
// last line of output is "RESULT <json>". A stack overflow ends the process without that line.
//
//   calibrate <shape> <n>                 stack the parser uses for the shape at n (painted), and its counts
//   bound <shape> <stackMiB>              the shape at the bound for that stack, and one token over it
//   run <shape> <n> <stackKB> <entries>   the shape through product entry points, each on a thread of stackKB;
//                                         an entry "name@main" runs on the main thread, "name@pool" on the thread pool
//   walk <walk> <shape> <n> <stackKB>     one of our recursive walks on a thread of stackKB
//   reserve <threads> <stackMiB>          memory cost of that many blocked threads (0: the default size)
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Query;
using PPDS.Cli.StackProbe;
using PPDS.Cli.Tui.Components;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Execution;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Execution;
using PPDS.Query.Intellisense;
using PPDS.Query.Parsing;
using PPDS.Query.Planning;
using PPDS.Query.Transpilation;

var result = args[0] switch
{
    "calibrate" => Jobs.Calibrate(args[1], int.Parse(args[2])),
    "bound" => Jobs.Bound(args[1], int.Parse(args[2])),
    "run" => Jobs.RunEntries(args[1], int.Parse(args[2]), int.Parse(args[3]), args[4].Split(',')),
    "walk" => Jobs.Walk(args[1], args[2], int.Parse(args[3]), int.Parse(args[4])),
    "reserve" => Jobs.Reserve(int.Parse(args[1]), int.Parse(args[2])),
    _ => throw new ArgumentException(args[0])
};
Console.WriteLine("RESULT " + JsonSerializer.Serialize(result));
return 0;

internal static class Jobs
{
    private const int MiB = 1 << 20;

    public static object Calibrate(string shape, int n)
    {
        var sql = Shapes.Get(shape)(n);
        var (counted, commentOpens) = Counts(sql);
        long used = 0;
        string outcome = "";
        // A 512 MiB parse thread; the painted region is the 256 MiB below the measuring frame.
        QueryStack.Run(() =>
        {
            GC.Collect();
            var noGc = GC.TryStartNoGCRegion(64L * MiB);
            used = StackPaint.Measure(() => outcome = Parse(sql), 256 * MiB);
            if (noGc && System.Runtime.GCSettings.LatencyMode == System.Runtime.GCLatencyMode.NoGCRegion)
                GC.EndNoGCRegion();
            return 0;
        }, 512 * MiB);
        return new { shape, n, used, counted, commentOpens, outcome, factor = BoundedSqlParser.PlatformFactor, os = Os() };
    }

    public static object Bound(string shape, int stackMiB)
    {
        var make = Shapes.Get(shape);
        var stack = stackMiB * MiB;
        var comments = shape == "nestedcomment";
        var max = comments ? BoundedSqlParser.MaxCommentOpens(stack) : BoundedSqlParser.MaxTokens(stack);
        long Count(int n) { var c = Counts(make(n)); return comments ? c.CommentOpens : c.Counted; }

        // The largest n whose count is within the bound, by doubling then bisection (counts grow with n).
        int lo = 1, hi = 2;
        while (Count(hi) <= max) { lo = hi; hi *= 2; }
        while (hi - lo > 1) { var mid = lo + (hi - lo) / 2; if (Count(mid) <= max) lo = mid; else hi = mid; }

        var at = QueryStack.Run(() => Parse(make(lo)), stack);
        var over = QueryStack.Run(() => Parse(make(hi)), stack);
        return new { shape, stackMiB, max, nAt = lo, countAt = Count(lo), outcomeAt = at, nOver = hi, countOver = Count(hi), outcomeOver = over, os = Os() };
    }

    public static object RunEntries(string shape, int n, int stackKb, string[] entries)
    {
        var sql = Shapes.Get(shape)(n);
        var results = new Dictionary<string, string>();
        foreach (var item in entries)
        {
            var parts = item.Split('@');
            var entry = parts[0];
            results[entry] = parts.Length == 1 ? OnThread(stackKb, () => RunEntry(entry, sql))
                : parts[1] == "main" ? Outcome(() => RunEntry(entry, sql))
                : parts[1] == "pool" ? Task.Run(() => Outcome(() => RunEntry(entry, sql))).GetAwaiter().GetResult()
                : throw new ArgumentException(item);
        }
        return new { shape, n, stackKb, results, os = Os() };
    }

    private static string RunEntry(string entry, string sql)
    {
        switch (entry)
        {
            case "parse":
                return Parse(sql);
            case "transpile":
                Service().TranspileSql(sql);
                return "ok";
            case "explain":
                Service().ExplainAsync(sql).GetAwaiter().GetResult();
                return "ok";
            case "execute":
            {
                var r = Service().ExecuteAsync(new SqlQueryRequest
                {
                    Sql = sql,
                    DmlSafety = new DmlSafetyOptions { IsConfirmed = true, NoLimit = true }
                }).GetAwaiter().GetResult();
                return "ok";
            }
            case "dryrun":
                Service().ExecuteAsync(new SqlQueryRequest
                {
                    Sql = sql,
                    DmlSafety = new DmlSafetyOptions { IsDryRun = true, NoLimit = true }
                }).GetAwaiter().GetResult();
                return "ok";
            case "validate":
            {
                var diagnostics = new SqlValidator(new AccountMetadata()).ValidateAsync(sql).GetAwaiter().GetResult();
                return diagnostics.Any(d => d.Message.StartsWith(QueryNestedTooDeeplyException.DefaultMessage, StringComparison.Ordinal))
                    ? "refused"
                    : "ok";
            }
            case "cursor":
                SqlCursorContext.Analyze(sql, sql.Length);
                return "ok";
            case "tokenize":
                new SqlSourceTokenizer().Tokenize(sql);
                return "ok";
            case "mcp":
            {
                // What ppds_query_sql does: parse, then generate FetchXML directly.
                var statement = new QueryParser().ParseStatement(sql);
                new FetchXmlGenerator().Generate(statement);
                return "ok";
            }
            default:
                throw new ArgumentException(entry);
        }
    }

    public static object Walk(string walk, string shape, int n, int stackKb)
    {
        var sql = Shapes.Get(shape)(n);
        // Everything but the walk under test runs on a 1 GiB parse thread.
        var fragment = QueryStack.Run(() => new QueryParser().Parse(sql));
        var statement = ((TSqlScript)fragment).Batches[0].Statements[0];
        var builder = new ExecutionPlanBuilder(new FetchXmlGeneratorService());
        string outcome = walk switch
        {
            // Walks that run on the parse thread: run them on a small one.
            "fetchxml" => OnParseThread(stackKb, () => new FetchXmlGenerator().Generate(statement)),
            "plan" => OnParseThread(stackKb, () => builder.Plan(fragment)),
            // Walks that run on the caller's thread.
            "compile" => OnThread(stackKb, () => new ExpressionCompiler().CompilePredicate(Condition(statement))),
            "eval" => EvalOnThread(stackKb, statement),
            "evalscalar" => EvalScalarOnThread(stackKb, statement),
            "guard" => OnThread(stackKb, () => new DmlSafetyGuard().Check(statement, new DmlSafetyOptions { IsConfirmed = true })),
            "describe" => DescribeOnThread(stackKb, builder, fragment),
            "execute" => OnThread(stackKb, () => ExecutePlan(builder.Plan(fragment))),
            // The refusal's message, to check what a script refused part-way reports.
            "executemessage" => MessageOnThread(stackKb, () => ExecutePlan(builder.Plan(fragment))),
            "validate" => OnThread(stackKb, () => RunEntry("validate", sql)),
            _ => throw new ArgumentException(walk)
        };
        return new { walk, shape, n, stackKb, outcome, os = Os() };
    }

    public static object Reserve(int threads, int stackMiB)
    {
        using var release = new ManualResetEventSlim(false);
        using var started = new CountdownEvent(threads);
        var process = Process.GetCurrentProcess();
        GC.Collect();
        process.Refresh();
        var privateBefore = process.PrivateMemorySize64;
        var workingSetBefore = process.WorkingSet64;

        var list = new List<Thread>();
        for (var i = 0; i < threads; i++)
        {
            var thread = stackMiB == 0
                ? new Thread(() => { started.Signal(); release.Wait(); })
                : new Thread(() => { started.Signal(); release.Wait(); }, stackMiB * MiB);
            thread.IsBackground = true;
            thread.Start();
            list.Add(thread);
        }
        started.Wait();
        process.Refresh();
        var privateAfter = process.PrivateMemorySize64;
        var workingSetAfter = process.WorkingSet64;
        release.Set();
        foreach (var thread in list)
            thread.Join();

        return new
        {
            threads,
            stackMiB,
            defaultStackSetting = AppContext.GetData("System.Threading.DefaultStackSize")?.ToString(),
            privateBefore,
            privateAfter,
            workingSetBefore,
            workingSetAfter,
            privatePerThread = (privateAfter - privateBefore) / threads,
            workingSetPerThread = (workingSetAfter - workingSetBefore) / threads,
            os = Os()
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static SqlQueryService Service() => new(
        new EmptyQueryExecutor(),
        new NoShakedown(),
        bulkOperationExecutor: new CountingBulkExecutor(),
        metadataProvider: new AccountMetadata());

    private static string Parse(string sql)
    {
        try
        {
            new QueryParser().TryParse(sql, out _, out var errors);
            return errors.Count == 0 ? "parsed" : "parsed-with-errors";
        }
        catch (QueryNestedTooDeeplyException ex)
        {
            return "refused:" + ex.Limit;
        }
    }

    private static (long Counted, long CommentOpens) Counts(string sql)
    {
        var tokens = QueryStack.Run(() => BoundedSqlParser.GetTokenStream(new TSql160Parser(true), sql, out _));
        return (BoundedSqlParser.CountTokens(tokens), BoundedSqlParser.CountCommentOpens(sql));
    }

    private static BooleanExpression Condition(TSqlStatement statement) => statement switch
    {
        SelectStatement { QueryExpression: QuerySpecification spec } => spec.WhereClause.SearchCondition,
        DeleteStatement delete => delete.DeleteSpecification.WhereClause.SearchCondition,
        UpdateStatement update => update.UpdateSpecification.WhereClause.SearchCondition,
        _ => throw new ArgumentException("No WHERE clause.")
    };

    private static string EvalOnThread(int stackKb, TSqlStatement statement)
    {
        var predicate = QueryStack.Run(() => new ExpressionCompiler().CompilePredicate(Condition(statement)));
        var row = Row();
        return OnThread(stackKb, () => predicate(row));
    }

    private static string EvalScalarOnThread(int stackKb, TSqlStatement statement)
    {
        var select = (SelectScalarExpression)((QuerySpecification)((SelectStatement)statement).QueryExpression).SelectElements[0];
        var scalar = QueryStack.Run(() => new ExpressionCompiler().CompileScalar(select.Expression));
        var row = Row();
        return OnThread(stackKb, () => scalar(row));
    }

    private static Dictionary<string, QueryValue> Row() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["name"] = QueryValue.Simple("w"),
        ["numberofemployees"] = QueryValue.Simple(1),
        ["createdon"] = QueryValue.Simple(DateTime.UtcNow)
    };

    private static string DescribeOnThread(int stackKb, ExecutionPlanBuilder builder, TSqlFragment fragment)
    {
        var plan = builder.Plan(fragment);
        var description = QueryStack.Run(() => QueryPlanDescription.FromNode(plan.RootNode));
        var outcomes = new[]
        {
            OnThread(stackKb, () => QueryPlanDescription.FromNode(plan.RootNode)),
            OnThread(stackKb, () => PlanFormatter.Format(description)),
            // The TUI draws a refusal as a line of text rather than throwing.
            OnThread(stackKb, () => QueryPlanView.FormatPlanTree(description).StartsWith("(The execution plan is nested too deeply", StringComparison.Ordinal)
                ? "refused"
                : "ok"),
        };
        return string.Join(",", outcomes);
    }

    private static object ExecutePlan(QueryPlanResult plan)
    {
        var context = new QueryPlanContext(new EmptyQueryExecutor(), CancellationToken.None,
            bulkOperationExecutor: new CountingBulkExecutor());
        return new PlanExecutor().ExecuteAsync(plan, context).GetAwaiter().GetResult();
    }

    private static string MessageOnThread(int stackKb, Func<object> work)
    {
        string message = "no refusal";
        var thread = new Thread(() =>
        {
            try { work(); }
            catch (QueryNestedTooDeeplyException ex) { message = ex.Message; }
            catch (Exception ex) { message = "error:" + ex.GetType().Name; }
        }, stackKb * 1024);
        thread.Start();
        thread.Join();
        return message;
    }

    private static string OnParseThread(int stackKb, Func<object> work) =>
        Outcome(() => QueryStack.Run(work, stackKb * 1024));

    /// <summary>Runs <paramref name="work"/> on a plain thread of <paramref name="stackKb"/> KB, as a caller would.</summary>
    private static string OnThread<T>(int stackKb, Func<T> work)
    {
        string outcome = "";
        var thread = new Thread(() => outcome = Outcome(() => work()), stackKb * 1024);
        thread.Start();
        thread.Join();
        return outcome;
    }

    private static string Outcome(Func<object?> work)
    {
        try
        {
            var value = work();
            return value is string s && (s.StartsWith("refused", StringComparison.Ordinal) || s.StartsWith("parsed", StringComparison.Ordinal) || s == "ok") ? s : "ok";
        }
        catch (QueryNestedTooDeeplyException)
        {
            return "refused";
        }
        catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Query.NestedTooDeeply)
        {
            return "refused";
        }
        catch (Exception ex)
        {
            var code = ex is PpdsException p ? p.ErrorCode : ex is QueryExecutionException q ? q.ErrorCode : "";
            return $"error:{ex.GetType().Name}:{code}";
        }
    }

    private static string Os() => $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture} {RuntimeInformation.FrameworkDescription}";
}
