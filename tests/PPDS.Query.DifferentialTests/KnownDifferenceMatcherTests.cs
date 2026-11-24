using PPDS.Query.DifferentialTests.Harness;
using Xunit;

namespace PPDS.Query.DifferentialTests;

/// <summary>
/// How wide each known-difference matcher is, without a server. Each entry must explain its own defect, recomputed from
/// the operands, and nothing else: the near misses below are defects on the same constructs that must stay new. The
/// negative control proves the same on the real outcomes of every pinned case when a server is configured.
/// </summary>
[Trait("Category", "SqlDifferential")]
public sealed class KnownDifferenceMatcherTests
{
    // ── Building causes ───────────────────────────────────────────────

    private static SqlTypeInfo T(string name, int? precision = null, int? scale = null, int? length = null)
        => name switch
        {
            "decimal" => new SqlTypeInfo(name, precision, scale),
            "money" => new SqlTypeInfo(name, 19, 4),
            "varchar" or "nvarchar" => new SqlTypeInfo(name, Length: length),
            _ => new SqlTypeInfo(name),
        };

    private static Outcome Sql(string value, SqlTypeInfo type) => Outcome.Of(value, type, type.Family);

    private static Outcome Ppds(string value, string? family) => Outcome.Of(value, null, family);

    private static Expr Leaf(string construct, string sql, Kind kind = Kind.Number) => new(construct, kind, sql, []);

    private static Expr Rule(string name, Kind result, params Expr[] children)
        => Expr.Of(RuleTable.Rules.Single(r => r.Name == name && r.Result == result), children);

    private static NodeResult Operand(Expr node, Outcome reference, Outcome actual) => new(node, reference, actual, Outcome.Compare(reference, actual));

    private static NodeDifference Cause(Expr node, Outcome reference, Outcome actual, params NodeResult[] operands)
    {
        var difference = Outcome.Compare(reference, actual);
        Assert.NotEqual(Difference.None, difference);
        Assert.All(operands, o => Assert.Equal(Difference.None, o.Difference));
        return new NodeDifference(node, 1, reference, actual, difference, operands);
    }

    private static readonly SqlTypeInfo Int = T("int");
    private static readonly SqlTypeInfo Money = T("money");
    private static readonly SqlTypeInfo Float = T("float");

    private static NodeResult IntOperand(string value) => Operand(Leaf("literal int", value), Sql(value, Int), Ppds(value, "int"));

    private static NodeResult MoneyColumn(string value) => Operand(Leaf("column money", "c_money"), Sql(value, Money), Ppds(value, "decimal"));

    private static NodeResult TextOperand(string rendered, SqlTypeInfo type) => Operand(Leaf("literal varchar", rendered, Kind.Text), Sql(rendered, type), Ppds(rendered, "text"));

    private static NodeDifference PatIndex(string rule, string text, SqlTypeInfo type, string sqlServer, string ppds)
    {
        var operand = TextOperand(text, type);
        return Cause(Rule(rule, Kind.Number, operand.Node), Sql(sqlServer, Int), Ppds(ppds, "int"), operand);
    }

    // ── Each entry explains its own defect ────────────────────────────

    /// <summary>One canonical cause per entry, with the values the reference server and the engine give.</summary>
    public static TheoryData<string> EntryIds()
    {
        var data = new TheoryData<string>();
        foreach (var id in OwnCauses.Keys)
            data.Add(id);
        return data;
    }

