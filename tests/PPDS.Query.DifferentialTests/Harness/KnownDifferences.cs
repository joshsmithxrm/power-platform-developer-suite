using System.Globalization;
using System.Text;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// The differences between today's engine and SQL Server that #1764 and #1851 already record: the negative control.
/// Every case here must still differ from SQL Server, exactly as pinned. A fix makes its cases fail with "stopped
/// differing", and the fix's pull request removes them (and the entry when no case is left). Nothing else may be added
/// here without an issue that records it.
/// </summary>
/// <remarks>
/// <para>
/// Each matcher recomputes its defect: it accepts a cause only when the engine's result is what that defect produces
/// from the operands and SQL Server's result is what SQL Server produces from the same operands. A different defect on
/// the same construct (a wrong branch, a NULL, a value a thousand times off, a dropped space) stays new.
/// </para>
/// <para>Bullets of #1764 that are not here, and why:</para>
/// <list type="bullet">
/// <item>"Whole-number literals above the 32-bit range are treated as integers and divide as integers": does not
/// reproduce on <c>main</c>. The parser reads such a literal as a numeric literal and the engine computes it as a .NET
/// decimal (<c>3000000000 / 7</c> gives 428571428.57142857142857142857, not 428571428). The remaining difference there
/// is the scale of a decimal division, which the generated run reports on its own.</item>
/// <item>"A decimal written to a currency column skips the conversion to 4 places" and "values that exceed the target
/// column's length, range or precision are not checked before the first write": these are about the write to a
/// Dataverse column, not the value of an expression, and are outside this harness.</item>
/// <item>"The same expressions in a SELECT return the same wrong values": every case here is evaluated on the path a
/// SELECT uses, so each one is also that bullet.</item>
/// </list>
/// </remarks>
public static class KnownDifferences
{
    // ── Value helpers ─────────────────────────────────────────────────

    private static bool IsWholeNumber(SqlTypeInfo? type) => type?.Name is "int" or "bigint" or "smallint" or "tinyint";

    private static decimal? Dec(string? value)
        => value is not null && decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? Dbl(string? value)
        => value is not null && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static bool NumericallyEqual(string? a, string? b) => Dec(a) is { } x && Dec(b) is { } y && x == y;

    /// <summary>True when <paramref name="asFloat"/> is the whole number <paramref name="whole"/> converted to float.</summary>
    private static bool IsFloatOf(string? asFloat, string? whole)
        => Dbl(asFloat) is { } f && Dec(whole) is { } w && f == (double)w; // CodeQL [cs/equality-on-floats] the conversion is exact or it is not this defect

    private static int Scale(string? value)
    {
        var dot = value?.IndexOf('.') ?? -1;
        return dot < 0 ? 0 : value!.Length - dot - 1;
    }

    private static int IntegerDigits(decimal value)
        => decimal.Truncate(Math.Abs(value)).ToString(CultureInfo.InvariantCulture).TrimStart('0').Length;

    /// <summary>Rounds half away from zero to <paramref name="length"/> places; a negative length rounds left of the point.</summary>
    private static decimal RoundTo(decimal value, int length)
    {
        if (length >= 0)
            return Math.Round(value, length, MidpointRounding.AwayFromZero);
        var factor = (decimal)Math.Pow(10, -length);
        return Math.Round(value / factor, 0, MidpointRounding.AwayFromZero) * factor;
    }

