using System.Globalization;
using System.Text.RegularExpressions;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Cli.Services.Data;

/// <summary>
/// Admits a value of a <c>--filter</c> for its column's type, or refuses it (issue 1705). This is the one place that
/// decides by column type.
/// </summary>
/// <remarks>
/// <para>
/// Interim rules (step A of the plan for #1912). The SQL write-safety work (#1698) brings the shared admission family
/// for values with column metadata; when it is on <c>main</c>, the body of <see cref="Admit"/> becomes a call to that
/// family, and nothing else in the data layer changes. Until then these rules admit little, on purpose: a value is
/// admitted only where its meaning in FetchXML is the meaning SQL Server gives it, and every column kind not listed is
/// refused.
/// </para>
/// <para>
/// Column kind by value (U = refused; the null test <c>IS [NOT] NULL</c> is admitted on every listed kind):
/// text (String, Memo): quoted text for <c>=</c>, <c>&lt;&gt;</c>, <c>IN</c>, <c>LIKE</c>, no ranges;
/// whole number (Integer): a whole-number literal in the 32-bit range for <c>=</c>, <c>&lt;&gt;</c>, <c>IN</c>, ranges;
/// decimal (Decimal): a number with at most 28 digits and no more decimal places than the column keeps;
/// currency (Money): a number with at most 28 digits and at most 4 decimal places;
/// floating point (Double): a number with at most 15 significant digits, for <c>=</c>, <c>&lt;&gt;</c> and ranges;
/// date and time (DateTime, user local, date and time): quoted <c>yyyy-MM-ddTHH:mm:ssZ</c> for <c>=</c>,
/// <c>&lt;&gt;</c> and ranges; date only (DateTime, date only): quoted <c>yyyy-MM-dd</c> for the same;
/// row id (the table's primary id, a lookup to one table): a quoted GUID written 8-4-4-4-12 with dashes, for
/// <c>=</c>, <c>&lt;&gt;</c>, <c>IN</c>. Choice, status, yes/no, big whole number, lookups to more than one table
/// (customer, owner), other date behaviours, other unique identifiers and every other kind: U.
/// </para>
/// </remarks>
internal static class DataFilterOperandAdmission
{
    private const int MaxDecimalDigits = 28;
    private const int MaxMoneyDecimalPlaces = 4;
    private const int MaxFloatingPointDigits = 15;

