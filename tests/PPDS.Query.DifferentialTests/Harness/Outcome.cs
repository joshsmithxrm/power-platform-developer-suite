using System.Data.SqlTypes;
using System.Globalization;
using System.Text;

namespace PPDS.Query.DifferentialTests.Harness;

/// <summary>The class of an error, compared between the two engines. The exact message and number are not compared.</summary>
public enum ErrorClass
{
    /// <summary>No error: the expression produced a value.</summary>
    None,
    /// <summary>Division or modulo by zero (SQL Server error 8134).</summary>
    DivideByZero,
    /// <summary>Arithmetic overflow (SQL Server errors 220, 232, 237, 3606, 8115).</summary>
    Overflow,
    /// <summary>A value could not be converted (SQL Server errors 235, 241, 245, 248, 293, 295, 8114, 8169; the engine's FormatException).</summary>
    Conversion,
    /// <summary>Text or binary data would be truncated (SQL Server errors 2628, 8152).</summary>
    Truncation,
    /// <summary>The operand types are not allowed together (SQL Server errors 206, 402, 529, 4127, 4151, 8116, 8117, 8133).</summary>
    TypeClash,
    /// <summary>The engine refused the expression before evaluating it (PPDS parse or compile refusal).</summary>
    Refused,
    /// <summary>Any other error; the detail names it. Two "Other" errors never compare equal.</summary>
    Other,
}

/// <summary>Which parts of two outcomes differ.</summary>
[Flags]
public enum Difference
{
    /// <summary>The outcomes agree.</summary>
    None = 0,
    /// <summary>One raised an error and the other did not, or both raised errors of different classes.</summary>
    Error = 1,
    /// <summary>Both produced a value and the values differ (NULL, digits, scale, characters, trailing spaces).</summary>
    Value = 2,
    /// <summary>Both produced a value and the result types differ, as far as the engine under test reports a type.</summary>
    Type = 4,
}

/// <summary>
/// A SQL result type. <see cref="Name"/> is the base type name as SQL Server spells it (<c>numeric</c> is reported as
/// <c>decimal</c>, its synonym). Precision and scale are set for <c>decimal</c> and <c>money</c>; length, in characters
/// (-1 for <c>max</c>), for the character types.
/// </summary>
public sealed record SqlTypeInfo(string Name, int? Precision = null, int? Scale = null, int? Length = null)
{
    /// <summary>
    /// The family used when the engine under test reports no more than a family. The engine computes currency as a
    /// .NET decimal and text as a .NET string, so it cannot report money apart from decimal, varchar apart from
    /// nvarchar, or any precision or length. The whole-number types are not folded: tinyint, smallint, int and bigint
    /// are four families, because each overflows at a different point and the engine does distinguish int from bigint
    /// (SQL Server types <c>NULLIF(7, 1.005)</c> tinyint, which is reported as a difference).
    /// </summary>
    public string Family => FamilyOf(Name);

    /// <summary>The family of a base type name.</summary>
    public static string FamilyOf(string name) => name switch
    {
        "decimal" or "numeric" or "money" or "smallmoney" => "decimal",
        "float" or "real" => "float",
        "varchar" or "nvarchar" or "char" or "nchar" => "text",
        _ => name,
    };

    /// <summary>
    /// The type described by a result column's metadata: its type name, the precision and scale of a decimal, and the
    /// size, in characters, of a character type (-1 for <c>max</c>). Money and smallmoney get their documented
    /// precision and scale of 19,4 and 10,4, which the metadata does not carry.
    /// </summary>
    public static SqlTypeInfo FromMetadata(string dataTypeName, int? precision, int? scale, int? size)
    {
        var name = dataTypeName.Trim().ToLowerInvariant();
        return name switch
        {
            "decimal" or "numeric" => new SqlTypeInfo("decimal", precision, scale),
            "money" => new SqlTypeInfo(name, 19, 4),
            "smallmoney" => new SqlTypeInfo(name, 10, 4),
            "varchar" or "nvarchar" or "char" or "nchar" or "varbinary" or "binary" =>
                new SqlTypeInfo(name, Length: size is null or < 0 or >= 1073741823 ? -1 : size),
            _ => new SqlTypeInfo(name),
        };
    }

    /// <inheritdoc />
    public override string ToString() => Name switch
    {
        "decimal" => $"decimal({Precision},{Scale})",
        "money" or "smallmoney" => Name,
        _ when Length is not null => $"{Name}({(Length == -1 ? "max" : Length.Value.ToString(CultureInfo.InvariantCulture))})",
        _ => Name,
    };
}