    private static decimal? TruncateTo4(decimal value)
    {
        try
        {
            return decimal.Truncate(value * 10000m) / 10000m;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    /// <summary>The text a rendered text value stands for: quotes removed, <c>{U+XXXX}</c> turned back into the character.</summary>
    public static string? Unrender(string? rendered)
    {
        if (rendered is null || rendered.Length < 2 || rendered[0] != '\'' || rendered[^1] != '\'')
            return null;
        var inner = rendered[1..^1];
        var text = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (i + 8 <= inner.Length && string.CompareOrdinal(inner, i, "{U+", 0, 3) == 0 && inner[i + 7] == '}')
            {
                text.Append((char)int.Parse(inner.Substring(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                i += 7;
            }
            else
            {
                text.Append(inner[i]);
            }
        }
        return text.ToString();
    }

    /// <summary>True when <paramref name="converted"/> is <paramref name="value"/> converted to <paramref name="type"/> the way SQL Server converts it.</summary>
    private static bool ConvertsTo(string? value, SqlTypeInfo? type, string? converted)
    {
        if (type is null || value is null || converted is null)
            return false;
        switch (type.Name)
        {
            case "int" or "bigint" or "smallint" or "tinyint":
                return Dec(value) is { } i && Dec(converted) is { } ci && decimal.Truncate(i) == ci;
            case "decimal":
                return Dec(value) is { } d && Dec(converted) is { } cd && type.Scale is { } s && RoundTo(d, s) == cd;
            case "money" or "smallmoney":
                return Dec(value) is { } m && Dec(converted) is { } cm && RoundTo(m, 4) == cm;
            case "float" or "real":
                return Dbl(value) is { } f && Dbl(converted) is { } cf && f == cf; // CodeQL [cs/equality-on-floats] the conversion is exact or it is not this defect
            case "varchar" or "nvarchar" or "char" or "nchar":
                return Unrender(value) is { } t && Unrender(converted) is { } ct && type.Length is { } n
                       && ct == (n < 0 || t.Length <= n ? t : t[..n]);
            default:
                return false;
        }
    }

    private static int? RoundLength(string construct)
    {
        // "ROUND(x, -3)" -> -3
        var comma = construct.IndexOf(", ", StringComparison.Ordinal);
        return construct.StartsWith("ROUND(", StringComparison.Ordinal) && comma > 0
               && int.TryParse(construct[(comma + 2)..^1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var length)
            ? length
            : null;
    }

    private static bool IsPatIndex(NodeDifference d) => d.Node.Construct.StartsWith("PATINDEX(", StringComparison.Ordinal);

    private static string Pattern(NodeDifference d) => d.Node.Construct["PATINDEX('".Length..d.Node.Construct.IndexOf("',", StringComparison.Ordinal)];

    private static string? PatIndexText(NodeDifference d) => Unrender(d.Children[0].Reference.Value);

    /// <summary>The value operands of a conditional: both operands of COALESCE, the THEN and ELSE values of IIF and CASE.</summary>
    private static IEnumerable<NodeResult> Branches(NodeDifference d) => d.Node.Construct == "COALESCE" ? d.Children : d.Children.Skip(1);

    /// <summary>The comparison of a predicate construct (<c>number = number</c> -> <c>=</c>).</summary>
    private static Func<double, double, bool>? Comparison(string construct) => construct switch
    {
        "number = number" => (a, b) => a == b, // CodeQL [cs/equality-on-floats] SQL Server compares floats exactly
        "number <> number" => (a, b) => a != b, // CodeQL [cs/equality-on-floats] SQL Server compares floats exactly
        "number < number" => (a, b) => a < b,
        "number > number" => (a, b) => a > b,
        _ => null,
    };

    private static string Bit(bool value) => value ? "1" : "0";

    // ── Building the cases' trees ─────────────────────────────────────

    private static Expr Num(string sql) => new(
        sql == "NULL" ? "NULL"
        : sql.Contains('E', StringComparison.OrdinalIgnoreCase) ? "literal float"
        : sql.Contains('.', StringComparison.Ordinal) ? "literal decimal"
        : long.Parse(sql, CultureInfo.InvariantCulture) > int.MaxValue ? "literal int above int range"
        : "literal int",
        Kind.Number, sql, []);

    private static Expr Text(string sql, string construct) => new(construct, Kind.Text, sql, []);

    private static Expr Col(string name)
    {
        var column = Operands.Columns.Single(c => c.Name == name);
        return new Expr("column " + column.SqlType, column.Kind, name, []);
    }

    private static Expr Rule(string name, Kind result, params Expr[] children)
        => Expr.Of(RuleTable.Rules.Single(r => r.Name == name && r.Result == result), children);

    private static Expr Rule(string name, params Expr[] children) => Rule(name, Kind.Number, children);

    private static Expr Node(string construct, Kind kind, string sql, params Expr[] children) => new(construct, kind, sql, children);

    private static readonly Expr LineFeed = Node("CHAR(10)", Kind.Text, "CHAR(10)");

    // ── The entries ───────────────────────────────────────────────────

    /// <summary>#1764: a conditional returns its whole-number branch unconverted.</summary>
    public static readonly KnownDifference ConditionalUnconverted = new(
        "conditional-whole-number-unconverted",
        "#1764, first known case",
        "COALESCE, IIF and CASE mixing a whole number with decimal, currency or float return the whole number unconverted, so a following / divides as integers.",
        [
            new("COALESCE(5, 2.50) / 2", 1, "2.500000 : decimal(7,6)", "2 : ~bigint",
                Rule("/", Rule("COALESCE", Num("5"), Num("2.50")), Num("2"))),
            new("IIF(1 = 1, 5, 2.50) / 2", 1, "2.500000 : decimal(7,6)", "2 : ~bigint",
                Rule("/", Rule("IIF", Rule("number = number", Kind.Predicate, Num("1"), Num("1")), Num("5"), Num("2.50")), Num("2"))),
            new("CASE WHEN 1 = 1 THEN 7 ELSE 2.5E0 END / 2", 1, "3.5 : float", "3 : ~bigint",
                Rule("/", Rule("CASE", Rule("number = number", Kind.Predicate, Num("1"), Num("1")), Num("7"), Num("2.5E0")), Num("2"))),
            new("COALESCE(c_int, c_money) / 2", 1, "3.5000 : money", "3 : ~bigint",
                Rule("/", Rule("COALESCE", Col("c_int"), Col("c_money")), Num("2"))),
        ],
        // The engine returns a whole-number branch as it is; SQL Server returns the same branch converted to the result
        // type, so the two are numerically equal and differ in type or scale.
        d => d.Node.Construct is "COALESCE" or "IIF" or "CASE"
             && d.Node.Kind == Kind.Number
             && !d.Difference.HasFlag(Difference.Error)
             && d.Reference.Type?.Family is "decimal" or "float"
             && Branches(d).Any(b => IsWholeNumber(b.Reference.Type)
                                     && b.Actual.Value is { } value && value != "NULL"
                                     && d.Actual.Value == value
                                     && d.Actual.Family == b.Actual.Family
                                     && (NumericallyEqual(d.Reference.Value, b.Reference.Value)
                                         || (d.Reference.Type?.Family == "float" && IsFloatOf(d.Reference.Value, b.Reference.Value)))));

    /// <summary>#1764: ROUND of a whole number returns a float.</summary>
    public static readonly KnownDifference RoundWholeNumberFloat = new(
        "round-whole-number-float",
        "#1764, second known case",
        "ROUND of a whole number returns a floating-point value, so a following / or * computes in float.",
        [
            new("ROUND(7, 0) / 2", 1, "3 : int", "3.5 : ~float",
                Rule("/", Rule("ROUND(x, 0)", Num("7")), Num("2"))),
            new("ROUND(c_int, 0)", 1, "7 : int", "7 : ~float",
                Rule("ROUND(x, 0)", Col("c_int"))),
        ],
        // The same number, as a float (exactly, or as the nearest float when the whole number has more digits).
        d => d.Node.Construct.StartsWith("ROUND(", StringComparison.Ordinal)
             && !d.Difference.HasFlag(Difference.Error)
             && d.Difference.HasFlag(Difference.Type)
             && IsWholeNumber(d.Children[0].Reference.Type)
             && d.Actual.Family == "float"
             && (NumericallyEqual(d.Reference.Value, d.Actual.Value) || IsFloatOf(d.Actual.Value, d.Reference.Value)));

    /// <summary>#1764: currency arithmetic keeps more than 4 places.</summary>
    public static readonly KnownDifference CurrencyFullPrecision = new(
        "currency-arithmetic-full-precision",
        "#1764, third known case",
        "Arithmetic on currency keeps full decimal precision; SQL Server keeps 4 places after each operation (a single division truncates).",
        [
            // Over the money column (12.3456), whose value both engines read alike; a CAST to money would itself differ
            // in scale first and become the cause.
            new("c_money / 7", 1, "1.7636 : money", "1.7636571428571428571428571429 : ~decimal",
                Rule("/", Col("c_money"), Num("7"))),
            new("(c_money / 7) * 7", 1, "12.3452 : money", "12.345600000000000000000000000 : ~decimal",
                Rule("*", Rule("/", Col("c_money"), Num("7")), Num("7"))),
            new("c_money * c_money", 1, "152.4138 : money", "152.41383936 : ~decimal",
                Rule("*", Col("c_money"), Col("c_money"))),
        ],
        // More than 4 places, and SQL Server's value is the engine's cut to 4 places (truncated or rounded).
        d => d.Node.Construct is "+" or "-" or "*" or "/"
             && d.Difference == Difference.Value
             && d.Reference.Type?.Name == "money"
             && d.Children.Any(c => c.Reference.Type?.Name == "money")
             && d.Actual.Family == "decimal"
             && Scale(d.Actual.Value) > 4
             && Dec(d.Reference.Value) is { } reference && Dec(d.Actual.Value) is { } actual
             && Math.Abs(reference - actual) < 0.0001m
             && (reference == TruncateTo4(actual) || reference == RoundTo(actual, 4)));

    /// <summary>#1764: ISNULL does not convert its replacement to the first operand's type.</summary>
    public static readonly KnownDifference IsNullUnconverted = new(
        "isnull-replacement-unconverted",
        "#1764, fourth known case",
        "ISNULL returns its second value unconverted; SQL Server converts it to the first value's type, precision, scale and length.",
        [
            new("ISNULL(CAST(NULL AS varchar(3)), 'abcdef')", 1, "'abc' : varchar(3)", "'abcdef' : ~text",
                Rule("ISNULL", Kind.Text, Node("CAST AS varchar(3)", Kind.Text, "CAST(NULL AS varchar(3))", Num("NULL")), Text("'abcdef'", "literal varchar"))),
            new("ISNULL(c_int, 2.7)", 6, "2 : int", "2.7 : ~decimal",
                Rule("ISNULL", Col("c_int"), Num("2.7"))),
            new("ISNULL(c_dec2, 1.005)", 6, "1.01 : decimal(12,2)", "1.005 : ~decimal",
                Rule("ISNULL", Col("c_dec2"), Num("1.005"))),
        ],
        // The first value is NULL; the engine returns the replacement as it is, SQL Server the replacement converted to
        // the first value's type.
        d => d.Node.Construct == "ISNULL"
             && !d.Difference.HasFlag(Difference.Error)
             && d.Children[0].Reference.Value == "NULL"
             && d.Reference.Type == d.Children[0].Reference.Type
             && d.Reference.Type != d.Children[1].Reference.Type
             && d.Actual.Value is { } value && value != "NULL"
             && value == d.Children[1].Actual.Value
             && ConvertsTo(d.Children[1].Reference.Value, d.Reference.Type, d.Reference.Value));

    /// <summary>#1764: ROUND that overflows its decimal type returns a value.</summary>
    public static readonly KnownDifference RoundOverflowValue = new(
        "round-decimal-overflow-value",
        "#1764, fifth known case",
        "ROUND that overflows its decimal type returns a value where SQL Server raises an overflow error.",
        [
            new("ROUND(9.99, 0)", 1, "error Overflow (8115)", "10 : ~decimal",
                Rule("ROUND(x, 0)", Num("9.99"))),
        ],
        // The engine's value is the correctly rounded input, and it has more integer digits than the input's type holds.
        d => d.Node.Construct.StartsWith("ROUND(", StringComparison.Ordinal)
             && d.Reference.Error == ErrorClass.Overflow
             && !d.Actual.IsError
             && d.Children[0].Reference.Type is { Name: "decimal", Precision: { } precision, Scale: { } scale }
             && RoundLength(d.Node.Construct) is { } length
             && Dec(d.Children[0].Reference.Value) is { } input && Dec(d.Actual.Value) is { } actual
             && RoundTo(input, length) == actual
             && IntegerDigits(actual) > precision - scale);

    /// <summary>#1764: a text literal without N is not converted through the code page.</summary>
    public static readonly KnownDifference VarcharLiteralCodePage = new(
        "varchar-literal-code-page",
        "#1764, seventh known case",
        "A text literal without the N prefix keeps characters its code page cannot hold; SQL Server converts them.",
        [
            new("'\u0101'", 1, "'a' : varchar(1)", "'{U+0101}' : ~text",
                Text("'\u0101'", "literal varchar outside the code page")),
            new("UPPER('\u0101')", 1, "'A' : varchar(1)", "'{U+0100}' : ~text",
                Rule("UPPER", Kind.Text, Text("'\u0101'", "literal varchar outside the code page"))),
        ],
        // The engine keeps the literal's characters exactly; SQL Server has replaced the ones outside the code page.
        d => d.Node.IsLeaf
             && d.Node.Construct == "literal varchar outside the code page"
             && d.Difference == Difference.Value
             && d.Actual.Value == Outcome.Render(d.Node.Sql[1..^1].Replace("''", "'", StringComparison.Ordinal))
             && Unrender(d.Reference.Value) is { } converted
             && converted.Length == d.Node.Sql.Length - 2
             && converted.All(c => c < 0x80));

    /// <summary>#1764: a float compared with a decimal literal is compared at 15 significant digits.</summary>
    public static readonly KnownDifference FloatDecimalComparison = new(
        "float-decimal-literal-comparison",
        "#1764, ninth known case",
        "A float compared with a decimal literal inside IIF or CASE is compared at 15 significant digits; SQL Server converts the literal to float and compares exactly.",
        [
            new("IIF(CAST(0.1 AS float) + CAST(0.2 AS float) = 0.3, 1, 0)", 1, "0 : int", "1 : ~int",
                Rule("IIF",
                    Rule("number = number", Kind.Predicate, Rule("+", Rule("CAST AS float", Num("0.1")), Rule("CAST AS float", Num("0.2"))), Num("0.3")),
                    Num("1"), Num("0"))),
        ],
        // SQL Server's result is the exact comparison; the engine's is the comparison of the float rounded to 15
        // significant digits.
        d => d.Node.Kind == Kind.Predicate
             && d.Difference == Difference.Value
             && Comparison(d.Node.Construct) is { } compare
             && d.Children.Count == 2
             && d.Children.Count(c => c.Reference.Type?.Family == "float") == 1
             && d.Children.Count(c => c.Node.Construct == "literal decimal") == 1
             && Dbl(d.Children[0].Reference.Value) is { } left && Dbl(d.Children[1].Reference.Value) is { } right
             && d.Reference.Value == Bit(compare(left, right))
             && d.Actual.Value == Bit(compare(
                 d.Children[0].Reference.Type?.Family == "float" ? double.Parse(left.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : left,
                 d.Children[1].Reference.Type?.Family == "float" ? double.Parse(right.ToString("G15", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : right)));

    /// <summary>#1764: UPPER and LOWER use .NET's casing rules.</summary>
    public static readonly KnownDifference DotNetCasing = new(
        "upper-lower-dotnet-casing",
        "#1764, eleventh known case",
        "UPPER and LOWER use .NET's casing rules, not the collation's: a letter the collation has no case pair for is changed.",
        [
            new("UPPER(N'\uA794')", 1, "'{U+A794}' : nvarchar(1)", "'{U+A7C4}' : ~text",
                Rule("UPPER", Kind.Text, Text("N'\uA794'", "literal nvarchar without a case pair in the collation"))),
            new("LOWER(N'\uA7C4')", 1, "'{U+A7C4}' : nvarchar(1)", "'{U+A794}' : ~text",
                Rule("LOWER", Kind.Text, Text("N'\uA7C4'", "literal nvarchar"))),
        ],
        // The engine's value is .NET's invariant casing of the input; SQL Server agrees on every ASCII character and
        // differs only on characters outside ASCII.
        d => d.Node.Construct is "UPPER" or "LOWER"
             && d.Difference == Difference.Value
             && Unrender(d.Children[0].Reference.Value) is { } input
             && Unrender(d.Actual.Value) is { } actual
             && Unrender(d.Reference.Value) is { } reference
             && actual == (d.Node.Construct == "UPPER" ? input.ToUpperInvariant() : input.ToLowerInvariant())
             && reference.Length == input.Length && actual.Length == input.Length
             && Enumerable.Range(0, input.Length).All(i => input[i] >= 0x80 || reference[i] == actual[i])
             && Enumerable.Range(0, input.Length).Any(i => input[i] >= 0x80 && reference[i] != actual[i]));

    /// <summary>#1851 (1): the end anchor of a PATINDEX pattern matches before a final line feed.</summary>
    public static readonly KnownDifference PatIndexEndAnchor = new(
        "patindex-end-anchor-line-feed",
        "#1851, case 1",
        "PATINDEX with a pattern that does not end in % finds a match in a value that ends in a line feed.",
        [
            new("PATINDEX('%a', 'xa' + CHAR(10))", 1, "0 : int", "2 : ~int",
                Rule("PATINDEX('%a', x)", Rule("text + text", Kind.Text, Text("'xa'", "literal varchar"), LineFeed))),
            new("PATINDEX('%a', c_text)", 7, "0 : int", "2 : ~int",
                Rule("PATINDEX('%a', x)", Col("c_text"))),
        ],
        // A pattern of % and literal characters; the literal text ends just before the final line feed, and the engine
        // reports its position there, where SQL Server finds no match.
        d => IsPatIndex(d)
             && d.Difference == Difference.Value
             && Pattern(d) is { Length: > 1 } pattern && pattern[0] == '%' && pattern[1..].All(c => c is not ('%' or '_' or '[' or ']'))
             && PatIndexText(d) is { } text && text.EndsWith('\n')
             && text.Length > pattern.Length - 1
             && string.Equals(text[^pattern.Length..^1], pattern[1..], StringComparison.OrdinalIgnoreCase)
             && d.Reference.Value == "0"
             && d.Actual.Value == (text.Length - (pattern.Length - 1)).ToString(CultureInfo.InvariantCulture));

    /// <summary>#1851 (2): % and _ do not match a line feed.</summary>
    public static readonly KnownDifference PatIndexLineBreak = new(
        "patindex-wildcard-line-feed",
        "#1851, case 2",
        "PATINDEX's % and _ do not match a line feed, so a match that has to cross a line break is not found.",
        [
            new("PATINDEX('a%b', 'a' + CHAR(10) + 'b')", 1, "1 : int", "0 : ~int",
                Rule("PATINDEX('a%b', x)", Rule("text + text", Kind.Text, Rule("text + text", Kind.Text, Text("'a'", "literal varchar"), LineFeed), Text("'b'", "literal varchar")))),
            new("PATINDEX('a%b', c_ntext)", 7, "1 : int", "0 : ~int",
                Rule("PATINDEX('a%b', x)", Col("c_ntext"))),
        ],
        // SQL Server finds a match; the engine finds none; and with every line feed replaced by a space the engine's
        // wildcards would have had a character to match, so the line feed is what stopped it. (Without a pattern
        // matcher of its own the harness cannot say more.)
        d => IsPatIndex(d)
             && d.Difference == Difference.Value
             && Pattern(d) is { } pattern && !pattern.Contains('[') && (pattern.Contains('%') || pattern.Contains('_'))
             && PatIndexText(d) is { } text && text.Contains('\n')
             && Dec(d.Reference.Value) is { } position && position > 0
             && text[((int)position - 1)..].Contains('\n')
             && d.Actual.Value == "0");

    /// <summary>#1851 (3): character classes are matched as literal text.</summary>
    public static readonly KnownDifference PatIndexCharacterClass = new(
        "patindex-character-class",
        "#1851, case 3",
        "PATINDEX matches [0-9], [a-z] and [^x] as literal text.",
        [
            new("PATINDEX('%[0-9]%', 'abc5')", 1, "4 : int", "0 : ~int",
                Rule("PATINDEX('%[0-9]%', x)", Text("'abc5'", "literal varchar"))),
        ],
        // The class read as literal text does not occur in the value, so the engine finds nothing; SQL Server finds it.
        d => IsPatIndex(d)
             && d.Difference == Difference.Value
             && Pattern(d) is { } pattern && pattern.Contains('[')
             && PatIndexText(d) is { } text
             && !text.Contains(pattern.Trim('%'), StringComparison.OrdinalIgnoreCase)
             && Dec(d.Reference.Value) is > 0m
             && d.Actual.Value == "0");

    /// <summary>Every known difference.</summary>
    public static readonly IReadOnlyList<KnownDifference> All =
    [
        ConditionalUnconverted,
        RoundWholeNumberFloat,
        CurrencyFullPrecision,
        IsNullUnconverted,
        RoundOverflowValue,
        VarcharLiteralCodePage,
        FloatDecimalComparison,
        DotNetCasing,
        PatIndexEndAnchor,
        PatIndexLineBreak,
        PatIndexCharacterClass,
    ];
}
