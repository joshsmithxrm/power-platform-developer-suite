using System.Globalization;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Metadata;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Explicit choices the caller makes for one column, beyond the column's metadata (from a <c>data load</c> mapping file).
/// </summary>
public sealed record ColumnValueOptions
{
    /// <summary>
    /// A .NET custom date format given for the column, read in the invariant culture. It replaces the ISO forms; the
    /// parsed value must still be exact for the column (a zone for a user-local column, no time for a date-only one).
    /// </summary>
    public string? DateFormat { get; init; }

    /// <summary>Text labels mapped to choice values, matched exactly before the value is read as an integer.</summary>
    public IReadOnlyDictionary<string, int>? OptionLabels { get; init; }
}

/// <summary>
/// The outcome of converting one value: admitted with the value to send, or refused with the reason.
/// </summary>
/// <param name="IsAdmitted">Whether the text is exact for the column.</param>
/// <param name="Value">The value to send when admitted.</param>
/// <param name="Refusal">
/// When refused, what the column takes, in a sentence that repeats nothing of the text (the caller adds where it was).
/// </param>
public readonly record struct ColumnValueResult(bool IsAdmitted, object? Value, string? Refusal)
{
    internal static ColumnValueResult Admit(object value) => new(true, value, null);

    internal static ColumnValueResult Refuse(string reason) => new(false, null, reason);
}

/// <summary>
/// The one rule by which <c>data load</c> and <c>data update</c> turn the text of a CSV cell or a <c>--set</c> value into
/// the value sent for a column (#1916, #1917). The value is converted by the target column's type from metadata and is
/// never guessed from its text. Numbers are read in the invariant culture, in one exact form, and never in the
/// machine's culture. Text that is not exact for its column is refused, so nothing is written from it.
/// </summary>
/// <remarks>
/// The forms, by the column's type:
/// <list type="bullet">
/// <item>Text (single line, multiple lines, table name): the text as given, nothing trimmed. Refused: a NUL or another
/// control character other than tab, line feed and carriage return (a request cannot carry it), and a lone surrogate.</item>
/// <item>Whole number and big integer: an optional sign and ASCII digits, in the type's range.</item>
/// <item>
/// Decimal, floating point and currency: an optional sign, digits, and optionally one point and digits. No thousands
/// separator, no comma, no exponent, no currency symbol, no white space; at most 28 significant digits and 28 places. No
/// more places than the column keeps. Currency: the column's precision (at most 4) when the column itself is the precision
/// source; when the source is the organization's pricing precision or the record's currency, which are not read here,
/// whole amounts only. A floating-point value must be held by a double whose shortest round-trip digits are exactly the
/// digits written; negative zero is sent as zero.
/// </item>
/// <item>Yes/no: <c>true</c>/<c>false</c>, <c>yes</c>/<c>no</c>, <c>y</c>/<c>n</c>, <c>1</c>/<c>0</c>, in any case.</item>
/// <item>
/// Date and time, by the column's behaviour. Date only: <c>yyyy-MM-dd</c>. User local: <c>yyyy-MM-ddTHH:mm:ssZ</c> or
/// <c>yyyy-MM-ddTHH:mm:ss+hh:mm</c>, sent as the UTC instant. Time-zone independent: <c>yyyy-MM-dd</c> when the column
/// shows only the date, else <c>yyyy-MM-ddTHH:mm:ssZ</c> with the clock stored as written. From 1753-01-01 on.
/// </item>
/// <item>Unique identifier: a GUID by <see cref="RecordIdText"/> (8-4-4-4-12, optionally in braces, nothing around it, not all zeros).</item>
/// <item>Choice, status and status reason: a label from <see cref="ColumnValueOptions.OptionLabels"/>, or the integer
/// value of one of the column's options. A column whose options are not in the metadata is refused.</item>
/// <item>Lookup with exactly one target table: a GUID, sent as a reference to that table. A lookup that can point to
/// more than one table (customer, owner, regarding) is refused: the table cannot be known from a GUID.</item>
/// <item>Every other type (multi-select choice, party list, file, image, ...) is refused.</item>
/// </list>
/// When the shared exact-admission family of SQL writes is on <c>main</c>, this function is to be replaced by it; the
/// callers depend only on <see cref="Convert"/>.
/// </remarks>
public static class ColumnValueConverter
{
    private const string NumberForm =
        "digits with an optional sign and an optional point and digits (1234.75): no thousands separator, no comma, no exponent";

