namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>An operand the generator can place at the bottom of an expression: a column or a literal.</summary>
/// <param name="Name">What it is, used to group differences (<c>column money</c>, <c>literal int above int range</c>).</param>
/// <param name="Kind">What it produces.</param>
/// <param name="Sql">Its SQL text.</param>
public sealed record Leaf(string Name, Kind Kind, string Sql);

/// <summary>An operator or function the generator can apply.</summary>
/// <param name="Name">The construct, used to group differences and by the known differences (<c>/</c>, <c>ROUND(x, -3)</c>).</param>
/// <param name="Result">What it produces.</param>
/// <param name="Operands">What each operand slot takes.</param>
/// <param name="Render">The SQL text, given the operands' SQL text. Operators are parenthesised, so precedence never matters.</param>
/// <param name="Weight">How often the generator picks it relative to the other rules of the same result kind.</param>
public sealed record Rule(string Name, Kind Result, Kind[] Operands, Func<string[], string> Render, int Weight = 2);

/// <summary>
/// The rule table: the operand types, operators, functions and boundary values the generator draws from. Adding a
/// construct here puts it under the differential comparison at every depth, combined with every other construct.
/// </summary>
/// <remarks>
/// The table covers the value grammar the engine evaluates today for numbers and text: the five arithmetic operators
/// and unary minus, concatenation, conversions, the conditional forms, and the numeric and text functions of the DML
/// value grammar, plus PATINDEX and CHARINDEX (#1851). Date and time values are not in it yet (#1806).
/// </remarks>
public static class RuleTable
{
    private static readonly Kind[] N = [Kind.Number];
    private static readonly Kind[] NN = [Kind.Number, Kind.Number];
    private static readonly Kind[] T = [Kind.Text];
    private static readonly Kind[] TT = [Kind.Text, Kind.Text];

    /// <summary>Columns of the operand table, and literals at and around the type boundaries.</summary>
    public static readonly IReadOnlyList<Leaf> Leaves =
    [
        .. Operands.Columns.Select(c => new Leaf("column " + c.SqlType, c.Kind, c.Name)),

        new("literal int", Kind.Number, "0"),
        new("literal int", Kind.Number, "1"),
        new("literal int", Kind.Number, "2"),
        new("literal int", Kind.Number, "7"),
        new("literal int", Kind.Number, "2147483647"),
        new("literal int above int range", Kind.Number, "2147483648"),
        new("literal int above int range", Kind.Number, "3000000000"),
        new("literal decimal", Kind.Number, "2.5"),
        new("literal decimal", Kind.Number, "0.1"),
        new("literal decimal", Kind.Number, "1.005"),
        new("literal decimal", Kind.Number, "0.0001"),
        new("literal decimal", Kind.Number, "748.58"),
        new("literal decimal", Kind.Number, "9.99"),
        new("literal float", Kind.Number, "1.5E0"),
        new("literal float", Kind.Number, "1E-1"),
        new("NULL", Kind.Number, "NULL"),

        new("literal nvarchar", Kind.Text, "N'Abc'"),
        new("literal nvarchar", Kind.Text, "N''"),
        new("literal nvarchar", Kind.Text, "N' x '"),
        new("literal nvarchar", Kind.Text, "N'5'"),
        new("literal varchar", Kind.Text, "'abc '"),
        new("literal varchar", Kind.Text, "''"),
        new("literal varchar", Kind.Text, "'2.5'"),
        new("literal varchar outside the code page", Kind.Text, "'ā'"),
        new("NULL", Kind.Text, "NULL"),
    ];