/// <summary>
/// What one engine produced for one expression over one row: a value with its type, or an error.
/// </summary>
/// <param name="Value">The value, rendered by <see cref="Render"/>; null when <paramref name="Error"/> is not None.</param>
/// <param name="Error">The error class; None for a value.</param>
/// <param name="ErrorDetail">The SQL Server error number or the .NET exception type, for the report. Not compared.</param>
/// <param name="Type">The full SQL type, when the engine reports one (SQL Server always does).</param>
/// <param name="Family">The type family; null when the engine reports none (the engine under test reports none for NULL).</param>
public sealed record Outcome(string? Value, ErrorClass Error, string? ErrorDetail, SqlTypeInfo? Type, string? Family)
{
    /// <summary>A value outcome.</summary>
    public static Outcome Of(string value, SqlTypeInfo? type, string? family) => new(value, ErrorClass.None, null, type, family);

    /// <summary>An error outcome.</summary>
    public static Outcome Failed(ErrorClass error, string detail) => new(null, error, detail, null, null);

    /// <summary>True when the outcome is an error.</summary>
    public bool IsError => Error != ErrorClass.None;

    /// <summary>
    /// The outcome as one line: <c>2.500000 : decimal(7,6)</c>, <c>NULL : int</c>, <c>2 : ~bigint</c> (a tilde marks a
    /// type family only) or <c>error DivideByZero (8134)</c>. Known differences pin both engines' outcomes in this form.
    /// </summary>
    public override string ToString()
    {
        if (IsError)
            return $"error {Error} ({ErrorDetail})";
        var type = Type is not null ? Type.ToString() : Family is not null ? "~" + Family : "~untyped";
        return $"{Value} : {type}";
    }

    /// <summary>
    /// Compares <paramref name="reference"/> (SQL Server) with <paramref name="actual"/> (the engine under test).
    /// The type is compared at the level the engine under test reports: the full type when it reports one, else the
    /// family, and not at all when it reports none (a NULL from today's engine).
    /// </summary>
    public static Difference Compare(Outcome reference, Outcome actual)
    {
        if (reference.Error != actual.Error)
            return Difference.Error;
        // Two errors of the same named class agree. Two "Other" errors say nothing about each other (an unmapped SQL
        // Server error number and a .NET exception), so they never agree.
        if (reference.Error == ErrorClass.Other)
            return Difference.Error;
        if (reference.IsError)
            return Difference.None;

        var difference = Difference.None;
        if (!string.Equals(reference.Value, actual.Value, StringComparison.Ordinal))
            difference |= Difference.Value;

        if (actual.Type is not null && reference.Type is not null)
        {
            if (actual.Type != reference.Type)
                difference |= Difference.Type;
        }
        else if (actual.Family is not null && reference.Type is not null && actual.Family != reference.Type.Family)
        {
            difference |= Difference.Type;
        }

        return difference;
    }

    /// <summary>
    /// True when both values are numbers with the same numeric value, so the difference is in the scale only
    /// (<c>1.25</c> against <c>1.250000</c>).
    /// </summary>
    public static bool NumericallyEqual(string? a, string? b)
    {
        if (a is null || b is null)
            return false;
        return decimal.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
               && decimal.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y)
               && x == y;
    }

    /// <summary>
    /// Renders a value so that two values render alike exactly when they are the same value: NULL as <c>NULL</c>;
    /// decimals with their scale (<c>2.50</c> is not <c>2.5</c>); money with its four places; floats in round-trip form;
    /// text quoted, with every character outside printable ASCII (and the brace) written as <c>{U+XXXX}</c>, so
    /// trailing spaces, line feeds and look-alike letters are visible. A value of any other type is a harness failure.
    /// </summary>
    public static string Render(object? value) => value switch
    {
        null or DBNull => "NULL",
        INullable { IsNull: true } => "NULL",
        SqlDecimal d => d.ToString(),
        SqlMoney m => m.Value.ToString("F4", CultureInfo.InvariantCulture),
        SqlString s => Quote(s.Value),
        SqlBoolean b => b.Value ? "1" : "0",
        SqlDouble d => Render(d.Value),
        SqlSingle f => Render((double)f.Value),
        SqlInt64 l => Render(l.Value),
        SqlInt32 i => Render(i.Value),
        SqlInt16 s => Render((int)s.Value),
        SqlByte b => Render((int)b.Value),
        SqlDateTime dt => Render(dt.Value),
        SqlGuid g => Render(g.Value),
        string s => Quote(s),
        bool b => b ? "1" : "0",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => ((double)f).ToString("R", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        short s => s.ToString(CultureInfo.InvariantCulture),
        byte b => b.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        // Anything else (binary, datetimeoffset, time, xml, a type not seen before) is not rendered exactly here, so
        // comparing its text could call two different values equal: stop the run instead.
        _ => throw new HarnessException($"The harness does not render values of type {value.GetType().Name} exactly; add an exact rendering before comparing them."),
    };

    private static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var c in text)
        {
            if (c is >= ' ' and <= '~' and not '{' and not '}')
                builder.Append(c);
            else
                builder.Append("{U+").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('}');
        }
        return builder.Append('\'').ToString();
    }
}
