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
/// is the scale of a decimal division, which the snapshot records like every other difference.</item>
/// <item>"A decimal written to a currency column skips the conversion to 4 places" and "values that exceed the target
/// column's length, range or precision are not checked before the first write": these are about the write to a
/// Dataverse column, not the value of an expression, and are outside this harness.</item>
/// <item>"The same expressions in a SELECT return the same wrong values": every case here is evaluated on the path a
/// SELECT uses, so each one is also that bullet.</item>
/// </list>
/// <para>
/// #1851 also remarks that PATINDEX is always case-insensitive, whatever the collation. Under the reference collation
/// (case-insensitive) SQL Server agrees, so there is no case for it here; reference observation R26 records SQL Server
/// under a case-sensitive collation. That part belongs to the collation rules of #1764 and to COLLATE being ignored
/// (#1631).
/// </para>
/// </remarks>
public static class KnownDifferences
{
    /// <summary>#1764, first known case: conditional-whole-number-unconverted.</summary>
    public static readonly KnownDifference ConditionalUnconverted = new(
        "conditional-whole-number-unconverted",
        "#1764, first known case",
        "COALESCE, IIF and CASE mixing a whole number with decimal, currency or float return the whole number unconverted, so a following / divides as integers.",
        [
            new("COALESCE(5, 2.50) / 2", 1, "2.500000 : decimal(7,6)", "2 : ~bigint"),
            new("IIF(1 = 1, 5, 2.50) / 2", 1, "2.500000 : decimal(7,6)", "2 : ~bigint"),
            new("CASE WHEN 1 = 1 THEN 7 ELSE 2.5E0 END / 2", 1, "3.5 : float", "3 : ~bigint"),
            new("COALESCE(c_int, c_money) / 2", 1, "3.5000 : money", "3 : ~bigint"),
        ]);

    /// <summary>#1764, second known case: round-whole-number-float.</summary>
    public static readonly KnownDifference RoundWholeNumberFloat = new(
        "round-whole-number-float",
        "#1764, second known case",
        "ROUND of a whole number returns a floating-point value, so a following / or * computes in float.",
        [
            new("ROUND(7, 0) / 2", 1, "3 : int", "3.5 : ~float"),
            new("ROUND(c_int, 0)", 1, "7 : int", "7 : ~float"),
        ]);

    /// <summary>#1764, third known case: currency-arithmetic-full-precision.</summary>
    public static readonly KnownDifference CurrencyFullPrecision = new(
        "currency-arithmetic-full-precision",
        "#1764, third known case",
        "Arithmetic on currency keeps full decimal precision; SQL Server keeps 4 places after each operation (a single division truncates).",
        [
            new("c_money / 7", 1, "1.7636 : money", "1.7636571428571428571428571429 : ~decimal"),
            new("(c_money / 7) * 7", 1, "12.3452 : money", "12.345600000000000000000000000 : ~decimal"),
            new("c_money * c_money", 1, "152.4138 : money", "152.41383936 : ~decimal"),
            new("CAST(2 AS money) / 3", 1, "0.6666 : money", "0.6666666666666666666666666667 : ~decimal"),
        ]);

    /// <summary>#1764, fourth known case: isnull-replacement-unconverted.</summary>
    public static readonly KnownDifference IsNullUnconverted = new(
        "isnull-replacement-unconverted",
        "#1764, fourth known case",
        "ISNULL returns its second value unconverted; SQL Server converts it to the first value's type, precision, scale and length.",
        [
            new("ISNULL(CAST(NULL AS varchar(3)), 'abcdef')", 1, "'abc' : varchar(3)", "'abcdef' : ~text"),
            new("ISNULL(c_int, 2.7)", 6, "2 : int", "2.7 : ~decimal"),
            new("ISNULL(c_dec2, 1.005)", 6, "1.01 : decimal(12,2)", "1.005 : ~decimal"),
        ]);

    /// <summary>#1764, fifth known case: round-decimal-overflow-value.</summary>
    public static readonly KnownDifference RoundOverflowValue = new(
        "round-decimal-overflow-value",
        "#1764, fifth known case",
        "ROUND that overflows its decimal type returns a value where SQL Server raises an overflow error.",
        [
            new("ROUND(9.99, 0)", 1, "error Overflow (8115)", "10 : ~decimal"),
        ]);

    /// <summary>#1764, seventh known case: varchar-literal-code-page.</summary>
    public static readonly KnownDifference VarcharLiteralCodePage = new(
        "varchar-literal-code-page",
        "#1764, seventh known case",
        "A text literal without the N prefix keeps characters its code page cannot hold; SQL Server converts them.",
        [
            new("'\u0101'", 1, "'a' : varchar(1)", "'{U+0101}' : ~text"),
            new("UPPER('\u0101')", 1, "'A' : varchar(1)", "'{U+0100}' : ~text"),
        ]);

    /// <summary>#1764, ninth known case: float-decimal-literal-comparison.</summary>
    public static readonly KnownDifference FloatDecimalComparison = new(
        "float-decimal-literal-comparison",
        "#1764, ninth known case",
        "A float compared with a decimal literal inside IIF or CASE is compared at 15 significant digits; SQL Server converts the literal to float and compares exactly.",
        [
            new("IIF(CAST(0.1 AS float) + CAST(0.2 AS float) = 0.3, 1, 0)", 1, "0 : int", "1 : ~int"),
        ]);

    /// <summary>#1764, eleventh known case: upper-lower-dotnet-casing.</summary>
    public static readonly KnownDifference DotNetCasing = new(
        "upper-lower-dotnet-casing",
        "#1764, eleventh known case",
        "UPPER and LOWER use .NET's casing rules, not the collation's: a letter the collation has no case pair for is changed.",
        [
            new("UPPER(N'\uA794')", 1, "'{U+A794}' : nvarchar(1)", "'{U+A7C4}' : ~text"),
            new("LOWER(N'\uA7C4')", 1, "'{U+A7C4}' : nvarchar(1)", "'{U+A794}' : ~text"),
        ]);

    /// <summary>#1851, case 1: patindex-end-anchor-line-feed.</summary>
    public static readonly KnownDifference PatIndexEndAnchor = new(
        "patindex-end-anchor-line-feed",
        "#1851, case 1",
        "PATINDEX with a pattern that does not end in % finds a match in a value that ends in a line feed.",
        [
            new("PATINDEX('%a', 'xa' + CHAR(10))", 1, "0 : int", "2 : ~int"),
            new("PATINDEX('%a', c_text)", 7, "0 : int", "2 : ~int"),
        ]);

    /// <summary>#1851, case 2: patindex-wildcard-line-feed.</summary>
    public static readonly KnownDifference PatIndexLineBreak = new(
        "patindex-wildcard-line-feed",
        "#1851, case 2",
        "PATINDEX's % and _ do not match a line feed, so a match that has to cross a line break is not found.",
        [
            new("PATINDEX('a%b', 'a' + CHAR(10) + 'b')", 1, "1 : int", "0 : ~int"),
            new("PATINDEX('a%b', c_ntext)", 7, "1 : int", "0 : ~int"),
        ]);

    /// <summary>#1851, case 3: patindex-character-class.</summary>
    public static readonly KnownDifference PatIndexCharacterClass = new(
        "patindex-character-class",
        "#1851, case 3",
        "PATINDEX matches [0-9], [a-z] and [^x] as literal text.",
        [
            new("PATINDEX('%[0-9]%', 'abc5')", 1, "4 : int", "0 : ~int"),
        ]);

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