    /// <summary>The operators and functions.</summary>
    public static readonly IReadOnlyList<Rule> Rules =
    [
        // Arithmetic (Precision, scale, and length; Data type precedence).
        new("+", Kind.Number, NN, a => $"({a[0]} + {a[1]})", 4),
        new("-", Kind.Number, NN, a => $"({a[0]} - {a[1]})", 4),
        new("*", Kind.Number, NN, a => $"({a[0]} * {a[1]})", 4),
        new("/", Kind.Number, NN, a => $"({a[0]} / {a[1]})", 5),
        new("%", Kind.Number, NN, a => $"({a[0]} % {a[1]})", 2),
        new("unary -", Kind.Number, N, a => $"(-{a[0]})", 1),
        new("number + text", Kind.Number, [Kind.Number, Kind.Text], a => $"({a[0]} + {a[1]})", 1),

        // Numeric functions.
        new("ABS", Kind.Number, N, a => $"ABS({a[0]})"),
        new("CEILING", Kind.Number, N, a => $"CEILING({a[0]})", 1),
        new("FLOOR", Kind.Number, N, a => $"FLOOR({a[0]})", 1),
        new("ROUND(x, 0)", Kind.Number, N, a => $"ROUND({a[0]}, 0)"),
        new("ROUND(x, 1)", Kind.Number, N, a => $"ROUND({a[0]}, 1)"),
        new("ROUND(x, 2)", Kind.Number, N, a => $"ROUND({a[0]}, 2)"),
        new("ROUND(x, -1)", Kind.Number, N, a => $"ROUND({a[0]}, -1)"),
        new("ROUND(x, -3)", Kind.Number, N, a => $"ROUND({a[0]}, -3)"),
        new("LEN", Kind.Number, T, a => $"LEN({a[0]})"),
        new("CHARINDEX", Kind.Number, T, a => $"CHARINDEX(N'b', {a[0]})", 1),
        new("PATINDEX('%a', x)", Kind.Number, T, a => $"PATINDEX('%a', {a[0]})", 1),
        new("PATINDEX('a%b', x)", Kind.Number, T, a => $"PATINDEX('a%b', {a[0]})", 1),
        new("PATINDEX('%b%', x)", Kind.Number, T, a => $"PATINDEX('%b%', {a[0]})", 1),
        new("PATINDEX('_b%', x)", Kind.Number, T, a => $"PATINDEX('_b%', {a[0]})", 1),
        new("PATINDEX('%[0-9]%', x)", Kind.Number, T, a => $"PATINDEX('%[0-9]%', {a[0]})", 1),
        new("PATINDEX('%[^a-z]%', x)", Kind.Number, T, a => $"PATINDEX('%[^a-z]%', {a[0]})", 1),

        // Conversions (CAST and CONVERT).
        new("CAST AS int", Kind.Number, N, a => $"CAST({a[0]} AS int)"),
        new("CAST AS bigint", Kind.Number, N, a => $"CAST({a[0]} AS bigint)", 1),
        new("CAST AS decimal(10,2)", Kind.Number, N, a => $"CAST({a[0]} AS decimal(10,2))"),
        new("CAST AS decimal(38,6)", Kind.Number, N, a => $"CAST({a[0]} AS decimal(38,6))", 1),
        new("CAST AS money", Kind.Number, N, a => $"CAST({a[0]} AS money)"),
        new("CAST AS float", Kind.Number, N, a => $"CAST({a[0]} AS float)", 1),
        new("CAST text AS int", Kind.Number, T, a => $"CAST({a[0]} AS int)", 1),
        new("CAST text AS decimal(10,2)", Kind.Number, T, a => $"CAST({a[0]} AS decimal(10,2))", 1),
        new("CAST AS nvarchar(30)", Kind.Text, N, a => $"CAST({a[0]} AS nvarchar(30))", 1),
        new("CAST AS varchar(3)", Kind.Text, T, a => $"CAST({a[0]} AS varchar(3))", 1),

        // Conditional values (CASE, IIF, COALESCE, NULLIF, ISNULL).
        new("COALESCE", Kind.Number, NN, a => $"COALESCE({a[0]}, {a[1]})", 3),
        new("ISNULL", Kind.Number, NN, a => $"ISNULL({a[0]}, {a[1]})", 3),
        new("NULLIF", Kind.Number, NN, a => $"NULLIF({a[0]}, {a[1]})", 1),
        new("IIF", Kind.Number, [Kind.Predicate, Kind.Number, Kind.Number], a => $"IIF({a[0]}, {a[1]}, {a[2]})", 3),
        new("CASE", Kind.Number, [Kind.Predicate, Kind.Number, Kind.Number], a => $"CASE WHEN {a[0]} THEN {a[1]} ELSE {a[2]} END", 3),
        new("COALESCE", Kind.Text, TT, a => $"COALESCE({a[0]}, {a[1]})", 2),
        new("ISNULL", Kind.Text, TT, a => $"ISNULL({a[0]}, {a[1]})", 2),
        new("IIF", Kind.Text, [Kind.Predicate, Kind.Text, Kind.Text], a => $"IIF({a[0]}, {a[1]}, {a[2]})", 1),

        // Text functions and concatenation.
        new("text + text", Kind.Text, TT, a => $"({a[0]} + {a[1]})", 3),
        new("CONCAT", Kind.Text, TT, a => $"CONCAT({a[0]}, {a[1]})"),
        new("UPPER", Kind.Text, T, a => $"UPPER({a[0]})"),
        new("LOWER", Kind.Text, T, a => $"LOWER({a[0]})"),
        new("LTRIM", Kind.Text, T, a => $"LTRIM({a[0]})"),
        new("RTRIM", Kind.Text, T, a => $"RTRIM({a[0]})"),
        new("TRIM", Kind.Text, T, a => $"TRIM({a[0]})", 1),
        new("SUBSTRING(x, 2, 2)", Kind.Text, T, a => $"SUBSTRING({a[0]}, 2, 2)"),
        new("LEFT(x, 2)", Kind.Text, T, a => $"LEFT({a[0]}, 2)", 1),
        new("RIGHT(x, 2)", Kind.Text, T, a => $"RIGHT({a[0]}, 2)", 1),
        new("REPLACE(x, 'a', 'xy')", Kind.Text, T, a => $"REPLACE({a[0]}, N'a', N'xy')", 1),

        // Comparisons, only inside IIF and CASE.
        new("number = number", Kind.Predicate, NN, a => $"{a[0]} = {a[1]}", 3),
        new("number < number", Kind.Predicate, NN, a => $"{a[0]} < {a[1]}", 2),
        new("number > number", Kind.Predicate, NN, a => $"{a[0]} > {a[1]}", 2),
        new("number <> number", Kind.Predicate, NN, a => $"{a[0]} <> {a[1]}", 1),
        new("text = text", Kind.Predicate, TT, a => $"{a[0]} = {a[1]}", 2),
        new("text < text", Kind.Predicate, TT, a => $"{a[0]} < {a[1]}", 1),
        new("number IS NULL", Kind.Predicate, N, a => $"{a[0]} IS NULL", 1),
        new("text IS NULL", Kind.Predicate, T, a => $"{a[0]} IS NULL", 1),
    ];
}
