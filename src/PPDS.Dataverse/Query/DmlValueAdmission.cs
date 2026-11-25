using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Xrm.Sdk;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Query.Execution;

namespace PPDS.Dataverse.Query;

/// <summary>The kind of a literal in a DML statement, as written in the SQL text.</summary>
public enum DmlLiteralKind
{
    /// <summary>A text literal without the N prefix (<c>'x'</c>).</summary>
    Text,

    /// <summary>A text literal with the N prefix (<c>N'x'</c>).</summary>
    NationalText,

    /// <summary>A whole-number literal (<c>5</c>), optionally with a minus sign written directly on it.</summary>
    WholeNumber,

    /// <summary>A decimal literal (<c>2.5</c>), optionally with a minus sign written directly on it.</summary>
    DecimalNumber,

    /// <summary>NULL.</summary>
    Null,

    /// <summary><c>CAST('&lt;guid&gt;' AS uniqueidentifier)</c>; <see cref="DmlLiteral.Text"/> is the quoted text.</summary>
    GuidCast
}

/// <summary>A literal of a DML statement: its kind, its text exactly as written (without quotes or sign), and its sign.</summary>
/// <param name="Kind">The literal's kind.</param>
/// <param name="Text">The text of the literal: the characters between the quotes, or the digits of a number.</param>
/// <param name="Negative">True when a minus sign is written directly on a number literal.</param>
public readonly record struct DmlLiteral(DmlLiteralKind Kind, string Text, bool Negative = false);

/// <summary>Where a value is used: written to a column, or compared with one in the WHERE of a write.</summary>
public enum DmlValueUse
{
    /// <summary>A value written by UPDATE ... SET, INSERT ... VALUES or the SELECT list of an INSERT ... SELECT.</summary>
    Write,

    /// <summary>An operand compared with a column in the WHERE of an UPDATE, a DELETE or an INSERT ... SELECT.</summary>
    Compare
}

/// <summary>The target kinds of the admission functions, one per kind of Dataverse column a value may be written to or compared with.</summary>
public enum DmlTargetKind
{
    /// <summary>Single-line or multiline text.</summary>
    Text,

    /// <summary>Whole number (Int32).</summary>
    WholeNumber,

    /// <summary>Decimal number.</summary>
    Decimal,

    /// <summary>Currency.</summary>
    Money,

    /// <summary>Floating-point number.</summary>
    FloatingPoint,

    /// <summary>User-local date and time.</summary>
    DateAndTime,

    /// <summary>Date only.</summary>
    DateOnly,

    /// <summary>Yes/no.</summary>
    Boolean,

    /// <summary>Choice, status or status reason.</summary>
    Choice,

    /// <summary>A row id: a lookup, the primary key or the owner.</summary>
    Guid,

    /// <summary>A column of a kind no admission function covers: every value is refused.</summary>
    Unsupported
}

/// <summary>The decision of an admission function: the value that will be sent, or the code and the reason of the refusal.</summary>
public sealed record DmlAdmission
{
    private DmlAdmission() { }

    /// <summary>True when the literal is admitted.</summary>
    public bool IsAdmitted { get; private init; }

    /// <summary>The value that will be sent to Dataverse (written), or the literal's text (compared). Null for NULL or when refused.</summary>
    public object? Value { get; private init; }

    /// <summary>The refusal's code (a <see cref="QueryErrorCode"/>); null when admitted.</summary>
    public string? Code { get; private init; }

    /// <summary>Why the literal is refused and what to write instead. It never repeats the literal.</summary>
    public string? Reason { get; private init; }

    /// <summary>An admitted literal and the value that will be sent.</summary>
    public static DmlAdmission Admit(object? value) => new() { IsAdmitted = true, Value = value };

    /// <summary>A refused literal.</summary>
    public static DmlAdmission Refuse(string code, string reason) => new() { Code = code, Reason = reason };
}

/// <summary>
/// The admission of the values of a DML statement, exact by construction (#1698, review round 2). There is one function
/// per target kind. Each admits a literal only when (a) its text matches the exact grammar of that kind, and (b) the
/// value that will be sent, computed as the executor computes it (the literal's value, then
/// <see cref="DmlValueCoercer.Coerce"/>), loses nothing: back to text it has the same digits, or it is the same instant.
/// Everything else is refused. No general-purpose parser decides admission: grammars are regular expressions anchored at
/// both ends, and numbers are compared digit by digit.
/// </summary>
/// <remarks>
/// <para>The same family is the only path for every value: a literal written by SET, VALUES or the INSERT ... SELECT
/// list, and a literal compared in the WHERE of a write (<see cref="AdmitLiteral"/>), and every value read from a column
/// and written in the statement's pass, before its first write (<see cref="CheckSentValue"/>).</para>
/// <para>Codes: a form that no grammar admits, or a value outside what the target's type holds, is
/// <see cref="QueryErrorCode.DmlUnsupportedConstruct"/> (until the column-limit check has a code of its own); a value
/// Dataverse would store as something else (empty text, text with white space at either end, more decimal places than
/// the column keeps, digits a floating-point number cannot hold) is <see cref="QueryErrorCode.DmlValueStoredDifferently"/>.</para>
/// </remarks>
public static class DmlValueAdmission
{
    /// <summary>
    /// Whether text that is only white space, or has white space at either end, is written. False until rows B25 and B26
    /// of the admission table (specs/sql-values.md, core requirement 4) come into force; then set it to true.
    /// </summary>
    public const bool WhiteSpaceEdgedTextIsWritten = false;