    private static readonly Dictionary<string, Func<NodeDifference>> OwnCauses = new()
    {
        ["conditional-whole-number-unconverted"] = () =>
        {
            NodeResult five = IntOperand("5"), decimalOperand = Operand(Leaf("literal decimal", "2.50"), Sql("2.50", T("decimal", 3, 2)), Ppds("2.50", "decimal"));
            return Cause(Rule("COALESCE", Kind.Number, five.Node, decimalOperand.Node), Sql("5.00", T("decimal", 3, 2)), Ppds("5", "int"), five, decimalOperand);
        },
        ["round-whole-number-float"] = () =>
        {
            var seven = IntOperand("7");
            return Cause(Rule("ROUND(x, 0)", Kind.Number, seven.Node), Sql("7", Int), Ppds("7", "float"), seven);
        },
        ["currency-arithmetic-full-precision"] = () =>
        {
            var money = MoneyColumn("12.3456");
            return Cause(Rule("*", Kind.Number, money.Node, money.Node), Sql("152.4138", Money), Ppds("152.41383936", "decimal"), money, money);
        },
        ["isnull-replacement-unconverted"] = () =>
        {
            var column = Operand(Leaf("column int", "c_int"), Sql("NULL", Int), Ppds("NULL", null));
            var replacement = Operand(Leaf("literal decimal", "2.7"), Sql("2.7", T("decimal", 2, 1)), Ppds("2.7", "decimal"));
            return Cause(Rule("ISNULL", Kind.Number, column.Node, replacement.Node), Sql("2", Int), Ppds("2.7", "decimal"), column, replacement);
        },
        ["round-decimal-overflow-value"] = () =>
        {
            var input = Operand(Leaf("literal decimal", "9.99"), Sql("9.99", T("decimal", 3, 2)), Ppds("9.99", "decimal"));
            return Cause(Rule("ROUND(x, 0)", Kind.Number, input.Node), Outcome.Failed(ErrorClass.Overflow, "8115"), Ppds("10", "decimal"), input);
        },
        ["varchar-literal-code-page"] = () =>
            Cause(Leaf("literal varchar outside the code page", "'\u0101'", Kind.Text), Sql("'a'", T("varchar", length: 1)), Ppds("'{U+0101}'", "text")),
        ["float-decimal-literal-comparison"] = () =>
        {
            var sum = Operand(Leaf("+", "(CAST(0.1 AS float) + CAST(0.2 AS float))"), Sql("0.30000000000000004", Float), Ppds("0.30000000000000004", "float"));
            var literal = Operand(Leaf("literal decimal", "0.3"), Sql("0.3", T("decimal", 1, 1)), Ppds("0.3", "decimal"));
            return Cause(Rule("number = number", Kind.Predicate, sum.Node, literal.Node), Sql("0", Int), Ppds("1", "int"), sum, literal);
        },
        ["upper-lower-dotnet-casing"] = () =>
        {
            var input = TextOperand("'{U+A794}'", T("nvarchar", length: 1));
            return Cause(Rule("UPPER", Kind.Text, input.Node), Sql("'{U+A794}'", T("nvarchar", length: 1)), Ppds("'{U+A7C4}'", "text"), input);
        },
        ["patindex-end-anchor-line-feed"] = () => PatIndex("PATINDEX('%a', x)", "'xa{U+000A}'", T("varchar", length: 3), "0", "2"),
        ["patindex-wildcard-line-feed"] = () => PatIndex("PATINDEX('a%b', x)", "'a{U+000A}b'", T("varchar", length: 3), "1", "0"),
        ["patindex-character-class"] = () => PatIndex("PATINDEX('%[0-9]%', x)", "'abc5'", T("varchar", length: 4), "4", "0"),
    };

    [Theory]
    [MemberData(nameof(EntryIds))]
    public void Each_entry_explains_its_own_defect_and_no_other_entry_does(string id)
    {
        var cause = OwnCauses[id]();
        var explaining = KnownDifferences.All.Where(k => k.Explains(cause)).Select(k => k.Id).ToList();
        Assert.Equal([id], explaining);
    }

    [Fact]
    public void Every_entry_has_a_canonical_cause_here()
        => Assert.Equal(KnownDifferences.All.Select(k => k.Id).Order(StringComparer.Ordinal), OwnCauses.Keys.Order(StringComparer.Ordinal));

    // ── Near misses: other defects on the same constructs stay new ────