    private static readonly Regex NumberForm = new(@"\A-?(?<int>[0-9]*)(?:\.(?<frac>[0-9]*))?\z", RegexOptions.CultureInvariant);
    private static readonly Regex UtcDateTimeForm = new(
        @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z", RegexOptions.CultureInvariant);
    private static readonly Regex DateOnlyForm = new(@"\A[0-9]{4}-[0-9]{2}-[0-9]{2}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CanonicalGuid = new(
        @"\A[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\z", RegexOptions.CultureInvariant);

    private enum ColumnKind
    {
        Unsupported,
        Text,
        WholeNumber,
        Decimal,
        Money,
        FloatingPoint,
        DateTimeUtc,
        DateOnly,
        RowId
    }

    /// <summary>
    /// Admits <paramref name="literal"/> against <paramref name="column"/> as <paramref name="use"/> uses it, and returns
    /// the value to send in FetchXML; or refuses.
    /// </summary>
    /// <param name="column">The table's column, with its metadata and where the filter names it.</param>
    /// <param name="use">How the condition uses the column.</param>
    /// <param name="literal">The value; <see langword="null"/> for <see cref="DataFilterUse.NullTest"/>.</param>
    /// <returns>The FetchXML value text; <see langword="null"/> for the null test.</returns>
    /// <exception cref="PpdsException"><see cref="ErrorCodes.Validation.FilterNotSupported"/> when not admitted.</exception>
    public static string? Admit(DataFilterColumn column, DataFilterUse use, DataFilterLiteral? literal)
    {
        var (kind, kindName) = KindOf(column.Metadata);
        if (kind == ColumnKind.Unsupported)
        {
            throw DataFilter.Refuse(column.Line, column.Column, $"a column of a kind not yet supported in a filter ({kindName})",
                "Leave this column out of the filter, or select the ids with 'query sql' and pass them with --file.");
        }

        if (use == DataFilterUse.NullTest)
            return null;
        if (literal is null)
            throw new InvalidOperationException("A condition with a value reached the admission without one.");

        if (use == DataFilterUse.Like && kind != ColumnKind.Text)
            throw RefuseUse(column, $"LIKE on a {kindName} column", "Use LIKE only on a text column.");

        return kind switch
        {
            ColumnKind.Text => AdmitText(column, use, literal),
            ColumnKind.WholeNumber => AdmitWholeNumber(literal),
            ColumnKind.Decimal => AdmitNumber(literal, kindName, column.Metadata.Precision
                ?? throw RefuseUse(column, "a decimal column whose number of decimal places is not known", "")),
            ColumnKind.Money => AdmitNumber(literal, kindName, MaxMoneyDecimalPlaces),
            ColumnKind.FloatingPoint => AdmitFloatingPoint(column, use, literal),
            ColumnKind.DateTimeUtc => AdmitDate(column, use, literal, UtcDateTimeForm, "yyyy-MM-ddTHH:mm:ssZ",
                "'2024-01-15T09:30:00Z' (UTC, with Z; no offset and no fraction of a second)"),
            ColumnKind.DateOnly => AdmitDate(column, use, literal, DateOnlyForm, "yyyy-MM-dd", "'2024-01-15'"),
            ColumnKind.RowId => AdmitRowId(column, use, literal),
            _ => throw new InvalidOperationException("A column kind without a rule.")
        };
    }

    private static (ColumnKind Kind, string Name) KindOf(AttributeMetadataDto column)
    {
        if (!string.IsNullOrEmpty(column.AttributeOf))
            return (ColumnKind.Unsupported, "computed from another column");

        return column.AttributeType switch
        {
            "String" or "Memo" => (ColumnKind.Text, "text"),
            "Integer" => (ColumnKind.WholeNumber, "whole number"),
            "Decimal" => (ColumnKind.Decimal, "decimal"),
            "Money" => (ColumnKind.Money, "currency"),
            "Double" => (ColumnKind.FloatingPoint, "floating point"),
            "DateTime" when column.DateTimeBehavior == "UserLocal" && column.Format == "DateAndTime" =>
                (ColumnKind.DateTimeUtc, "date and time"),
            "DateTime" when column.DateTimeBehavior == "DateOnly" => (ColumnKind.DateOnly, "date only"),
            "DateTime" => (ColumnKind.Unsupported, "date with this behaviour or format"),
            "Uniqueidentifier" when column.IsPrimaryId => (ColumnKind.RowId, "row id"),
            "Lookup" when column.Targets is { Count: 1 } => (ColumnKind.RowId, "lookup"),
            "Lookup" or "Customer" or "Owner" => (ColumnKind.Unsupported, "lookup to more than one table"),
            "Picklist" or "State" or "Status" => (ColumnKind.Unsupported, "choice"),
            "Boolean" => (ColumnKind.Unsupported, "yes/no"),
            "BigInt" => (ColumnKind.Unsupported, "big whole number"),
            "Uniqueidentifier" => (ColumnKind.Unsupported, "unique identifier other than the row id"),
            _ => (ColumnKind.Unsupported, column.AttributeType)
        };
    }

    private static string AdmitText(DataFilterColumn column, DataFilterUse use, DataFilterLiteral literal)
    {
        if (literal.Kind != DataFilterLiteralKind.Text)
        {
            throw RefuseValue(literal, "a number compared with a text column",
                "SQL Server would convert the column to a number; Dataverse compares text. Quote the value, for example N'5'.");
        }

        if (use == DataFilterUse.Range)
        {
            throw RefuseValue(literal, "a range comparison (<, <=, >, >=, BETWEEN) on a text column",
                "Use =, <>, IN or LIKE on text.");
        }

        return literal.Text;
    }

    private static string AdmitWholeNumber(DataFilterLiteral literal)
    {
        if (literal.Kind != DataFilterLiteralKind.Number || !int.TryParse(literal.Text, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out _))
        {
            throw RefuseValue(literal, "a value that is not a whole number from -2147483648 to 2147483647, against a whole-number column",
                "Write a whole number without quotes or a decimal point, for example 5.");
        }

        return literal.Text;
    }

    private static string AdmitNumber(DataFilterLiteral literal, string kindName, int maxDecimalPlaces)
    {
        if (literal.Kind != DataFilterLiteralKind.Number || NumberForm.Match(literal.Text) is not { Success: true } number)
        {
            throw RefuseValue(literal, $"text compared with a {kindName} column", "Write the number without quotes.");
        }

        var digits = number.Groups["int"].Value.TrimStart('0').Length + number.Groups["frac"].Value.Length;
        if (digits > MaxDecimalDigits)
        {
            throw RefuseValue(literal, $"a number of more than {MaxDecimalDigits} digits",
                "Dataverse would round it, SQL Server would not. Write at most 28 digits.");
        }

        if (number.Groups["frac"].Value.Length > maxDecimalPlaces)
        {
            throw RefuseValue(literal, $"a number with more decimal places than the {kindName} column keeps ({maxDecimalPlaces})",
                "Write the number with no more decimal places than the column keeps.");
        }

        return literal.Text;
    }

    private static string AdmitFloatingPoint(DataFilterColumn column, DataFilterUse use, DataFilterLiteral literal)
    {
        if (use == DataFilterUse.In)
            throw RefuseUse(column, "IN on a floating-point column", "Use = with OR, or a range.");

        if (literal.Kind != DataFilterLiteralKind.Number || NumberForm.Match(literal.Text) is not { Success: true } number)
            throw RefuseValue(literal, "text compared with a floating-point column", "Write the number without quotes.");

        var significant = (number.Groups["int"].Value + number.Groups["frac"].Value).Trim('0').Length;
        if (significant > MaxFloatingPointDigits)
        {
            throw RefuseValue(literal, $"a number of more than {MaxFloatingPointDigits} significant digits against a floating-point column",
                "Write at most 15 significant digits.");
        }

        return literal.Text;
    }

    private static string AdmitDate(DataFilterColumn column, DataFilterUse use, DataFilterLiteral literal, Regex form,
        string formName, string example)
    {
        if (use == DataFilterUse.In)
            throw RefuseUse(column, "IN on a date column", "Use = with OR, or a range.");

        var valid = literal.Kind == DataFilterLiteralKind.Text && form.IsMatch(literal.Text)
            && DateTime.TryParseExact(literal.Text, formName == "yyyy-MM-dd" ? "yyyy-MM-dd" : "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
        if (!valid)
        {
            throw RefuseValue(literal, $"a value against a date column that is not a date written as {formName}",
                $"Write it exactly as {example}.");
        }

        return literal.Text;
    }

    private static string AdmitRowId(DataFilterColumn column, DataFilterUse use, DataFilterLiteral literal)
    {
        if (use == DataFilterUse.Range)
            throw RefuseUse(column, "a range comparison on a row id column", "Use =, <> or IN.");

        if (literal.Kind != DataFilterLiteralKind.Text || !CanonicalGuid.IsMatch(literal.Text))
        {
            throw RefuseValue(literal, "a value against a row id column that is not a GUID written as 8-4-4-4-12 digits with dashes",
                "Write it in quotes, with dashes and without braces, for example '00000000-0000-0000-0000-000000000000'.");
        }

        return literal.Text;
    }

    private static PpdsException RefuseValue(DataFilterLiteral literal, string kind, string instead)
        => DataFilter.Refuse(literal.Line, literal.Column, kind, instead);

    private static PpdsException RefuseUse(DataFilterColumn column, string kind, string instead)
        => DataFilter.Refuse(column.Line, column.Column, kind, instead);
}