    private static readonly HashSet<string> TrueTexts = new(StringComparer.OrdinalIgnoreCase) { "true", "yes", "y", "1" };
    private static readonly HashSet<string> FalseTexts = new(StringComparer.OrdinalIgnoreCase) { "false", "no", "n", "0" };

    private static readonly DateTime EarliestDate = new(1753, 1, 1);

    /// <summary>
    /// Converts <paramref name="text"/> for <paramref name="column"/>, or refuses it.
    /// </summary>
    /// <param name="text">The text of the value. Empty text is the caller's to handle (it means no value or null).</param>
    /// <param name="column">The target column's metadata, as read from the environment.</param>
    /// <param name="options">Explicit choices for this column, if any.</param>
    public static ColumnValueResult Convert(string text, AttributeMetadata column, ColumnValueOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(column);

        if (text.Length == 0)
        {
            return ColumnValueResult.Refuse("The value is empty.");
        }

        if (column is MultiSelectPicklistAttributeMetadata)
        {
            return ColumnValueResult.Refuse("A multi-select choice column cannot be written by this command.");
        }

        return column.AttributeType switch
        {
            AttributeTypeCode.String or AttributeTypeCode.Memo or AttributeTypeCode.EntityName => ConvertText(text),
            AttributeTypeCode.Integer => ConvertInteger(text),
            AttributeTypeCode.BigInt => ConvertBigInt(text),
            AttributeTypeCode.Decimal => ConvertDecimal(text, (column as DecimalAttributeMetadata)?.Precision),
            AttributeTypeCode.Double => ConvertDouble(text, (column as DoubleAttributeMetadata)?.Precision),
            AttributeTypeCode.Money => ConvertMoney(text, column as MoneyAttributeMetadata),
            AttributeTypeCode.Boolean => ConvertBoolean(text),
            AttributeTypeCode.DateTime => ConvertDateTime(text, column as DateTimeAttributeMetadata, options?.DateFormat),
            AttributeTypeCode.Uniqueidentifier => RecordIdText.TryParse(text, out var id)
                ? ColumnValueResult.Admit(id)
                : ColumnValueResult.Refuse($"A unique identifier column takes a GUID: {RecordIdText.Rule}; not the empty (all-zero) GUID."),
            AttributeTypeCode.Picklist or AttributeTypeCode.State or AttributeTypeCode.Status =>
                ConvertChoice(text, column as EnumAttributeMetadata, options?.OptionLabels),
            AttributeTypeCode.Lookup or AttributeTypeCode.Customer or AttributeTypeCode.Owner =>
                ConvertLookup(text, column as LookupAttributeMetadata),
            null => ColumnValueResult.Refuse("The column's type is not known from its metadata."),
            _ => ColumnValueResult.Refuse($"A column of type {column.AttributeType} cannot be written by this command."),
        };
    }

    private static ColumnValueResult ConvertText(string text)
    {
        var refusal = TextRefusal(text);
        return refusal == null ? ColumnValueResult.Admit(text) : ColumnValueResult.Refuse(refusal);
    }

    /// <summary>
    /// Why <paramref name="text"/> cannot be sent as text, or null: a NUL or another control character other than tab, line
    /// feed and carriage return, a lone surrogate, or U+FFFE / U+FFFF (none can be carried by the request).
    /// </summary>
    internal static string? TextRefusal(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c < ' ' && c != '\t' && c != '\n' && c != '\r')
            {
                return $"The text holds a control character (U+{(int)c:X4}) at position {i + 1}; only tab, line feed and carriage return can be sent.";
            }