    /// <summary>Each near miss: what it is, and the cause.</summary>
    public static TheoryData<string> NearMissNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in NearMisses.Keys)
            data.Add(name);
        return data;
    }

    private static readonly Dictionary<string, Func<NodeDifference>> NearMisses = new()
    {
        ["IIF picks the wrong branch while its predicate agrees"] = () =>
        {
            var predicate = Operand(Leaf("number = number", "1 = 0", Kind.Predicate), Sql("0", Int), Ppds("0", "int"));
            NodeResult five = IntOperand("5"), other = Operand(Leaf("literal decimal", "2.5"), Sql("2.5", T("decimal", 2, 1)), Ppds("2.5", "decimal"));
            return Cause(Rule("IIF", Kind.Number, predicate.Node, five.Node, other.Node), Sql("2.5", T("decimal", 11, 1)), Ppds("5", "int"), predicate, five, other);
        },
        ["COALESCE returns NULL where SQL Server returns the second value"] = () =>
        {
            var column = Operand(Leaf("column int", "c_int"), Sql("NULL", Int), Ppds("NULL", null));
            var other = Operand(Leaf("literal decimal", "2.5"), Sql("2.5", T("decimal", 2, 1)), Ppds("2.5", "decimal"));
            return Cause(Rule("COALESCE", Kind.Number, column.Node, other.Node), Sql("2.5", T("decimal", 11, 1)), Ppds("NULL", null), column, other);
        },
        ["a money product a thousand times off"] = () =>
        {
            var money = MoneyColumn("12.3456");
            return Cause(Rule("*", Kind.Number, money.Node, money.Node), Sql("152.4138", Money), Ppds("152413.83936", "decimal"), money, money);
        },
        ["a money result with fewer than four places (not full precision)"] = () =>
        {
            NodeResult money = MoneyColumn("2.0000"), two = IntOperand("2");
            return Cause(Rule("/", Kind.Number, money.Node, two.Node), Sql("1.0000", Money), Ppds("1", "decimal"), money, two);
        },
        ["COALESCE returns a float that is not the whole number converted"] = () =>
        {
            var column = Operand(Leaf("column bigint", "c_bigint"), Sql("9223372036854775807", T("bigint")), Ppds("9223372036854775807", "bigint"));
            var other = Operand(Leaf("literal float", "1.5E0"), Sql("1.5", Float), Ppds("1.5", "float"));
            return Cause(Rule("COALESCE", Kind.Number, column.Node, other.Node), Sql("9.2E+18", Float), Ppds("9223372036854775807", "bigint"), column, other);
        },
        ["ROUND of a whole number with a wrong value"] = () =>
        {
            var seven = IntOperand("7");
            return Cause(Rule("ROUND(x, -1)", Kind.Number, seven.Node), Sql("10", Int), Ppds("7", "float"), seven);
        },
        ["UPPER drops a trailing space"] = () =>
        {
            var input = TextOperand("' x '", T("nvarchar", length: 3));
            return Cause(Rule("UPPER", Kind.Text, input.Node), Sql("' X '", T("nvarchar", length: 3)), Ppds("'X'", "text"), input);
        },
        ["a float comparison inverted"] = () =>
        {
            var column = Operand(Leaf("column float", "c_float"), Sql("0.1", Float), Ppds("0.1", "float"));
            var literal = Operand(Leaf("literal decimal", "0.1"), Sql("0.1", T("decimal", 1, 1)), Ppds("0.1", "decimal"));
            return Cause(Rule("number < number", Kind.Predicate, column.Node, literal.Node), Sql("0", Int), Ppds("1", "int"), column, literal);
        },
        ["ISNULL returns a value that is neither operand"] = () =>
        {
            var column = Operand(Leaf("column int", "c_int"), Sql("NULL", Int), Ppds("NULL", null));
            var replacement = Operand(Leaf("literal decimal", "2.7"), Sql("2.7", T("decimal", 2, 1)), Ppds("2.7", "decimal"));
            return Cause(Rule("ISNULL", Kind.Number, column.Node, replacement.Node), Sql("2", Int), Ppds("2.8", "decimal"), column, replacement);
        },
        ["ROUND that overflows, with a wrongly rounded value"] = () =>
        {
            var input = Operand(Leaf("literal decimal", "9.99"), Sql("9.99", T("decimal", 3, 2)), Ppds("9.99", "decimal"));
            return Cause(Rule("ROUND(x, 0)", Kind.Number, input.Node), Outcome.Failed(ErrorClass.Overflow, "8115"), Ppds("11", "decimal"), input);
        },
        ["a varchar literal changed into something else"] = () =>
            Cause(Leaf("literal varchar outside the code page", "'\u0101'", Kind.Text), Sql("'a'", T("varchar", length: 1)), Ppds("'b'", "text")),
        ["PATINDEX at the end of a value with a line feed, at a wrong position"] = () =>
            PatIndex("PATINDEX('%a', x)", "'xa{U+000A}'", T("varchar", length: 3), "0", "1"),
        ["PATINDEX finds no match in a value without a line feed"] = () =>
            PatIndex("PATINDEX('a%b', x)", "'axb'", T("varchar", length: 3), "1", "0"),
        ["PATINDEX with a class, at a wrong position"] = () =>
            PatIndex("PATINDEX('%[0-9]%', x)", "'abc5'", T("varchar", length: 4), "4", "3"),
        ["ROUND of a whole number that throws"] = () =>
        {
            var column = Operand(Leaf("column int", "c_int"), Sql("2147483647", Int), Ppds("2147483647", "int"));
            return Cause(Rule("ROUND(x, -1)", Kind.Number, column.Node), Outcome.Failed(ErrorClass.Overflow, "8115"), Outcome.Failed(ErrorClass.Other, "ArgumentOutOfRangeException"), column);
        },
    };

    [Fact]
    public void A_whole_number_that_loses_digits_as_a_float_is_still_its_entry()
    {
        // SQL Server converts the bigint branch to float (9.223372036854776E+18); the engine returns the bigint.
        var column = Operand(Leaf("column bigint", "c_bigint"), Sql("9223372036854775807", T("bigint")), Ppds("9223372036854775807", "bigint"));
        var other = Operand(Leaf("literal float", "1.5E0"), Sql("1.5", Float), Ppds("1.5", "float"));
        var coalesce = Cause(Rule("COALESCE", Kind.Number, column.Node, other.Node), Sql("9.223372036854776E+18", Float), Ppds("9223372036854775807", "bigint"), column, other);
        Assert.Equal(["conditional-whole-number-unconverted"], KnownDifferences.All.Where(k => k.Explains(coalesce)).Select(k => k.Id));

        // ROUND of the same bigint returns the nearest float.
        var round = Cause(Rule("ROUND(x, 0)", Kind.Number, column.Node), Sql("9223372036854775807", T("bigint")), Ppds("9.223372036854776E+18", "float"), column);
        Assert.Equal(["round-whole-number-float"], KnownDifferences.All.Where(k => k.Explains(round)).Select(k => k.Id));
    }

    [Theory]
    [MemberData(nameof(NearMissNames))]
    public void A_near_miss_stays_new(string name)
    {
        var cause = NearMisses[name]();
        var explaining = KnownDifferences.All.Where(k => k.Explains(cause)).Select(k => k.Id).ToList();
        Assert.True(explaining.Count == 0, $"{name}: explained by {string.Join(", ", explaining)}; it must stay new.");
    }

    // ── The list itself ───────────────────────────────────────────────

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
        Assert.Equal([4, 2, 3, 3, 1, 2, 1, 2, 2, 2, 1], KnownDifferences.All.Select(k => k.Cases.Count));
    }

    [Fact]
    public void Each_case_tree_is_its_expression()
    {
        static string Bare(string sql) => new(sql.Where(c => c is not ('(' or ')' or ' ')).ToArray());
        Assert.All(
            KnownDifferences.All.SelectMany(k => k.Cases),
            c => Assert.Equal(Bare(c.Expression), Bare(c.Tree.ScalarSql)));
    }
}
