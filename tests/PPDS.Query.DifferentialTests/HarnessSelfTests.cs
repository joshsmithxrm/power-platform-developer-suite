using System.Data.SqlTypes;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Query.DifferentialTests.Harness;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// Tests of the harness itself. They need no server, so they run in every CI build: the generator is deterministic and
/// writes SQL the product parses, values render exactly or stop the run, the comparison sees each kind of difference,
/// a harness fault is never an outcome, and the snapshot compares exactly.
/// </summary>
[Trait("Category", "SqlDifferential")]
public sealed class HarnessSelfTests
{
    // ── Generator ─────────────────────────────────────────────────────

    [Fact]
    public void Same_seed_generates_the_same_expressions()
    {
        var first = new ExpressionGenerator(42, 3);
        var second = new ExpressionGenerator(42, 3);
        for (var i = 0; i < 200; i++)
            Assert.Equal(first.Next().Sql, second.Next().Sql);
    }

    [Fact]
    public void Different_seeds_generate_different_expressions()
    {
        var a = new ExpressionGenerator(1, 3);
        var b = new ExpressionGenerator(2, 3);
        var left = Enumerable.Range(0, 20).Select(_ => a.Next().Sql).ToList();
        var right = Enumerable.Range(0, 20).Select(_ => b.Next().Sql).ToList();
        Assert.NotEqual(left, right);
    }

    [Fact]
    public void The_random_source_is_fixed_across_runtimes()
    {
        // SplitMix64 from seed 0: the first outputs are fixed by the algorithm, so a seed means the same on every
        // .NET version and machine.
        var random = new SeededRandom(0);
        Assert.Equal(0xE220A8397B1DCDAFUL, random.NextUInt64());
        Assert.Equal(0x6E789E6AA1B965F4UL, random.NextUInt64());
    }

    [Fact]
    public void Generated_expressions_parse_as_one_scalar_expression_and_reach_every_rule()
    {
        var parser = new QueryParser();
        var generator = new ExpressionGenerator(7, 3);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3000; i++)
        {
            var expression = generator.Next();
            foreach (var node in expression.Nodes())
                seen.Add(node.Construct);
            var statement = Assert.IsType<SelectStatement>(parser.ParseStatement("SELECT " + expression.Sql));
            Assert.Single(((QuerySpecification)statement.QueryExpression).SelectElements);
        }