    /// <summary>The most decimal places a decimal column keeps when its metadata does not say (Dataverse's maximum).</summary>
    public const int DefaultDecimalPlaces = 10;

    /// <summary>
    /// The most digits before the point a decimal value may have: those of <c>decimal(38, 10)</c>, the widest decimal with
    /// Dataverse's maximum of 10 places (a column's own range is the column-limit check's, not built yet).
    /// </summary>
    public const int DecimalIntegerDigits = 28;

    /// <summary>The most decimal places a currency value keeps (SQL Server's money, and Dataverse's maximum currency precision).</summary>
    public const int MoneyPlaces = 4;

    /// <summary>The largest magnitude of a currency value (SQL Server's money).</summary>
    public static readonly decimal MoneyMax = 922_337_203_685_477.5807m;

    /// <summary>The earliest year a date or date and time may have (SQL Server's datetime, which Dataverse uses).</summary>
    public const int EarliestYear = 1753;

    /// <summary>
    /// The most digits a number literal may be written with, leading zeros and trailing zeros of the fraction included: SQL
    /// Server refuses a longer one (error 1007, maximum precision 38), whatever its value.
    /// </summary>
    public const int MaxLiteralDigits = 38;

    private static readonly Regex Digits = new(@"\A[0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex DecimalDigits = new(@"\A[0-9]+(?:\.[0-9]+)?\z", RegexOptions.CultureInvariant);
    private static readonly Regex DateOnlyForm = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex DateTimeForm = new(
        @"\A(?<local>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:(?<utc>Z)|(?<sign>[+-])(?<hh>[0-9]{2}):(?<mm>[0-9]{2}))\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex CanonicalGuidForm = new(
        @"\A(?:[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}|\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})\z",
        RegexOptions.CultureInvariant);

    /// <summary>The one spelling a compared date and time is sent in: its UTC instant, for example 2024-01-15T04:30:00Z.</summary>
    public const string UtcInstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private const string Unsupported = QueryErrorCode.DmlUnsupportedConstruct;
    private const string StoredDifferently = QueryErrorCode.DmlValueStoredDifferently;

    /// <summary>The target kind of a column, from its metadata.</summary>
    /// <param name="target">The column's metadata.</param>
    /// <returns>The kind whose admission function decides values for this column.</returns>
    public static DmlTargetKind KindOf(AttributeMetadataDto target) => target.AttributeType switch
    {
        "String" or "Memo" => DmlTargetKind.Text,
        "Integer" => DmlTargetKind.WholeNumber,
        "Decimal" => DmlTargetKind.Decimal,
        "Money" => DmlTargetKind.Money,
        "Double" => DmlTargetKind.FloatingPoint,
        "Boolean" => DmlTargetKind.Boolean,
        "Picklist" or "State" or "Status" => DmlTargetKind.Choice,
        "Lookup" or "Owner" or "Uniqueidentifier" => DmlTargetKind.Guid,
        "DateTime" when string.Equals(target.DateTimeBehavior, "DateOnly", StringComparison.OrdinalIgnoreCase) => DmlTargetKind.DateOnly,
        "DateTime" when string.Equals(target.DateTimeBehavior, "UserLocal", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(target.Format, "DateAndTime", StringComparison.OrdinalIgnoreCase) => DmlTargetKind.DateAndTime,
        _ => DmlTargetKind.Unsupported
    };

    /// <summary>
    /// Admits <paramref name="literal"/> for <paramref name="target"/>, or refuses it. For <see cref="DmlValueUse.Write"/>
    /// the admitted value is the one the executor sends; for <see cref="DmlValueUse.Compare"/> it is the text the FetchXML
    /// carries: the literal's text, except a date and time, which is its UTC instant written as <c>yyyy-MM-ddTHH:mm:ssZ</c>
    /// (<see cref="UtcInstantFormat"/>), the same instant a written date and time is converted to.
    /// </summary>
    /// <param name="literal">The literal as written.</param>
    /// <param name="target">The column it is written to or compared with.</param>
    /// <param name="use">Written or compared.</param>
    /// <returns>The decision.</returns>
    public static DmlAdmission AdmitLiteral(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (literal.Kind == DmlLiteralKind.Null)
            return AdmitNull(target, use);
        if (literal.Kind is DmlLiteralKind.Text or DmlLiteralKind.NationalText or DmlLiteralKind.GuidCast && HasLineContinuation(literal.Text))
        {
            return DmlAdmission.Refuse(Unsupported,
                "the text holds a backslash directly followed by a line break (a carriage return or a line feed). SQL Server reads " +
                "that as a line continuation and removes the backslash and the line break (before a carriage return alone, the " +
                "backslash only), so the text would not be the text as typed. Remove the backslash, or the line break after it.");
        }
        if (literal.Kind is DmlLiteralKind.WholeNumber or DmlLiteralKind.DecimalNumber && literal.Text.Count(char.IsAsciiDigit) > MaxLiteralDigits)
        {
            return DmlAdmission.Refuse(Unsupported,
                $"the number is written with more than {MaxLiteralDigits} digits (leading zeros and zeros at the end of the fraction count), " +
                "which SQL Server does not read as a number. Write it without the extra zeros.");
        }
        if (literal.Kind == DmlLiteralKind.Text && !IsSevenBitAscii(literal.Text))
        {
            return DmlAdmission.Refuse(Unsupported,
                "text without the N prefix that holds characters outside 7-bit ASCII is not admitted. Write the same text with " +
                "the N prefix (N'...'): SQL Server converts text without N to the database's code page, and PPDS cannot tell which " +
                "code page that is.");
        }

        return KindOf(target) switch
        {
            DmlTargetKind.Text => AdmitText(literal, use),
            DmlTargetKind.WholeNumber => AdmitWholeNumber(literal, target, use),
            DmlTargetKind.Decimal => AdmitDecimal(literal, target, use),
            DmlTargetKind.Money => AdmitMoney(literal, target, use),
            DmlTargetKind.FloatingPoint => AdmitFloatingPoint(literal, target, use),
            DmlTargetKind.DateAndTime => AdmitDateAndTime(literal, target, use),
            DmlTargetKind.DateOnly => AdmitDateOnly(literal, target, use),
            DmlTargetKind.Boolean => AdmitBoolean(literal, target, use),
            DmlTargetKind.Choice => AdmitChoice(literal, target, use),
            DmlTargetKind.Guid => AdmitGuid(literal, target, use),
            _ => DmlAdmission.Refuse(Unsupported, "a column of this kind does not take a value from SQL DML.")
        };
    }

    /// <summary>
    /// Admits <paramref name="literal"/> as one value of an IN list in the WHERE of a write, or refuses it: as
    /// <see cref="AdmitLiteral"/> with <see cref="DmlValueUse.Compare"/>, and text that holds a line break (a carriage return
    /// or a line feed), or is empty or only white space, is refused, with or without the N prefix. FetchXML carries an IN
    /// value as the text of a <c>value</c> element. The executor's serialisation (<see cref="FetchXmlSerialization"/>)
    /// writes a carriage return there as a character reference and a line feed as itself, the same on every machine, but
    /// whether Dataverse reads a line break in element text unchanged has not been shown (an XML reader that normalises line
    /// breaks reads a carriage return written as itself as a line feed). The executor's parse empties an element that is only
    /// XML white space; what Dataverse selects for an empty or blank IN value has not been shown, so text that is only
    /// Unicode white space is refused with it.
    /// </summary>
    /// <param name="literal">The literal as written.</param>
    /// <param name="target">The column it is compared with.</param>
    /// <returns>The decision.</returns>
    public static DmlAdmission AdmitInListOperand(DmlLiteral literal, AttributeMetadataDto target)
    {
        var admission = AdmitLiteral(literal, target, DmlValueUse.Compare);
        if (!admission.IsAdmitted || literal.Kind is not (DmlLiteralKind.Text or DmlLiteralKind.NationalText) || admission.Value is not string text)
            return admission;
        if (text.Contains('\r') || text.Contains('\n'))
        {
            return DmlAdmission.Refuse(Unsupported,
                "an IN value that holds a line break (a carriage return or a line feed) is not admitted: the FetchXML carries it " +
                "as element text, and whether Dataverse reads a line break in element text unchanged has not been shown. Compare " +
                "with = for each such value, combined with OR.");
        }
        if (text.Length == 0 || text.All(IsUnicodeWhiteSpace))
        {
            return DmlAdmission.Refuse(Unsupported,
                "an IN value that is empty or only white space is not admitted: the FetchXML carries it as element text, which is " +
                "emptied before it reaches Dataverse when it is only spaces, tabs or line breaks. Compare with = instead.");
        }
        return admission;
    }

    private static DmlAdmission AdmitNull(AttributeMetadataDto target, DmlValueUse use)
    {
        if (use == DmlValueUse.Compare)
            return DmlAdmission.Refuse(Unsupported, "comparing with NULL matches no rows in SQL. Use IS NULL or IS NOT NULL.");
        if (target.AttributeType is "State" or "Status")
            return DmlAdmission.Refuse(Unsupported, "this column cannot be set to NULL: every row has a status.");
        return KindOf(target) == DmlTargetKind.Unsupported
            ? DmlAdmission.Refuse(Unsupported, "a column of this kind does not take a value from SQL DML.")
            : DmlAdmission.Admit(null);
    }

    // ── text ─────────────────────────────────────────────────────────────

    /// <summary>Text: a text literal (with N, or 7-bit ASCII without); written, it is not empty and has no white space at either end.</summary>
    private static DmlAdmission AdmitText(DmlLiteral literal, DmlValueUse use)
    {
        if (literal.Kind is not (DmlLiteralKind.Text or DmlLiteralKind.NationalText))
            return DmlAdmission.Refuse(Unsupported, use == DmlValueUse.Write
                ? "this is a text column and the value is not text. Write text in quotes (for example '5')."
                : "this is a text column; compare it with a quoted string.");
        if (use == DmlValueUse.Compare)
        {
            return HasCharacterOutsideXml(literal.Text)
                ? DmlAdmission.Refuse(Unsupported,
                    "the text holds a character that XML 1.0 does not allow (a control character other than tab, line feed and carriage " +
                    "return, U+FFFE, U+FFFF, or half of a surrogate pair), which a FetchXML condition cannot carry. Compare with text without it.")
                : DmlAdmission.Admit(literal.Text);
        }
        if (HasUnpairedSurrogate(literal.Text))
        {
            return DmlAdmission.Refuse(Unsupported,
                "the text holds half of a surrogate pair (an unpaired surrogate), which is not a character and would not be stored as " +
                "written. Remove it, or write the whole character.");
        }
        return TextRule(literal.Text) ?? Sent(literal, Coerce(literal.Text, "String"), sent => sent is string s && s == literal.Text);
    }

    /// <summary>The text rule for a value to be written, whatever its source: null when it is written.</summary>
    private static DmlAdmission? TextRule(string text)
    {
        if (text.Length == 0)
            return DmlAdmission.Refuse(StoredDifferently, "Dataverse stores empty text as NULL. Write NULL instead.");
        if (!WhiteSpaceEdgedTextIsWritten && IsWhiteSpaceEdged(text))
        {
            return DmlAdmission.Refuse(StoredDifferently,
                "text that is only white space, or that begins or ends with a white-space character or an invisible one (U+180E, " +
                "U+200B, U+FEFF), is not written yet: Dataverse has not been shown to store such text unchanged. Remove the white space " +
                "at either end (or the invisible character), or write NULL.");
        }
        return null;
    }

    // ── numbers ──────────────────────────────────────────────────────────

    /// <summary>Whole number: digits only (an optional minus sign written on them), within Int32.</summary>
    private static DmlAdmission AdmitWholeNumber(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (literal.Kind == DmlLiteralKind.DecimalNumber)
        {
            return DmlAdmission.Refuse(Unsupported, use == DmlValueUse.Write
                ? "this column holds whole numbers and the value is a decimal number, whose fraction would be lost. Write a whole number (for example 5)."
                : "this column holds whole numbers; compare it with a whole number (for example 5), not a decimal number.");
        }
        if (literal.Kind != DmlLiteralKind.WholeNumber || !Digits.IsMatch(literal.Text))
            return DmlAdmission.Refuse(Unsupported, use == DmlValueUse.Write
                ? "this column holds whole numbers. Write a whole number without quotes (for example 5)."
                : "this column holds whole numbers; compare it with a whole number (for example 5).");
        if (!FitsInt32(literal))
        {
            return DmlAdmission.Refuse(Unsupported,
                "this column holds whole numbers, and the number is outside the range of a whole-number column (-2,147,483,648 to 2,147,483,647).");
        }
        return use == DmlValueUse.Compare
            ? DmlAdmission.Admit(literal.Text)
            : Sent(literal, Coerce(CompiledNumber(literal), target), sent => sent is int i && SameDigits(i.ToString(CultureInfo.InvariantCulture), literal));
    }

    /// <summary>Decimal: digits with an optional fraction, held exactly, with no more places than the column keeps.</summary>
    private static DmlAdmission AdmitDecimal(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (NumberForm(literal) is { } refusal)
            return refusal;
        var places = target.Precision ?? DefaultDecimalPlaces;
        if (IntegerDigits(literal) > DecimalIntegerDigits)
            return DmlAdmission.Refuse(Unsupported, $"the number has more than {DecimalIntegerDigits} digits before the point, more than a decimal value holds.");
        if (Places(literal) > places)
        {
            return use == DmlValueUse.Write
                ? DmlAdmission.Refuse(StoredDifferently,
                    $"the number has more decimal places than the column keeps ({places}), so Dataverse would store it rounded. Write at most {places} decimal places.")
                : DmlAdmission.Refuse(Unsupported, ComparedPlaces(places));
        }
        return use == DmlValueUse.Compare
            ? DmlAdmission.Admit(literal.Text)
            : Sent(literal, Coerce(CompiledNumber(literal), target), sent => sent is decimal d && SameDigits(d.ToString(CultureInfo.InvariantCulture), literal));
    }

    /// <summary>
    /// Currency: as decimal, with at most four places (and no more than the column's precision), within money's range. When
    /// the column's precision does not come from the column itself (<see cref="AttributeMetadataDto.PrecisionSource"/> is
    /// not 0), only a whole amount is admitted (#1931, "Prove or refuse").
    /// </summary>
    private static DmlAdmission AdmitMoney(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (NumberForm(literal) is { } refusal)
            return refusal;
        if (MoneyPlacesKept(target) is not { } places)
        {
            if (Places(literal) > 0)
                return DmlAdmission.Refuse(Unsupported, PrecisionNotTheColumns(target));
            places = 0;
        }
        if (Places(literal) > places)
        {
            return use == DmlValueUse.Write
                ? DmlAdmission.Refuse(StoredDifferently,
                    $"the amount has more decimal places than the column keeps ({places}), so it would be stored rounded. Write at most {places} decimal places.")
                : DmlAdmission.Refuse(Unsupported, ComparedPlaces(places));
        }
        if (CompiledNumber(literal) is not { } compiled || Math.Abs(Convert.ToDecimal(compiled, CultureInfo.InvariantCulture)) > MoneyMax)
            return DmlAdmission.Refuse(Unsupported, "the amount is outside the range of a currency value (-922,337,203,685,477.5807 to 922,337,203,685,477.5807).");
        return use == DmlValueUse.Compare
            ? DmlAdmission.Admit(literal.Text)
            : Sent(literal, Coerce(compiled, target), sent => sent is Money m && SameDigits(m.Value.ToString(CultureInfo.InvariantCulture), literal));
    }

    /// <summary>Floating point: as decimal, and the floating-point number sent has exactly the literal's digits.</summary>
    private static DmlAdmission AdmitFloatingPoint(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (NumberForm(literal) is { } refusal)
            return refusal;
        var sent = Coerce(CompiledNumber(literal), target);
        if (sent is not double d || !SameDigits(d.ToString("R", CultureInfo.InvariantCulture), literal))
        {
            return use == DmlValueUse.Write
                ? DmlAdmission.Refuse(StoredDifferently,
                    "a floating-point number cannot hold exactly these digits, so a different number would be stored. Write fewer significant digits (at most 15 are always kept).")
                : DmlAdmission.Refuse(Unsupported,
                    "a floating-point number cannot hold exactly these digits, so the column would be compared with a different number. " +
                    "Compare with fewer significant digits (at most 15 are always kept).");
        }
        return use == DmlValueUse.Compare ? DmlAdmission.Admit(literal.Text) : Sent(literal, sent, value => value is double);
    }

    /// <summary>
    /// The refusal of a compared number with more decimal places than the column keeps: SQL Server compares the column with
    /// the number as written, so no stored row matches; whether Dataverse compares with it as written or rounded to the
    /// column's places has not been shown, so the two could select different rows.
    /// </summary>
    private static string ComparedPlaces(int places)
        => $"the number has more decimal places than the column keeps ({places}). SQL Server compares the column with the number as " +
           "written, so no stored value matches it; whether Dataverse compares it as written or rounded has not been shown. " +
           $"Compare with at most {places} decimal places.";

    /// <summary>The number grammar shared by decimal, currency and floating point, and the 28 digits PPDS holds exactly.</summary>
    private static DmlAdmission? NumberForm(DmlLiteral literal)
    {
        if (literal.Kind is not (DmlLiteralKind.WholeNumber or DmlLiteralKind.DecimalNumber) || !DecimalDigits.IsMatch(literal.Text))
            return DmlAdmission.Refuse(Unsupported, "this is a number column and the value is not a number. Write a number without quotes (for example 5 or 2.5); compare it with a number.");
        if (CompiledNumber(literal) is not { } compiled || !SameDigits(Convert.ToString(compiled, CultureInfo.InvariantCulture)!, literal))
        {
            return DmlAdmission.Refuse(Unsupported,
                "the number has more significant digits than PPDS holds exactly (28). Write at most 28 significant digits.");
        }
        return null;
    }

    // ── dates ────────────────────────────────────────────────────────────

    /// <summary>
    /// User-local date and time: exactly <c>yyyy-MM-ddTHH:mm:ss</c> followed by <c>Z</c> or <c>±hh:mm</c> (at most 14:00),
    /// a real date and time from 1753 to 9999, also in UTC. No fraction of a second: the digits Dataverse keeps are not
    /// established, so none is admitted.
    /// </summary>
    private static DmlAdmission AdmitDateAndTime(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        const string form = "Write it exactly as yyyy-MM-ddTHH:mm:ss followed by Z (UTC) or an offset such as +01:00, for example 2024-01-15T09:30:00Z.";
        if (literal.Kind is not (DmlLiteralKind.Text or DmlLiteralKind.NationalText))
            return DmlAdmission.Refuse(Unsupported, "this is a date and time column and the value is not a quoted date and time. " + form);
        var match = DateTimeForm.Match(literal.Text);
        if (!match.Success)
        {
            return DmlAdmission.Refuse(Unsupported,
                (HasNoZone(literal.Text) ? "the date and time has no time zone, so Dataverse would read it in your time zone. Add Z (UTC) or an offset such as +01:00. " : "the date and time is not in the one form admitted. ")
                + form);
        }
        if (!DateTime.TryParseExact(match.Groups["local"].Value, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return DmlAdmission.Refuse(Unsupported, "the date and time does not exist (a month, day, hour, minute or second out of range). " + form);
        var offset = TimeSpan.Zero;
        if (!match.Groups["utc"].Success)
        {
            var hours = int.Parse(match.Groups["hh"].Value, CultureInfo.InvariantCulture);
            var minutes = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
            offset = new TimeSpan(hours, minutes, 0);
            if (minutes > 59 || offset > TimeSpan.FromHours(14))
                return DmlAdmission.Refuse(Unsupported, "the offset is not between -14:00 and +14:00. " + form);
            if (match.Groups["sign"].Value == "-")
                offset = -offset;
        }
        DateTime utc;
        try
        {
            utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DmlAdmission.Refuse(Unsupported, $"the date and time is outside the range Dataverse stores ({EarliestYear} to 9999, in UTC).");
        }
        if (local.Year < EarliestYear || utc.Year < EarliestYear)
            return DmlAdmission.Refuse(Unsupported, $"the date and time is outside the range Dataverse stores ({EarliestYear} to 9999, in UTC).");
        return use == DmlValueUse.Compare
            ? DmlAdmission.Admit(utc.ToString(UtcInstantFormat, CultureInfo.InvariantCulture))
            : Sent(literal, Coerce(literal.Text, target), sent => sent is DateTime dt && dt == utc && dt.Kind == DateTimeKind.Utc);
    }

    /// <summary>Date only: exactly <c>yyyy-MM-dd</c>, a real date from 1753 to 9999.</summary>
    private static DmlAdmission AdmitDateOnly(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        const string form = "Write the date exactly as yyyy-MM-dd, for example 2024-01-15.";
        if (literal.Kind is not (DmlLiteralKind.Text or DmlLiteralKind.NationalText))
            return DmlAdmission.Refuse(Unsupported, "this is a date-only column and the value is not a quoted date. " + form);
        if (!DateOnlyForm.IsMatch(literal.Text)
            || !DateTime.TryParseExact(literal.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return DmlAdmission.Refuse(Unsupported, "this is a date-only column and the value is not a date in the one form admitted. " + form);
        }
        if (date.Year < EarliestYear)
            return DmlAdmission.Refuse(Unsupported, $"the date is outside the range Dataverse stores ({EarliestYear} to 9999).");
        return use == DmlValueUse.Compare
            ? DmlAdmission.Admit(literal.Text)
            : Sent(literal, Coerce(literal.Text, target), sent => sent is DateTime dt && dt == date && dt.TimeOfDay == TimeSpan.Zero);
    }

    // ── yes/no, choices, row ids ─────────────────────────────────────────

    /// <summary>Yes/no: exactly <c>0</c> or <c>1</c>.</summary>
    private static DmlAdmission AdmitBoolean(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (literal.Kind != DmlLiteralKind.WholeNumber || literal.Negative || literal.Text is not ("0" or "1"))
        {
            return DmlAdmission.Refuse(Unsupported, use == DmlValueUse.Write
                ? "this is a yes/no column; write 1, 0 or NULL."
                : "this is a yes/no column; compare it with 1 or 0.");
        }
        return use == DmlValueUse.Compare
            ? DmlAdmission.Admit(literal.Text)
            : Sent(literal, Coerce(CompiledNumber(literal), target), sent => sent is bool b && b == (literal.Text == "1"));
    }

    /// <summary>Choice: a whole number within Int32; written, one of the column's choices.</summary>
    private static DmlAdmission AdmitChoice(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        if (literal.Kind != DmlLiteralKind.WholeNumber || !Digits.IsMatch(literal.Text) || !FitsInt32(literal))
            return DmlAdmission.Refuse(Unsupported, use == DmlValueUse.Write
                ? "this is a choice column; write the choice's whole-number value (for example 1)."
                : "this column holds whole numbers (the values of its choices); compare it with a whole number.");
        if (use == DmlValueUse.Compare)
            return DmlAdmission.Admit(literal.Text);
        var value = (int)Convert.ToInt64(CompiledNumber(literal), CultureInfo.InvariantCulture);
        if (target.Options is not { Count: > 0 } options || options.All(o => o.Value != value))
        {
            var known = target.Options is { Count: > 0 } ? string.Join(", ", target.Options.Select(o => $"{o.Value} ({o.Label})")) : "unknown";
            return DmlAdmission.Refuse(Unsupported, $"the value is not a choice of this column. Its choices are: {known}.");
        }
        return Sent(literal, Coerce(CompiledNumber(literal), target), sent => sent is OptionSetValue o && o.Value == value);
    }

    /// <summary>Row id: a quoted GUID (or the GUID cast) as 8-4-4-4-12 hexadecimal digits with dashes, optionally in braces, nothing else.</summary>
    private static DmlAdmission AdmitGuid(DmlLiteral literal, AttributeMetadataDto target, DmlValueUse use)
    {
        var quoted = literal.Kind is DmlLiteralKind.Text or DmlLiteralKind.NationalText
                     || (literal.Kind == DmlLiteralKind.GuidCast && use == DmlValueUse.Write);
        if (!quoted || !CanonicalGuidForm.IsMatch(literal.Text))
        {
            return DmlAdmission.Refuse(Unsupported, use == DmlValueUse.Write
                ? "this column is a lookup; write the row id as a quoted GUID with dashes and without spaces, optionally in braces (for example '00000000-0000-0000-0000-000000000000'), or NULL."
                : "this column holds row ids; compare it with a quoted GUID with dashes and without spaces, optionally in braces (for example '00000000-0000-0000-0000-000000000000').");
        }
        var id = System.Guid.Parse(literal.Text);
        if (use == DmlValueUse.Compare)
            return DmlAdmission.Admit(literal.Text);
        return Sent(literal, Coerce(literal.Text, target), sent => sent is EntityReference r && r.Id == id);
    }

    // ── the per-value rule of the statement's pass ───────────────────────

    /// <summary>
    /// The per-value rule for a value about to be written in the statement's pass, before its first write, whatever its
    /// source (a column read from the source rows, or a literal already admitted): <paramref name="source"/> is the value
    /// the statement produced and <paramref name="sent"/> the value after <see cref="DmlValueCoercer.Coerce"/>. Null when
    /// it is written; otherwise the refusal.
    /// </summary>
    /// <param name="source">The value before conversion to the column's type.</param>
    /// <param name="sent">The value that would be sent.</param>
    /// <param name="target">The column.</param>
    /// <returns>Null, or the refusal.</returns>
    public static DmlAdmission? CheckSentValue(object? source, object? sent, AttributeMetadataDto target)
    {
        if (sent is null)
            return null;
        switch (KindOf(target))
        {
            case DmlTargetKind.Text when sent is string text:
                return TextRule(text);
            case DmlTargetKind.WholeNumber when IsNumber(source) && sent is int whole:
                return Convert.ToDecimal(source, CultureInfo.InvariantCulture) == whole ? null : Lost("a whole-number column");
            case DmlTargetKind.Decimal when sent is decimal exact:
                var places = target.Precision ?? DefaultDecimalPlaces;
                return Places(exact) > places || (IsNumber(source) && source is not double && Convert.ToDecimal(source, CultureInfo.InvariantCulture) != exact)
                    ? Lost($"a decimal column that keeps {places} places")
                    : null;
            case DmlTargetKind.Money when sent is Money money:
                if (MoneyPlacesKept(target) is not { } moneyPlaces)
                    return Places(money.Value) > 0 ? DmlAdmission.Refuse(Unsupported, PrecisionNotTheColumns(target)) : null;
                return Places(money.Value) > moneyPlaces ? Lost($"a currency column that keeps {moneyPlaces} places") : null;
            case DmlTargetKind.FloatingPoint when sent is double d && source is decimal or int or long:
                return SameDigits(d.ToString("R", CultureInfo.InvariantCulture), Convert.ToString(source, CultureInfo.InvariantCulture)!)
                    ? null
                    : Lost("a floating-point column");
            default:
                return null;
        }
    }

    /// <summary>
    /// The places a currency column keeps: its own precision (at most four) when the precision comes from the column
    /// (<see cref="AttributeMetadataDto.PrecisionSource"/> 0); null when it comes from the currency or the organisation's
    /// pricing setting, or was not read, which PPDS cannot establish.
    /// </summary>
    private static int? MoneyPlacesKept(AttributeMetadataDto target)
        => target.PrecisionSource == 0 ? Math.Min(target.Precision ?? MoneyPlaces, MoneyPlaces) : null;

    private static string PrecisionNotTheColumns(AttributeMetadataDto target) => (target.PrecisionSource switch
    {
        1 => "the column's decimal places come from the organisation's pricing setting, not from the column",
        2 => "the column's decimal places come from the currency of each row, not from the column",
        null => "where the column's decimal places come from (the column, the currency or the pricing setting) was not read",
        _ => "the column's decimal places come from a source PPDS does not know"
    }) + ", so how many places Dataverse keeps cannot be established. Write a whole amount, without decimal places.";

    private static DmlAdmission Lost(string where) => DmlAdmission.Refuse(StoredDifferently,
        $"a value of the source would be stored as a different value in {where}. Change the source values, or write literals.");

    // ── shared ───────────────────────────────────────────────────────────

    /// <summary>
    /// True when every character of <paramref name="text"/> is 7-bit ASCII. A text literal without the N prefix that holds
    /// any other character is refused (maintainer decisions of 2026-11-24 and 2026-11-25).
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether every character is at most U+007F.</returns>
    public static bool IsSevenBitAscii(string text) => text.All(c => c <= '\u007F');

    /// <summary>
    /// True when <paramref name="text"/> is not a sequence of XML 1.0 characters (production <c>Char</c>): it holds a control
    /// character below U+0020 other than tab, line feed and carriage return, U+FFFE, U+FFFF, or an unpaired surrogate. A
    /// FetchXML condition cannot carry such text.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether such a character occurs.</returns>
    public static bool HasCharacterOutsideXml(string text)
        => HasUnpairedSurrogate(text) || text.Any(c => c < '\u0020' && c is not ('\t' or '\n' or '\r') || c is '\uFFFE' or '\uFFFF');

    /// <summary>True when <paramref name="text"/> holds a high surrogate not followed by a low one, or a low surrogate not preceded by a high one.</summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether an unpaired surrogate occurs.</returns>
    public static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> holds a backslash directly followed by a carriage return or a line feed: SQL Server
    /// reads it as a line continuation and removes the backslash and the line break after it (a carriage return and a line
    /// feed together, or a line feed; before a carriage return alone, the backslash only). Checked on the reference SQL
    /// Server: a backslash before any other character, a space or a tab before the line break included, is read as typed.
    /// </summary>
    /// <param name="text">The text of a literal, as ScriptDom reads it (the characters between the quotes).</param>
    /// <returns>Whether a line continuation occurs.</returns>
    public static bool HasLineContinuation(string text)
    {
        for (var i = 0; i + 1 < text.Length; i++)
        {
            if (text[i] == '\\' && text[i + 1] is '\r' or '\n')
                return true;
        }
        return false;
    }

    /// <summary>
    /// True when <paramref name="text"/> begins or ends with a character of the Unicode White_Space property (so also when
    /// it is only white space): U+0009 to U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000 to U+200A, U+2028, U+2029,
    /// U+202F, U+205F, U+3000 (specs/sql-values.md, core requirement 4); or with one of the invisible characters
    /// <see cref="InvisibleEdgeCharacters"/>, which are not in that set.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>Whether either end is white space or an invisible character.</returns>
    public static bool IsWhiteSpaceEdged(string text)
        => text.Length > 0 && (IsEdgeCharacter(text[0]) || IsEdgeCharacter(text[^1]));

    /// <summary>
    /// The invisible characters refused at either end of written text as white space is, though the Unicode White_Space set
    /// does not have them: U+180E (Mongolian vowel separator, White_Space before Unicode 6.3), U+200B (zero width space) and
    /// U+FEFF (zero width no-break space, the byte order mark). A reader cannot see them, and Dataverse has not been shown to
    /// store them unchanged at either end of a value.
    /// </summary>
    public const string InvisibleEdgeCharacters = "\u180E\u200B\uFEFF";

    private static bool IsEdgeCharacter(char c) => IsUnicodeWhiteSpace(c) || InvisibleEdgeCharacters.Contains(c);

    private static bool IsUnicodeWhiteSpace(char c) => c is >= '\u0009' and <= '\u000D' or '\u0020' or '\u0085' or '\u00A0'
        or '\u1680' or >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000';

    /// <summary>
    /// The value the executor computes for a number literal, as the query engine compiles it: a whole number within Int32
    /// is an int (ScriptDom's IntegerLiteral); a larger one, like a decimal literal, is a NumericLiteral and so a decimal;
    /// then the minus sign. Null when it does not fit a decimal.
    /// </summary>
    private static object? CompiledNumber(DmlLiteral literal)
    {
        object value;
        if (literal.Kind == DmlLiteralKind.WholeNumber && int.TryParse(literal.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var i32))
            value = i32;
        else if (decimal.TryParse(literal.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d))
            value = d;
        else
            return null;
        if (!literal.Negative)
            return value;
        return value switch
        {
            int i => -i,
            decimal m => -m,
            _ => value
        };
    }

    private static bool FitsInt32(DmlLiteral literal)
        => long.TryParse(literal.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var magnitude)
           && (literal.Negative ? -magnitude >= int.MinValue : magnitude <= int.MaxValue);

    private static bool IsNumber(object? value) => value is int or long or decimal or double or float;

    private static object? Coerce(object? value, AttributeMetadataDto target)
    {
        try
        {
            return DmlValueCoercer.Coerce(value, target);
        }
        catch (QueryExecutionException)
        {
            return null;
        }
    }

    private static object? Coerce(string text, string attributeType)
        => Coerce(text, new AttributeMetadataDto { LogicalName = "v", DisplayName = "v", SchemaName = "v", AttributeType = attributeType });

    /// <summary>Admits the value that will be sent when it is the literal's value exactly (<paramref name="exact"/>); otherwise refuses.</summary>
    private static DmlAdmission Sent(DmlLiteral literal, object? sent, Func<object?, bool> exact)
        => sent is not null && exact(sent)
            ? DmlAdmission.Admit(sent)
            : DmlAdmission.Refuse(StoredDifferently, "the value would not be sent to Dataverse exactly as written.");

    /// <summary>The number of decimal places of a number literal, trailing zeros of the fraction not counted.</summary>
    private static int Places(DmlLiteral literal) => Places(Canonical(literal.Text, literal.Negative));

    private static int Places(decimal value) => Places(Canonical(value.ToString(CultureInfo.InvariantCulture), negative: false));

    /// <summary>The number of digits before the point of a number literal, leading zeros not counted.</summary>
    private static int IntegerDigits(DmlLiteral literal)
    {
        var (digits, exponent, _) = Canonical(literal.Text, literal.Negative);
        return digits == "0" ? 0 : Math.Max(0, digits.Length + exponent);
    }

    private static int Places((string Digits, int Exponent, bool Negative) canonical) => Math.Max(0, -canonical.Exponent);

    /// <summary>True when <paramref name="text"/> (a number as .NET writes it, possibly in E notation) has the literal's value digit for digit.</summary>
    private static bool SameDigits(string text, DmlLiteral literal) => Canonical(text, negative: false) == Canonical(literal.Text, literal.Negative);

    private static bool SameDigits(string a, string b) => Canonical(a, negative: false) == Canonical(b, negative: false);

    /// <summary>
    /// A number as (significant digits without leading or trailing zeros, power of ten, sign); zero is ("0", 0, false)
    /// whatever its sign or spelling. Accepts digits with an optional fraction, a leading minus, and E notation.
    /// </summary>
    internal static (string Digits, int Exponent, bool Negative) Canonical(string text, bool negative)
    {
        var s = text.Trim();
        if (s.StartsWith('-'))
        {
            negative = !negative;
            s = s[1..];
        }
        var exponent = 0;
        var e = s.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(s[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            s = s[..e];
        }
        var point = s.IndexOf('.');
        if (point >= 0)
        {
            exponent -= s.Length - point - 1;
            s = s.Remove(point, 1);
        }
        s = s.TrimStart('0');
        if (s.Length == 0)
            return ("0", 0, false);
        while (s.EndsWith('0'))
        {
            s = s[..^1];
            exponent++;
        }
        return (s, exponent, negative);
    }

    private static bool HasNoZone(string text) => !Regex.IsMatch(text, @"(Z|[+-][0-9]{2}:?[0-9]{2})\s*\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
