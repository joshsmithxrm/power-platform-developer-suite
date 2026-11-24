namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>
/// The differences between today's engine and SQL Server that #1764 and #1851 already record: the negative control.
/// Every case here must still differ from SQL Server, exactly as pinned. A fix makes its cases fail with "stopped
/// differing", and the fix's pull request removes them (and the entry when no case is left). Nothing else may be added
/// here without an issue that records it.
/// </summary>
/// <remarks>
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
    private static bool IsWholeNumber(SqlTypeInfo? type) => type?.Name is "int" or "bigint";

    private static int Scale(string? value)
    {
        var dot = value?.IndexOf('.') ?? -1;
        return dot < 0 ? 0 : value!.Length - dot - 1;
    }

    private static bool IsPatIndex(NodeDifference d) => d.Node.Construct.StartsWith("PATINDEX(", StringComparison.Ordinal);

    private static string Pattern(NodeDifference d) => d.Node.Construct["PATINDEX('".Length..d.Node.Construct.IndexOf("',", StringComparison.Ordinal)];

    private static bool TextHasLineFeed(NodeDifference d) => d.Children[0].Reference.Value?.Contains("{U+000A}", StringComparison.Ordinal) == true;

    /// <summary>The value operands of a conditional: both operands of COALESCE, the THEN and ELSE values of IIF and CASE.</summary>
    private static IEnumerable<NodeResult> Branches(NodeDifference d) => d.Node.Construct == "COALESCE" ? d.Children : d.Children.Skip(1);

    /// <summary>#1764: a conditional returns its whole-number branch unconverted.</summary>
    public static readonly KnownDifference ConditionalUnconverted = new(
        "conditional-whole-number-unconverted",
        "#1764, first known case",
        "COALESCE, IIF and CASE mixing a whole number with decimal, currency or float return the whole number unconverted, so a following / divides as integers.",
        [
            new("COALESCE(5, 2.50) / 2", 1, "2.500000 : decimal(7,6)", "2 : ~bigint"),
            new("IIF(1 = 1, 5, 2.50) / 2", 1, "2.500000 : decimal(7,6)", "2 : ~bigint"),
            new("CASE WHEN 1 = 1 THEN 7 ELSE 2.5E0 END / 2", 1, "3.5 : float", "3 : ~bigint"),
            new("COALESCE(c_int, c_money) / 2", 1, "3.5000 : money", "3 : ~bigint"),
        ],
        d => d.Node.Construct is "COALESCE" or "IIF" or "CASE"
             && d.Node.Kind == Kind.Number
             && !d.Difference.HasFlag(Difference.Error)
             && d.Reference.Type?.Family is "decimal" or "float"
             && Branches(d).Any(b => IsWholeNumber(b.Reference.Type)
                                     && b.Actual.Value == d.Actual.Value
                                     && b.Actual.Family == d.Actual.Family));

    /// <summary>#1764: ROUND of a whole number returns a float.</summary>
    public static readonly KnownDifference RoundWholeNumberFloat = new(
        "round-whole-number-float",
        "#1764, second known case",
        "ROUND of a whole number returns a floating-point value, so a following / or * computes in float.",
        [
            new("ROUND(7, 0) / 2", 1, "3 : int", "3.5 : ~float"),
            new("ROUND(c_int, 0)", 1, "7 : int", "7 : ~float"),
        ],
        d => d.Node.Construct.StartsWith("ROUND(", StringComparison.Ordinal)
             && !d.Difference.HasFlag(Difference.Error)
             && d.Difference.HasFlag(Difference.Type)
             && IsWholeNumber(d.Children[0].Reference.Type)
             && d.Actual.Family == "float");

    /// <summary>#1764: currency arithmetic keeps more than 4 places.</summary>
    public static readonly KnownDifference CurrencyFullPrecision = new(
        "currency-arithmetic-full-precision",
        "#1764, third known case",
        "Arithmetic on currency keeps full decimal precision; SQL Server keeps 4 places after each operation (a single division truncates).",
        [
            new("CAST(2 AS money) / 3", 1, "0.6666 : money", "0.6666666666666666666666666667 : ~decimal"),
            new("CAST(1 AS money) / 3 * 3", 1, "0.9999 : money", "0.9999999999999999999999999999 : ~decimal"),
            new("c_money * c_money", 1, "152.4138 : money", "152.41383936 : ~decimal"),
        ],
        d => d.Node.Construct is "+" or "-" or "*" or "/"
             && d.Difference == Difference.Value
             && d.Reference.Type?.Name == "money"
             && d.Children.Any(c => c.Reference.Type?.Name == "money")
             && d.Actual.Family == "decimal"
             && Scale(d.Actual.Value) > 4);

    /// <summary>#1764: ISNULL does not convert its replacement to the first operand's type.</summary>
    public static readonly KnownDifference IsNullUnconverted = new(
        "isnull-replacement-unconverted",
        "#1764, fourth known case",
        "ISNULL returns its second value unconverted; SQL Server converts it to the first value's type, precision, scale and length.",
        [
            new("ISNULL(CAST(NULL AS varchar(3)), 'abcdef')", 1, "'abc' : varchar(3)", "'abcdef' : ~text"),
            new("ISNULL(c_int, 2.7)", 6, "2 : int", "2.7 : ~decimal"),
            new("ISNULL(c_dec2, 1.005)", 6, "1.01 : decimal(12,2)", "1.005 : ~decimal"),
        ],
        d => d.Node.Construct == "ISNULL"
             && !d.Difference.HasFlag(Difference.Error)
             && d.Children[0].Reference.Value == "NULL"
             && d.Reference.Type == d.Children[0].Reference.Type
             && d.Reference.Type != d.Children[1].Reference.Type
             && d.Actual.Value == d.Children[1].Actual.Value);

    /// <summary>#1764: ROUND that overflows its decimal type returns a value.</summary>
    public static readonly KnownDifference RoundOverflowValue = new(
        "round-decimal-overflow-value",
        "#1764, fifth known case",
        "ROUND that overflows its decimal type returns a value where SQL Server raises an overflow error.",
        [
            new("ROUND(9.99, 0)", 1, "error Overflow (8115)", "10 : ~decimal"),
        ],
        d => d.Node.Construct.StartsWith("ROUND(", StringComparison.Ordinal)
             && d.Reference.Error == ErrorClass.Overflow
             && !d.Actual.IsError
             && d.Children[0].Reference.Type?.Name == "decimal");

    /// <summary>#1764: a text literal without N is not converted through the code page.</summary>
    public static readonly KnownDifference VarcharLiteralCodePage = new(
        "varchar-literal-code-page",
        "#1764, seventh known case",
        "A text literal without the N prefix keeps characters its code page cannot hold; SQL Server converts them.",
        [
            new("'ā'", 1, "'a' : varchar(1)", "'{U+0101}' : ~text"),
            new("UPPER('ā')", 1, "'A' : varchar(1)", "'{U+0100}' : ~text"),
        ],
        d => d.Node.IsLeaf
             && d.Node.Construct == "literal varchar outside the code page"
             && d.Difference == Difference.Value);

    /// <summary>#1764: a float compared with a decimal literal is compared at 15 significant digits.</summary>
    public static readonly KnownDifference FloatDecimalComparison = new(
        "float-decimal-literal-comparison",
        "#1764, ninth known case",
        "A float compared with a decimal literal inside IIF or CASE is compared at 15 significant digits; SQL Server converts the literal to float and compares exactly.",
        [
            new("IIF(CAST(0.1 AS float) + CAST(0.2 AS float) = 0.3, 1, 0)", 1, "0 : int", "1 : ~int"),
        ],
        d => d.Node.Kind == Kind.Predicate
             && d.Node.Construct.StartsWith("number ", StringComparison.Ordinal)
             && d.Difference == Difference.Value
             && d.Children.Any(c => c.Reference.Type?.Family == "float")
             && d.Children.Any(c => c.Node.Construct == "literal decimal"));

    /// <summary>#1764: UPPER and LOWER use .NET's casing rules.</summary>
    public static readonly KnownDifference DotNetCasing = new(
        "upper-lower-dotnet-casing",
        "#1764, eleventh known case",
        "UPPER and LOWER use .NET's casing rules, not the collation's: a letter the collation has no case pair for is changed.",
        [
            new("UPPER(N'ꞔ')", 1, "'{U+A794}' : nvarchar(1)", "'{U+A7C4}' : ~text"),
            new("LOWER(N'Ꞔ')", 1, "'{U+A7C4}' : nvarchar(1)", "'{U+A794}' : ~text"),
        ],
        d => d.Node.Construct is "UPPER" or "LOWER" && d.Difference == Difference.Value);

    /// <summary>#1851 (1): the end anchor of a PATINDEX pattern matches before a final line feed.</summary>
    public static readonly KnownDifference PatIndexEndAnchor = new(
        "patindex-end-anchor-line-feed",
        "#1851, case 1",
        "PATINDEX with a pattern that does not end in % finds a match in a value that ends in a line feed.",
        [
            new("PATINDEX('%a', 'xa' + CHAR(10))", 1, "0 : int", "2 : ~int"),
            new("PATINDEX('%a', c_text)", 7, "0 : int", "2 : ~int"),
        ],
        d => IsPatIndex(d)
             && d.Difference == Difference.Value
             && !Pattern(d).EndsWith('%')
             && d.Children[0].Reference.Value?.EndsWith("{U+000A}'", StringComparison.Ordinal) == true
             && !Pattern(d).Contains('['));

    /// <summary>#1851 (2): % and _ do not match a line feed.</summary>
    public static readonly KnownDifference PatIndexLineBreak = new(
        "patindex-wildcard-line-feed",
        "#1851, case 2",
        "PATINDEX's % and _ do not match a line feed, so a match that has to cross a line break is not found.",
        [
            new("PATINDEX('a%b', 'a' + CHAR(10) + 'b')", 1, "1 : int", "0 : ~int"),
            new("PATINDEX('a%b', c_ntext)", 7, "1 : int", "0 : ~int"),
        ],
        d => IsPatIndex(d)
             && d.Difference == Difference.Value
             && TextHasLineFeed(d)
             && d.Actual.Value == "0"
             && !Pattern(d).Contains('['));

    /// <summary>#1851 (3): character classes are matched as literal text.</summary>
    public static readonly KnownDifference PatIndexCharacterClass = new(
        "patindex-character-class",
        "#1851, case 3",
        "PATINDEX matches [0-9], [a-z] and [^x] as literal text.",
        [
            new("PATINDEX('%[0-9]%', 'abc5')", 1, "4 : int", "0 : ~int"),
        ],
        d => IsPatIndex(d)
             && d.Difference == Difference.Value
             && Pattern(d).Contains('[')
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