        var missing = RuleTable.Rules.Select(r => r.Name).Concat(RuleTable.Leaves.Select(l => l.Name)).Distinct().Where(n => !seen.Contains(n)).ToList();
        Assert.True(missing.Count == 0, "Never generated: " + string.Join(", ", missing));
    }

    [Fact]
    public void Generated_depth_never_exceeds_the_maximum()
    {
        static int Depth(Expr e) => e.IsLeaf ? 0 : 1 + e.Children.Max(Depth);
        var generator = new ExpressionGenerator(3, 2);
        for (var i = 0; i < 500; i++)
            Assert.InRange(Depth(generator.Next()), 1, 3); // a predicate adds one level under IIF and CASE
    }

    [Fact]
    public void A_predicate_is_evaluated_as_one_zero_or_null()
    {
        var rule = RuleTable.Rules.First(r => r.Name == "number = number");
        var leaf = RuleTable.Leaves.First(l => l.Sql == "c_int");
        var predicate = Expr.Of(rule, Expr.Of(leaf), Expr.Of(leaf));
        Assert.Equal("CASE WHEN c_int = c_int THEN 1 WHEN NOT (c_int = c_int) THEN 0 END", predicate.ScalarSql);
    }

    // ── Rendering and comparison ──────────────────────────────────────

    [Theory]
    [InlineData(null, "NULL")]
    [InlineData("abc ", "'abc '")]
    [InlineData("a\nb", "'a{U+000A}b'")]
    [InlineData("{x}", "'{U+007B}x{U+007D}'")]
    [InlineData("\u0101", "'{U+0101}'")]
    public void Text_renders_with_every_invisible_or_lookalike_character_spelled_out(string? value, string expected)
        => Assert.Equal(expected, Outcome.Render(value));

    [Fact]
    public void Numbers_render_with_their_scale()
    {
        Assert.Equal("2.50", Outcome.Render(2.50m));
        Assert.Equal("2.5", Outcome.Render(2.5m));
        Assert.Equal("2.500000", Outcome.Render(SqlDecimal.Parse("2.500000")));
        Assert.Equal("1.5000", Outcome.Render(new SqlMoney(1.5m)));
        Assert.Equal("0.1", Outcome.Render(0.1d));
        Assert.Equal("NULL", Outcome.Render(SqlInt32.Null));
        Assert.Equal("1", Outcome.Render(true));
    }

    [Fact]
    public void Result_column_metadata_gives_precision_scale_and_length()
    {
        Assert.Equal(new SqlTypeInfo("decimal", 16, 6), SqlTypeInfo.FromMetadata("decimal", 16, 6, 17));
        Assert.Equal(new SqlTypeInfo("decimal", 16, 6), SqlTypeInfo.FromMetadata("numeric", 16, 6, 17));
        Assert.Equal(new SqlTypeInfo("varchar", Length: 4), SqlTypeInfo.FromMetadata("varchar", 255, 255, 4));
        Assert.Equal(new SqlTypeInfo("nvarchar", Length: -1), SqlTypeInfo.FromMetadata("nvarchar", 255, 255, int.MaxValue));
        Assert.Equal(new SqlTypeInfo("money", 19, 4), SqlTypeInfo.FromMetadata("money", 19, 255, 8));
        Assert.Equal(new SqlTypeInfo("int"), SqlTypeInfo.FromMetadata("int", 10, 255, 4));
        Assert.Equal("decimal", SqlTypeInfo.FamilyOf("money"));
        Assert.Equal("text", SqlTypeInfo.FamilyOf("varchar"));
    }

    [Fact]
    public void Comparison_sees_value_scale_error_and_type_family()
    {
        var reference = Outcome.Of("2.500000", new SqlTypeInfo("decimal", 7, 6), "decimal");
        Assert.Equal(Difference.None, Outcome.Compare(reference, Outcome.Of("2.500000", null, "decimal")));
        Assert.Equal(Difference.Value, Outcome.Compare(reference, Outcome.Of("2.5", null, "decimal")));
        Assert.Equal(Difference.Value | Difference.Type, Outcome.Compare(reference, Outcome.Of("2", null, "bigint")));
        Assert.Equal(Difference.Error, Outcome.Compare(reference, Outcome.Failed(ErrorClass.DivideByZero, "x")));
        Assert.Equal(Difference.Error, Outcome.Compare(Outcome.Failed(ErrorClass.Overflow, "8115"), Outcome.Failed(ErrorClass.DivideByZero, "x")));
        Assert.Equal(Difference.None, Outcome.Compare(Outcome.Failed(ErrorClass.Overflow, "8115"), Outcome.Failed(ErrorClass.Overflow, "OverflowException")));
        // Two unclassified errors say nothing about each other: never equal, whatever their numbers or names.
        Assert.Equal(Difference.Error, Outcome.Compare(Outcome.Failed(ErrorClass.Other, "-2"), Outcome.Failed(ErrorClass.Other, "NullReferenceException")));
        Assert.Equal(Difference.Error, Outcome.Compare(Outcome.Failed(ErrorClass.Other, "1234"), Outcome.Failed(ErrorClass.Other, "1234")));
        Assert.True(Outcome.NumericallyEqual("2.5", "2.500000"));
    }

    [Fact]
    public void A_full_type_is_compared_exactly_when_the_engine_reports_one()
    {
        var reference = Outcome.Of("'abc'", new SqlTypeInfo("varchar", Length: 3), "text");
        Assert.Equal(Difference.None, Outcome.Compare(reference, Outcome.Of("'abc'", null, "text")));
        Assert.Equal(Difference.Type, Outcome.Compare(reference, Outcome.Of("'abc'", new SqlTypeInfo("nvarchar", Length: 3), "text")));
        Assert.Equal(Difference.Type, Outcome.Compare(reference, Outcome.Of("'abc'", new SqlTypeInfo("varchar", Length: 6), "text")));
    }

    [Fact]
    public void Null_from_the_engine_under_test_is_not_type_compared()
    {
        var reference = Outcome.Of("NULL", new SqlTypeInfo("int"), "int");
        Assert.Equal(Difference.None, Outcome.Compare(reference, Outcome.Of("NULL", null, PpdsEngine.FamilyOf(null))));
    }

    // ── Runner, attribution and harness faults ───────────────────────

    private sealed class FakeEngine(Dictionary<string, Outcome> outcomes) : IExpressionEngine
    {
        public Outcome Evaluate(string expression, int rowId) => outcomes[expression];
    }

    private sealed class FaultingEngine : IExpressionEngine
    {
        public Outcome Evaluate(string expression, int rowId) => throw new HarnessException("injected");
    }

    private static Expr Leaf(string sql, string construct = "literal int") => new(construct, Kind.Number, sql, []);

    private static readonly Outcome Int7 = Outcome.Of("7", new SqlTypeInfo("int"), "int");
    private static readonly Outcome Int7Actual = Outcome.Of("7", null, "int");

    [Fact]
    public void A_report_attributes_a_difference_to_the_smallest_node_whose_operands_agree()
    {
        // ROUND(7, 0) / 2: ROUND differs (float), its operand agrees; the division differs only because of it.
        var round = new Expr("ROUND(x, 0)", Kind.Number, "ROUND(7, 0)", [Leaf("7")]);
        var division = new Expr("/", Kind.Number, "(ROUND(7, 0) / 2)", [round, Leaf("2")]);
        var reference = new FakeEngine(new()
        {
            ["7"] = Int7, ["2"] = Outcome.Of("2", new SqlTypeInfo("int"), "int"),
            ["ROUND(7, 0)"] = Int7, ["(ROUND(7, 0) / 2)"] = Outcome.Of("3", new SqlTypeInfo("int"), "int"),
        });
        var actual = new FakeEngine(new()
        {
            ["7"] = Int7Actual, ["2"] = Outcome.Of("2", null, "int"),
            ["ROUND(7, 0)"] = Outcome.Of("7", null, "float"), ["(ROUND(7, 0) / 2)"] = Outcome.Of("3.5", null, "float"),
        });

        var node = Assert.Single(new DifferentialRunner(reference, actual).Attribute(division, 1));
        Assert.Same(round, node.Node);
        Assert.Equal("ROUND(x, 0): type int vs float", node.Group);
    }

    [Fact]
    public void The_runner_counts_both_engines_and_lets_a_harness_fault_through()
    {
        var runner = new DifferentialRunner(new FakeEngine(new() { ["7"] = Int7 }), new FakeEngine(new() { ["7"] = Int7Actual }));
        runner.Evaluate(Leaf("7"), 1);
        Assert.Equal((1, 1), (runner.ReferenceCalls, runner.ActualCalls));

        Assert.Throws<HarnessException>(() => new DifferentialRunner(new FaultingEngine(), new FakeEngine(new())).Run(1, 5, 2, [1]).ToList());
        Assert.Throws<HarnessException>(() => new DifferentialRunner(new FakeEngine(new() { ["7"] = Int7 }), new FaultingEngine()).Evaluate(Leaf("7"), 1));
    }

    [Fact]
    public void A_missing_operand_row_is_a_harness_fault_not_an_engine_error()
    {
        var engine = new PpdsEngine(new Dictionary<int, IReadOnlyDictionary<string, QueryValue>>());
        Assert.Throws<HarnessException>(() => engine.Evaluate("1 + 1", 99));
    }

    [Fact]
    public void The_engine_refusing_is_an_outcome_and_a_crash_is_not_a_refusal()
    {
        var engine = new PpdsEngine(new Dictionary<int, IReadOnlyDictionary<string, QueryValue>> { [1] = new Dictionary<string, QueryValue>() });
        Assert.Equal(ErrorClass.Refused, engine.Evaluate("LEFT(N'abc', 2)", 1).Error);
        Assert.Equal(ErrorClass.Refused, engine.Evaluate("1 +", 1).Error);
        Assert.Equal(ErrorClass.DivideByZero, engine.Evaluate("1 / 0", 1).Error);
    }

    [Theory]
    [MemberData(nameof(UnrenderedValues))]
    public void A_value_the_harness_cannot_render_exactly_stops_the_run(object value)
        => Assert.Throws<HarnessException>(() => Outcome.Render(value));

    /// <summary>Values of types the harness has no exact rendering for.</summary>
    public static TheoryData<object> UnrenderedValues() => new()
    {
        new byte[] { 1, 2 },
        new SqlBinary([1, 2]),
        new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.FromHours(5)),
        TimeSpan.FromHours(1),
        new[] { 1, 2 },
    };

    // ── Snapshot ──────────────────────────────────────────────────────

    private static readonly Expr Sum = new("+", Kind.Number, "(c_int + 1)", [Leaf("c_int", "column int"), Leaf("1")]);
    private static readonly Expr Product = new("*", Kind.Number, "(c_int * 2)", [Leaf("c_int", "column int"), Leaf("2")]);

    private static (int, int, Evaluation) Differing(Expr expression, int row, string ppds, int index = 0)
    {
        var reference = Outcome.Of("8", new SqlTypeInfo("int"), "int");
        var actual = Outcome.Of(ppds, null, "bigint");
        return (1, index, new Evaluation(expression, row, reference, actual, Outcome.Compare(reference, actual)));
    }

    [Fact]
    public void The_snapshot_round_trips_byte_for_byte_and_sorts_its_rows()
    {
        (int, int, Evaluation)[] run = [Differing(Sum, 4, "8"), Differing(Product, 1, "14", 3), Differing(Sum, 1, "8")];
        var rows = DifferenceSnapshot.FromRun(run);
        Assert.Equal(["(c_int * 2)", "(c_int + 1)", "(c_int + 1)"], rows.Select(r => r.Expression));
        Assert.Equal([1, 1, 4], rows.Select(r => r.RowId));
        var text = DifferenceSnapshot.Format(rows, RunFingerprint.Of(run));
        Assert.Equal(text, DifferenceSnapshot.Format(DifferenceSnapshot.Parse(text), RunFingerprint.Read(text)));
        Assert.Equal(RunFingerprint.Of(run), RunFingerprint.Read(text));
        Assert.DoesNotContain('\r', text);
    }

    [Fact]
    public void An_equal_evaluation_is_not_a_snapshot_row_and_a_repeat_is_one_row()
    {
        var equal = new Evaluation(Sum, 1, Int7, Int7Actual, Difference.None);
        var rows = DifferenceSnapshot.FromRun([(1, 0, equal), Differing(Sum, 4, "8"), Differing(Sum, 4, "8", 9)]);
        var row = Assert.Single(rows);
        Assert.Equal("seed 1 expression 0", row.FirstSeen);
    }

    [Fact]
    public void A_nondeterministic_outcome_stops_the_run()
        => Assert.Throws<HarnessException>(() => DifferenceSnapshot.FromRun([Differing(Sum, 4, "8"), Differing(Sum, 4, "9")]));

    [Fact]
    public void The_comparison_names_new_changed_and_gone_rows_and_ignores_the_note()
    {
        var snapshot = DifferenceSnapshot.FromRun([Differing(Sum, 4, "8"), Differing(Product, 1, "14")])
            .Select(r => r with { Note = "#1873" }).ToList();

        var same = DifferenceSnapshot.FromRun([Differing(Sum, 4, "8"), Differing(Product, 1, "14")]);
        var (added, changed, gone) = DifferenceSnapshot.Compare(snapshot, same);
        Assert.Equal((0, 0, 0), (added.Count, changed.Count, gone.Count));

        var found = DifferenceSnapshot.FromRun([Differing(Sum, 4, "9"), Differing(Sum, 1, "8")]);
        (added, changed, gone) = DifferenceSnapshot.Compare(snapshot, found);
        Assert.Equal("(c_int + 1)", Assert.Single(added).Expression);
        Assert.Equal(1, Assert.Single(added).RowId);
        Assert.Equal(("8 : ~bigint", "9 : ~bigint"), (Assert.Single(changed).Was.Ppds, changed[0].Now.Ppds));
        Assert.Equal("(c_int * 2)", Assert.Single(gone).Expression);
    }

    [Fact]
    public void A_regenerated_snapshot_keeps_the_note_of_an_unchanged_row_only()
    {
        var earlier = DifferenceSnapshot.FromRun([Differing(Sum, 4, "8"), Differing(Product, 1, "14")])
            .Select(r => r with { Note = "#1873" }).ToList();
        var regenerated = DifferenceSnapshot.FromRun([Differing(Sum, 4, "8"), Differing(Product, 1, "15")], earlier);
        Assert.Equal(["", "#1873"], regenerated.Select(r => r.Note));
    }

    [Fact]
    public void The_committed_snapshot_parses_and_matches_its_header()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Snapshots", DifferenceSnapshot.FileName);
        Assert.True(File.Exists(path), $"The snapshot {DifferenceSnapshot.FileName} is not beside the test assembly.");
        var text = File.ReadAllText(path);
        var rows = DifferenceSnapshot.Parse(text);
        Assert.NotEmpty(rows);
        Assert.Equal(text, DifferenceSnapshot.Format(rows, RunFingerprint.Read(text)));
    }

    [Fact]
    public void The_fingerprint_counts_every_evaluation_and_changes_when_one_is_left_out()
    {
        var equal = (1, 1, new Evaluation(Product, 1, Int7, Int7Actual, Difference.None));
        (int, int, Evaluation)[] run = [Differing(Sum, 4, "8"), equal, Differing(Sum, 1, "8", 2)];
        var all = RunFingerprint.Of(run);
        Assert.Equal(3, all.Evaluations);
        Assert.Equal(all, RunFingerprint.Of(run));

        // Leaving out the equal evaluation changes no snapshot row, but it changes the fingerprint.
        var fewer = RunFingerprint.Of([run[0], run[2]]);
        Assert.Equal(DifferenceSnapshot.FromRun(run), DifferenceSnapshot.FromRun([run[0], run[2]]));
        Assert.NotEqual(all, fewer);
        Assert.NotEqual(all.Sha256, fewer.Sha256);
    }

    [Fact]
    public void A_snapshot_without_a_well_formed_evaluation_line_is_refused()
    {
        var text = DifferenceSnapshot.Format(DifferenceSnapshot.FromRun([Differing(Sum, 4, "8")]), RunFingerprint.Of([Differing(Sum, 4, "8")]));
        var without = string.Join('\n', text.Split('\n').Where(l => !l.StartsWith("# Evaluations: ", StringComparison.Ordinal)));
        Assert.Throws<HarnessException>(() => RunFingerprint.Read(without));
        Assert.Throws<HarnessException>(() => RunFingerprint.Read(text.Replace("# Evaluations: 1.", "# Evaluations: one.", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_duplicated_snapshot_row_is_refused_and_named()
    {
        var text = DifferenceSnapshot.Format(DifferenceSnapshot.FromRun([Differing(Sum, 4, "8")]), RunFingerprint.Of([Differing(Sum, 4, "8")]));
        var lastRow = text.TrimEnd('\n').Split('\n')[^1];
        var fault = Assert.Throws<HarnessException>(() => DifferenceSnapshot.Parse(text + lastRow + "\n"));
        Assert.Contains("row 4: (c_int + 1)", fault.Message);
    }

    [Fact]
    public void An_equal_evaluation_that_later_differs_is_nondeterminism()
    {
        var equal = (1, 0, new Evaluation(Sum, 4, Outcome.Of("8", new SqlTypeInfo("int"), "int"), Outcome.Of("8", null, "int"), Difference.None));
        var fault = Assert.Throws<HarnessException>(() => DifferenceSnapshot.FromRun([equal, Differing(Sum, 4, "9", 5)]));
        Assert.Contains("row 4", fault.Message);
    }

    [Fact]
    public void The_engines_own_cast_failure_never_equals_a_sql_server_conversion_error()
    {
        // The engine cannot cast to tinyint; SQL Server's CAST('x' AS tinyint) is error 245, a conversion error.
        var engine = new PpdsEngine(new Dictionary<int, IReadOnlyDictionary<string, QueryValue>> { [1] = new Dictionary<string, QueryValue>() });
        var actual = engine.Evaluate("CAST('x' AS tinyint)", 1);
        Assert.Equal("error Other (InvalidCastException)", actual.ToString());
        Assert.Equal(Difference.Error, Outcome.Compare(Outcome.Failed(ErrorClass.Conversion, "245"), actual));
    }

    // ── Known differences ─────────────────────────────────────────────

    [Fact]
    public void The_known_list_is_pinned()
    {
        // Removing an entry or a case must be a visible change here, not a silent drop of negative-control rows.
        Assert.Equal(
            [
                "conditional-whole-number-unconverted", "round-whole-number-float", "currency-arithmetic-full-precision",
                "isnull-replacement-unconverted", "round-decimal-overflow-value", "varchar-literal-code-page",
                "float-decimal-literal-comparison", "upper-lower-dotnet-casing", "patindex-end-anchor-line-feed",
                "patindex-wildcard-line-feed", "patindex-character-class",
            ],
            KnownDifferences.All.Select(k => k.Id));
        Assert.Equal([4, 2, 4, 3, 1, 2, 1, 2, 2, 2, 1], KnownDifferences.All.Select(k => k.Cases.Count));
    }

    [Fact]
    public void Every_known_difference_cites_an_issue_and_has_a_case()
    {
        Assert.All(KnownDifferences.All, k =>
        {
            Assert.Matches("^#(1764|1851)", k.Issue);
            Assert.NotEmpty(k.Cases);
            Assert.All(k.Cases, c => Assert.Contains(c.RowId, Operands.RowIds));
        });
        Assert.Equal(KnownDifferences.All.Count, KnownDifferences.All.Select(k => k.Id).Distinct().Count());
    }

    [Fact]
    public void Every_known_case_is_still_wrong_in_the_engine_offline()
    {
        // The pinned PPDS outcomes do not need SQL Server: they are rerun here so that a change to the engine shows up
        // in every build, not only where a server is configured.
        var rows = Operands.Rows.ToDictionary(
            r => r.Id,
            r => (IReadOnlyDictionary<string, QueryValue>)new Dictionary<string, QueryValue>(StringComparer.OrdinalIgnoreCase));
        foreach (var known in KnownDifferences.All.SelectMany(k => k.Cases.Select(c => (k, c))))
        {
            if (Operands.Columns.Any(col => known.c.Expression.Contains(col.Name, StringComparison.Ordinal)))
                continue; // needs the stored row, which only the server can produce
            var actual = new PpdsEngine(rows).Evaluate(known.c.Expression, known.c.RowId);
            Assert.True(actual.ToString() == known.c.PpdsToday, $"{known.k}: {known.c.Expression}: recorded [{known.c.PpdsToday}], now [{actual}]");
        }
    }

    // ── Reference database ────────────────────────────────────────────

    [Fact]
    public void The_skip_reason_names_the_variables_and_nothing_else()
    {
        if (ReferenceServer.SkipReason is { } reason)
        {
            Assert.Contains(ReferenceServer.HostVariable, reason);
            Assert.DoesNotContain("=", reason);
        }
    }

    [Fact]
    public void Operand_table_sql_lists_every_column_and_row()
    {
        Assert.All(Operands.Columns, c => Assert.Contains($"{c.Name} {c.SqlType} NULL", Operands.CreateTableSql()));
        Assert.All(Operands.Rows, r => Assert.Equal(Operands.Columns.Count, r.Literals.Length));
        Assert.StartsWith("INSERT INTO dbo.Operands", Operands.InsertRowsSql());
    }
}