            if (c is '\uFFFE' or '\uFFFF')
            {
                return $"The text holds a noncharacter (U+{(int)c:X4}) at position {i + 1}.";
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                return $"The text holds a lone surrogate at position {i + 1}, which is not a character.";
            }
        }

        return null;
    }

    private static ColumnValueResult ConvertInteger(string text)
    {
        if (!IsExactNumber(text, allowFraction: false, out _))
        {
            return ColumnValueResult.Refuse("A whole number column takes an optional sign and digits, nothing else.");
        }

        return int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? ColumnValueResult.Admit(value)
            : ColumnValueResult.Refuse("The number is outside the range of a whole number column.");
    }

    private static ColumnValueResult ConvertBigInt(string text)
    {
        if (!IsExactNumber(text, allowFraction: false, out _))
        {
            return ColumnValueResult.Refuse("A big integer column takes an optional sign and digits, nothing else.");
        }

        return long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? ColumnValueResult.Admit(value)
            : ColumnValueResult.Refuse("The number is outside the range of a big integer column.");
    }

    private static ColumnValueResult ConvertDecimal(string text, int? precision)
    {
        var read = ReadDecimal(text, precision, "A decimal column");
        return read.Refusal != null ? ColumnValueResult.Refuse(read.Refusal) : ColumnValueResult.Admit(read.Value);
    }

    private static ColumnValueResult ConvertMoney(string text, MoneyAttributeMetadata? column)
    {
        // PrecisionSource 0: the column's own Precision governs; 1: the organization's pricing decimal precision; 2: the
        // record's currency precision. Only the first is in the column's metadata, so with the others a value with
        // places could be rounded to a precision not read here: whole amounts only, until that precision is read.
        if (column?.PrecisionSource is not 0)
        {
            var whole = ReadDecimal(text, 4, "A currency column");
            if (whole.Refusal != null)
            {
                return ColumnValueResult.Refuse(whole.Refusal);
            }

            return whole.Value != decimal.Truncate(whole.Value)
                ? ColumnValueResult.Refuse(
                    "The currency column takes its decimal places from the organization's pricing precision or the record's " +
                    "currency, which is not read here, so it takes whole amounts only.")
                : ColumnValueResult.Admit(new Money(whole.Value));
        }

        // Currency keeps at most four places whatever its precision says.
        var read = ReadDecimal(text, column.Precision is { } p ? Math.Min(p, 4) : 4, "A currency column");
        return read.Refusal != null ? ColumnValueResult.Refuse(read.Refusal) : ColumnValueResult.Admit(new Money(read.Value));
    }

    private static ColumnValueResult ConvertDouble(string text, int? precision)
    {
        var read = ReadDecimal(text, precision, "A floating-point column");
        if (read.Refusal != null)
        {
            return ColumnValueResult.Refuse(read.Refusal);
        }

        var value = double.Parse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);

        // Compared as digit strings, so nothing is rounded on the way: the double's shortest round-trip spelling must
        // have exactly the significant digits and the magnitude of the text.
        var heldExactly = double.IsFinite(value)
            && Canonical(value.ToString("R", CultureInfo.InvariantCulture)) == Canonical(text);
        if (!heldExactly)
        {
            return ColumnValueResult.Refuse(
                "A floating-point column holds a double, and no double has exactly the digits written; write fewer digits.");
        }

        // Negative zero is zero.
        return ColumnValueResult.Admit(value == 0 ? 0d : value);
    }

    private static (decimal Value, string? Refusal) ReadDecimal(string text, int? places, string what)
    {
        if (!IsExactNumber(text, allowFraction: true, out var fractionDigits))
        {
            return (0, $"{what} takes {NumberForm}.");
        }

        // Places that are written as zeros at the end change nothing.
        var significant = fractionDigits.TrimEnd('0').Length;
        var (digits, _) = Canonical(text);
        if (significant > MaxDigits || (digits != "0" && digits.Length > MaxDigits))
        {
            return (0, $"The number has more than {MaxDigits} significant digits or places, more than {Lower(what)} can be sent.");
        }

        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var value))
        {
            return (0, $"The number is too large for {Lower(what)}.");
        }
        if (significant > 0 && places is null)
        {
            return (0, $"The number of decimal places {Lower(what)} keeps is not known from its metadata.");
        }

        if (places is { } kept && significant > kept)
        {
            return (0, $"{what} keeps {kept} decimal place(s), and the number has more; round it first.");
        }

        return (value, null);
    }

    private static string Lower(string what) => char.ToLowerInvariant(what[0]) + what[1..];

    private const int MaxDigits = 28;

    /// <summary>
    /// The significant digits (no leading or trailing zeros, <c>"0"</c> for zero), with the sign, and the power of ten of
    /// the last digit, of a number spelled <c>[-+]digits[.digits][E[-+]digits]</c>.
    /// </summary>
    private static (string Digits, int Exponent) Canonical(string number)
    {
        var negative = number.StartsWith('-');
        var body = number.TrimStart('-', '+');
        var exponent = 0;
        var e = body.IndexOfAny(['E', 'e']);
        if (e >= 0)
        {
            exponent = int.Parse(body[(e + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            body = body[..e];
        }

        var point = body.IndexOf('.');
        if (point >= 0)
        {
            exponent -= body.Length - point - 1;
            body = body.Remove(point, 1);
        }

        body = body.TrimStart('0');
        if (body.Length == 0)
        {
            return ("0", 0);
        }

        var trimmed = body.TrimEnd('0');
        exponent += body.Length - trimmed.Length;
        return ((negative ? "-" : "") + trimmed, exponent);
    }

    /// <summary>
    /// Whether <paramref name="text"/> is an optional sign, ASCII digits and, when allowed, one point followed by digits.
    /// </summary>
    private static bool IsExactNumber(string text, bool allowFraction, out string fractionDigits)
    {
        fractionDigits = string.Empty;
        var i = text.Length > 0 && (text[0] == '-' || text[0] == '+') ? 1 : 0;
        var integerStart = i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == integerStart)
        {
            return false;
        }

        if (i == text.Length)
        {
            return true;
        }

        if (!allowFraction || text[i] != '.')
        {
            return false;
        }

        var fractionStart = ++i;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        if (i == fractionStart || i != text.Length)
        {
            return false;
        }

        fractionDigits = text[fractionStart..];
        return true;
    }

    private static ColumnValueResult ConvertBoolean(string text)
    {
        if (TrueTexts.Contains(text))
        {
            return ColumnValueResult.Admit(true);
        }

        return FalseTexts.Contains(text)
            ? ColumnValueResult.Admit(false)
            : ColumnValueResult.Refuse("A yes/no column takes true or false (also yes/no, y/n, 1/0).");
    }

    private static ColumnValueResult ConvertDateTime(string text, DateTimeAttributeMetadata? column, string? dateFormat)
    {
        var behavior = column?.DateTimeBehavior?.Value;
        var userLocal = behavior == DateTimeBehavior.UserLocal.Value;
        var independent = behavior == DateTimeBehavior.TimeZoneIndependent.Value;
        var dateOnly = behavior == DateTimeBehavior.DateOnly.Value || (independent && column!.Format == DateTimeFormat.DateOnly);

        if (!userLocal && !independent && !dateOnly)
        {
            return ColumnValueResult.Refuse("The column's date and time behaviour is not known from its metadata.");
        }

        if (dateFormat != null && DateFormatRefusal(dateFormat) is { } badFormat)
        {
            return ColumnValueResult.Refuse(badFormat);
        }

        if (dateOnly)
        {
            return ConvertDate(text, dateFormat);
        }

        if (independent)
        {
            // The clock is stored as sent, with no conversion, so the value names a clock, not an instant to convert.
            if (dateFormat != null || !TryReadIsoInstant(text, utcOnly: true, out var clock))
            {
                return ColumnValueResult.Refuse(
                    "A time-zone independent date and time column takes yyyy-MM-ddTHH:mm:ssZ; the time is stored as written.");
            }

            return InRange(clock);
        }

        // User local: stored as a UTC instant, so the text must say which instant.
        if (dateFormat != null)
        {
            if (!DateTime.TryParseExact(text, dateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                return ColumnValueResult.Refuse("The date and time is not in the dateFormat given for the column.");
            }

            if (parsed.Kind != DateTimeKind.Utc)
            {
                return ColumnValueResult.Refuse(
                    "The column stores a date and time in UTC, and the dateFormat given for it has no time zone (K or zzz).");
            }

            return parsed.Ticks % TimeSpan.TicksPerSecond != 0
                ? ColumnValueResult.Refuse("A date and time is sent to whole seconds; the value has a fraction of a second.")
                : InRange(parsed);
        }

        return TryReadIsoInstant(text, utcOnly: false, out var instant)
            ? InRange(instant)
            : ColumnValueResult.Refuse(column!.Format == DateTimeFormat.DateOnly
                ? "The column shows a date but stores a UTC date and time (user local), so it takes yyyy-MM-ddTHH:mm:ssZ or yyyy-MM-ddTHH:mm:ss+hh:mm."
                : "A date and time column takes yyyy-MM-ddTHH:mm:ssZ or yyyy-MM-ddTHH:mm:ss+hh:mm.");
    }

    /// <summary>
    /// Why a mapping's <c>dateFormat</c> cannot be used, or null when it can. A format must name every part of the date
    /// itself, so that nothing is taken from the machine's clock or settings: a four-digit year (<c>yyyy</c>), a month
    /// (<c>M</c>, <c>MM</c>, <c>MMM</c> or <c>MMMM</c>) and a day of the month (<c>d</c> or <c>dd</c>). A missing part would
    /// be the current year, or today's date; a two-digit year (<c>yy</c>) is given a century by a setting of the machine.
    /// A fraction of a second (<c>f</c>, <c>F</c>) is refused, as the fixed forms refuse it, and so is a one-letter format,
    /// which .NET reads as a standard format, not as the letter.
    /// </summary>
    internal static string? DateFormatRefusal(string format)
    {
        if (format.Length == 1)
        {
            return "The dateFormat is one letter, which .NET reads as a standard format; write the parts, such as dd/MM/yyyy.";
        }

        bool year = false, month = false, day = false, badYear = false, fraction = false;
        for (var i = 0; i < format.Length;)
        {
            var c = format[i];
            if (c is '\'' or '"')
            {
                var end = format.IndexOf(c, i + 1);
                i = end < 0 ? format.Length : end + 1;
                continue;
            }

            if (c == '\\')
            {
                i += 2;
                continue;
            }

            if (c == '%')
            {
                i++;
                continue;
            }

            var run = 1;
            while (i + run < format.Length && format[i + run] == c)
            {
                run++;
            }

            switch (c)
            {
                case 'y':
                    year |= run == 4;
                    badYear |= run != 4;
                    break;
                case 'M':
                    month |= run <= 4;
                    break;
                case 'd':
                    day |= run <= 2;
                    break;
                case 'f' or 'F':
                    fraction = true;
                    break;
            }

            i += run;
        }

        if (badYear || !year || !month || !day)
        {
            return "The dateFormat must name a four-digit year (yyyy), a month (MM) and a day (dd), such as dd/MM/yyyy: " +
                "a missing part, or a two-digit year, would be taken from the machine.";
        }

        return fraction
            ? "The dateFormat has a fraction of a second (f or F); a date and time is sent to whole seconds."
            : null;
    }

    private static ColumnValueResult ConvertDate(string text, string? dateFormat)
    {
        if (!DateTime.TryParseExact(text, dateFormat ?? "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return ColumnValueResult.Refuse(dateFormat == null
                ? "A date-only column takes yyyy-MM-dd."
                : "The date is not in the dateFormat given for the column.");
        }

        if (date.Kind != DateTimeKind.Unspecified || date.TimeOfDay != TimeSpan.Zero)
        {
            return ColumnValueResult.Refuse("A date-only column takes a date with no time and no time zone.");
        }

        return InRange(date);
    }

    private static ColumnValueResult InRange(DateTime value) =>
        value < EarliestDate
            ? ColumnValueResult.Refuse("A date is from 1753-01-01 on.")
            : ColumnValueResult.Admit(value);

    /// <summary>
    /// Reads <c>yyyy-MM-ddTHH:mm:ssZ</c> or (unless <paramref name="utcOnly"/>) <c>yyyy-MM-ddTHH:mm:ss+hh:mm</c>, exactly,
    /// as a UTC <see cref="DateTime"/>.
    /// </summary>
    private static bool TryReadIsoInstant(string text, bool utcOnly, out DateTime utc)
    {
        utc = default;
        var withZ = text.Length == 20 && text[19] == 'Z';
        var withOffset = !utcOnly && text.Length == 25 && (text[19] == '+' || text[19] == '-') && text[22] == ':'
            && char.IsAsciiDigit(text[20]) && char.IsAsciiDigit(text[21]) && char.IsAsciiDigit(text[23]) && char.IsAsciiDigit(text[24]);
        if (!withZ && !withOffset)
        {
            return false;
        }

        if (!DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var clock))
        {
            return false;
        }

        if (withZ)
        {
            utc = DateTime.SpecifyKind(clock, DateTimeKind.Utc);
            return true;
        }

        var hours = int.Parse(text.AsSpan(20, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var minutes = int.Parse(text.AsSpan(23, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var offset = new TimeSpan(hours, minutes, 0);
        if (minutes >= 60 || offset > TimeSpan.FromHours(14))
        {
            return false;
        }

        var ticks = clock.Ticks - (text[19] == '-' ? -offset.Ticks : offset.Ticks);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return false;
        }

        utc = new DateTime(ticks, DateTimeKind.Utc);
        return true;
    }

    private static ColumnValueResult ConvertChoice(string text, EnumAttributeMetadata? column, IReadOnlyDictionary<string, int>? labels)
    {
        int value;
        if (labels != null && labels.TryGetValue(text, out var mapped))
        {
            value = mapped;
        }
        else if (!IsExactNumber(text, allowFraction: false, out _)
            || !int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
        {
            return ColumnValueResult.Refuse(labels is { Count: > 0 }
                ? "A choice column takes one of the labels of its optionsetMap or the integer value of an option."
                : "A choice column takes the integer value of an option (or add an optionsetMap to the mapping file).");
        }

        var options = column?.OptionSet?.Options;
        if (options is not { Count: > 0 })
        {
            return ColumnValueResult.Refuse("The column's options are not in its metadata, so the value cannot be checked.");
        }

        if (!options.Any(o => o.Value == value))
        {
            return ColumnValueResult.Refuse("The value is not one of the column's options.");
        }

        return ColumnValueResult.Admit(new OptionSetValue(value));
    }

    private static ColumnValueResult ConvertLookup(string text, LookupAttributeMetadata? column)
    {
        var targets = column?.Targets;
        if (targets is not { Length: 1 } || string.IsNullOrEmpty(targets[0]))
        {
            return ColumnValueResult.Refuse(
                "The lookup can point to more than one table (or its table is not known), so a GUID does not say which record.");
        }

        return RecordIdText.TryParse(text, out var id)
            ? ColumnValueResult.Admit(new EntityReference(targets[0], id))
            : ColumnValueResult.Refuse($"A lookup column takes the GUID of the record: {RecordIdText.Rule}; not the empty (all-zero) GUID.");
    }
}
