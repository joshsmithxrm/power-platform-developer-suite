using System.Data.SqlTypes;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Dataverse.Query;
using PPDS.Query.DifferentialTests.Harness;
using PPDS.Query.Parsing;
using Xunit;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// Tests of the harness itself. They need no server, so they run in every CI build: the generator is deterministic and
/// writes SQL the product parses, values render exactly, the comparison sees each kind of difference, and a known
/// difference cannot absorb a new one.
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

    // ── Runner and known differences ──────────────────────────────────

    private sealed class FakeEngine(Dictionary<string, Outcome> outcomes) : IExpressionEngine
    {
        public Outcome Evaluate(string expression, int rowId) => outcomes[expression];
    }

    private static Expr Leaf(string sql, string construct = "literal int") => new(construct, Kind.Number, sql, []);

    private static readonly Outcome Int7 = Outcome.Of("7", new SqlTypeInfo("int"), "int");
    private static readonly Outcome Int7Actual = Outcome.Of("7", null, "int");

    [Fact]
    public void The_cause_is_the_smallest_node_whose_operands_agree()
    {
        // ROUND(7, 0) / 2: ROUND differs (float), its operand agrees; the division differs only because of it.
        var round = new Expr("ROUND(x, 0)", Kind.Number, "ROUND(7, 0)", [Leaf("7")]);
        var two = Leaf("2");
        var division = new Expr("/", Kind.Number, "(ROUND(7, 0) / 2)", [round, two]);
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

        var verdict = new DifferentialRunner(reference, actual, KnownDifferences.All).Check(division, 1);

        var cause = Assert.Single(verdict.Causes);
        Assert.Same(round, cause.Difference.Node);
        Assert.Same(KnownDifferences.RoundWholeNumberFloat, cause.Known);
        Assert.True(verdict.IsExpected);
    }

    [Fact]
    public void A_new_difference_beside_a_known_one_is_reported_as_new()
    {
        // ROUND(7, 0) + (2147483647 + 1): the ROUND cause is known; the int overflow beside it is not, so the whole
        // evaluation is new and the known entry does not hide it.
        var round = new Expr("ROUND(x, 0)", Kind.Number, "ROUND(7, 0)", [Leaf("7")]);
        var max = Leaf("2147483647");
        var one = Leaf("1");
        var overflow = new Expr("+", Kind.Number, "(2147483647 + 1)", [max, one]);
        var sum = new Expr("+", Kind.Number, "(ROUND(7, 0) + (2147483647 + 1))", [round, overflow]);
        var intType = new SqlTypeInfo("int");
        var reference = new FakeEngine(new()
        {
            ["7"] = Int7, ["1"] = Outcome.Of("1", intType, "int"), ["2147483647"] = Outcome.Of("2147483647", intType, "int"),
            ["ROUND(7, 0)"] = Int7, ["(2147483647 + 1)"] = Outcome.Failed(ErrorClass.Overflow, "8115"),
            ["(ROUND(7, 0) + (2147483647 + 1))"] = Outcome.Failed(ErrorClass.Overflow, "8115"),
        });
        var actual = new FakeEngine(new()
        {
            ["7"] = Int7Actual, ["1"] = Outcome.Of("1", null, "int"), ["2147483647"] = Outcome.Of("2147483647", null, "int"),
            ["ROUND(7, 0)"] = Outcome.Of("7", null, "float"), ["(2147483647 + 1)"] = Outcome.Of("2147483648", null, "bigint"),
            ["(ROUND(7, 0) + (2147483647 + 1))"] = Outcome.Of("2147483655", null, "float"),
        });

        var verdict = new DifferentialRunner(reference, actual, KnownDifferences.All).Check(sum, 1);

        Assert.Equal(2, verdict.Causes.Count);
        Assert.True(verdict.IsNew);
        var unknown = Assert.Single(verdict.Causes, c => c.Known is null);
        Assert.Equal("+(literal int, literal int): PPDS returns a value where SQL Server raises Overflow", unknown.Difference.Signature);
    }

    [Fact]
    public void A_known_entry_does_not_accept_another_kind_of_difference_on_its_construct()
    {
        // ROUND of a whole number that SQL Server overflows and the engine rejects with an exception: an error
        // difference, not the float result the entry records.
        var cause = new NodeDifference(
            new Expr("ROUND(x, -1)", Kind.Number, "ROUND(c_int, -1)", [Leaf("c_int", "column int")]),
            4,
            Outcome.Failed(ErrorClass.Overflow, "8115"),
            Outcome.Failed(ErrorClass.Other, "ArgumentOutOfRangeException"),
            Difference.Error,
            [new NodeResult(Leaf("c_int", "column int"), Outcome.Of("2147483647", new SqlTypeInfo("int"), "int"), Outcome.Of("2147483647", null, "int"), Difference.None)]);

        Assert.DoesNotContain(KnownDifferences.All, k => k.Explains(cause));
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
