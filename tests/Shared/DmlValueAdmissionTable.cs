using System;
using System.Collections.Generic;
using System.Linq;
using PPDS.Dataverse.Metadata.Models;

namespace PPDS.Tests.Shared;

/// <summary>
/// The table of literal forms crossed with target kinds that pins the admission of DML values (#1698, review round 2).
/// Each cell is "admitted, and exactly this value is sent" or "refused with this code". The same table is checked twice:
/// end to end through the service in PPDS.Cli.Tests (the decision, the code and the value sent), and against the
/// reference SQL Server in PPDS.Query.DifferentialTests (every admitted cell is the value SQL Server stores for the same
/// literal assigned to a column of the reference type).
/// </summary>
/// <remarks>
/// A cell is <c>U</c> (refused, <c>Query.DmlUnsupportedConstruct</c>), <c>S</c> (refused,
/// <c>Query.DmlValueStoredDifferently</c>) or <c>=value</c> (admitted; the value as text: numbers in plain digits, dates and
/// times in UTC as <c>yyyy-MM-ddTHH:mm:ssZ</c>, dates as <c>yyyy-MM-dd</c>, yes/no as 1 or 0, row ids in lower case,
/// <c>null</c> for NULL). Every literal is written in <c>UPDATE account SET &lt;column&gt; = &lt;literal&gt; WHERE name = 'n1'</c>.
/// </remarks>
public static class DmlValueAdmissionTable
{
    /// <summary>A target kind: the account column the tests write, its metadata, and the reference type on SQL Server.</summary>
    public sealed record Target(string Kind, string Column, string ReferenceType, AttributeMetadataDto Metadata);

    private static AttributeMetadataDto A(string name, string type, string? behavior = null, string? format = null,
        List<OptionValueDto>? options = null, List<string>? targets = null, int? precision = null) => new()
    {
        Precision = precision,
        // A currency column keeps its own precision (source 0, the column); any other source is refused for places (P1).
        PrecisionSource = type == "Money" ? 0 : null,
        LogicalName = name, DisplayName = name, SchemaName = name, AttributeType = type,
        IsValidForCreate = true, IsValidForUpdate = true, IsValidForRead = true,
        DateTimeBehavior = behavior, Format = format, Options = options, Targets = targets
    };

    /// <summary>The target kinds, in the order of the cells of a row. Their metadata matches the account table of the Cli tests.</summary>
    public static readonly Target[] Targets =
    [
        new("text", "name", "nvarchar(4000)", A("name", "String")),
        new("whole number", "numberofemployees", "int", A("numberofemployees", "Integer")),
        new("decimal", "revenue", "decimal(38, 10)", A("revenue", "Decimal")),
        new("currency", "creditlimit", "money", A("creditlimit", "Money")),
        // Precision 5, as account.address1_latitude has in Dataverse (live line E49 wrote to it).
        new("floating point", "address1_latitude", "float", A("address1_latitude", "Double", precision: 5)),
        new("date and time", "followupby", "datetimeoffset(0)", A("followupby", "DateTime", "UserLocal", "DateAndTime")),
        new("date only", "birthday", "date", A("birthday", "DateTime", "DateOnly", "DateOnly")),
        new("yes/no", "donotemail", "bit", A("donotemail", "Boolean")),
        new("choice", "customertypecode", "int", A("customertypecode", "Picklist",
            options: [new OptionValueDto { Value = 1, Label = "o1" }, new OptionValueDto { Value = 2, Label = "o2" }])),
        new("lookup", "parentaccountid", "uniqueidentifier", A("parentaccountid", "Lookup", targets: ["account"])),
    ];

    /// <summary>
    /// The targets of <see cref="CompareCases"/>: <see cref="Targets"/>, and a currency column that keeps 2 places (fewer than
    /// the 4 a currency amount can have), whose SQL Server counterpart is <c>decimal(19, 2)</c>.
    /// </summary>
    public static readonly Target[] CompareTargets =
    [
        .. Targets,
        new("currency, 2 places", "ppds_amount2", "decimal(19, 2)", A("ppds_amount2", "Money", precision: 2)),
        new("row id", "accountid", "uniqueidentifier", A("accountid", "Uniqueidentifier")),
    ];

    /// <summary>The target of a compare case's kind.</summary>
    public static Target CompareTarget(string kind) => CompareTargets.Single(t => t.Kind == kind);

    /// <summary>A literal form and its ten cells, in the order of <see cref="Targets"/>.</summary>
    public sealed record Row(string Literal, string[] Cells);

    private const string U = "U";
    private const string S = "S";

    /// <summary>A number literal: refused for text, date, date-only and lookup columns.</summary>
    private static Row N(string literal, string whole, string dec, string money, string flt, string yesNo, string choice)
        => new(literal, [U, whole, dec, money, flt, U, U, yesNo, choice, U]);

    /// <summary>A text literal: written to a text column as itself; refused for number, yes/no and choice columns.</summary>
    private static Row T(string literal, string text, string dateTime = U, string date = U, string id = U)
        => new(literal, [text, U, U, U, U, dateTime, date, U, U, id]);

    private static Row All(string literal, string cell) => new(literal, [cell, cell, cell, cell, cell, cell, cell, cell, cell, cell]);

    private const string Guid = "6F9619FF-8B86-D011-B42D-00C04FC964FF";
    private const string GuidLower = "=6f9619ff-8b86-d011-b42d-00c04fc964ff";

    /// <summary>The rows: number forms at each type's boundaries, text with each edge character, date and GUID spellings.</summary>
    public static readonly Row[] Rows =
    [
        // \u2500\u2500 whole numbers \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        N("0", "=0", "=0", "=0", "=0", "=0", U),
        N("1", "=1", "=1", "=1", "=1", "=1", "=1"),
        N("2", "=2", "=2", "=2", "=2", U, "=2"),
        N("7", "=7", "=7", "=7", "=7", U, U),
        N("007", "=7", "=7", "=7", "=7", U, U),
        N("-0", "=0", "=0", "=0", "=0", U, U),
        N("-5", "=-5", "=-5", "=-5", "=-5", U, U),
        N("2147483647", "=2147483647", "=2147483647", "=2147483647", "=2147483647", U, U),
        N("-2147483648", "=-2147483648", "=-2147483648", "=-2147483648", "=-2147483648", U, U),
        N("2147483648", U, "=2147483648", "=2147483648", "=2147483648", U, U),
        N("-2147483649", U, "=-2147483649", "=-2147483649", "=-2147483649", U, U),
        N("922337203685477", U, "=922337203685477", "=922337203685477", "=922337203685477", U, U),
        N("922337203685478", U, "=922337203685478", U, "=922337203685478", U, U),
        N("1234567890123456", U, "=1234567890123456", U, "=1234567890123456", U, U),
        N("9007199254740993", U, "=9007199254740993", U, S, U, U),
        N("9999999999999999999999999999", U, "=9999999999999999999999999999", U, S, U, U),
        N("79228162514264337593543950335", U, U, U, S, U, U),
        N("79228162514264337593543950336", U, U, U, U, U, U),
        N("100000000000000000000000000000", U, U, U, U, U, U),
        // \u2500\u2500 decimal numbers \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        N("2.5", U, "=2.5", "=2.5", "=2.5", U, U),
        N("-2.5", U, "=-2.5", "=-2.5", "=-2.5", U, U),
        N("2.70", U, "=2.7", "=2.7", "=2.7", U, U),
        N("0.0", U, "=0", "=0", "=0", U, U),
        N("-0.0", U, "=0", "=0", "=0", U, U),
        N("1.0", U, "=1", "=1", "=1", U, U),
        N("1.23", U, "=1.23", "=1.23", "=1.23", U, U),
        N("1.2345", U, "=1.2345", "=1.2345", "=1.2345", U, U),
        N("1.23495", U, "=1.23495", S, "=1.23495", U, U),
        N("0.1", U, "=0.1", "=0.1", "=0.1", U, U),
        N("0.3333333333", U, "=0.3333333333", S, S, U, U),
        N("0.33333333333", U, S, S, S, U, U),
        // The floating-point column keeps 5 places (live line E49: 47.123456 was stored as 47.12346); a trailing zero is not a place.
        N("47.12345", U, "=47.12345", S, "=47.12345", U, U),
        N("47.123456", U, "=47.123456", S, S, U, U),
        N("47.1234560", U, "=47.1234560", S, S, U, U),
        N("47.123450", U, "=47.123450", S, "=47.12345", U, U),
        // .NET's decimal-to-double conversion does not give the nearest double here (1.0000000000000001E-28), so the round
        // trip refuses it rather than send another number.
        N("0.0000000000000000000000000001", U, S, S, S, U, U),
        N("0.000000000000000000000000000015", U, U, U, U, U, U),
        N("123456789012.3456789012", U, "=123456789012.3456789012", S, S, U, U),
        N("922337203685477.5807", U, "=922337203685477.5807", "=922337203685477.5807", S, U, U),
        N("-922337203685477.5807", U, "=-922337203685477.5807", "=-922337203685477.5807", S, U, U),
        N("922337203685477.5808", U, "=922337203685477.5808", U, S, U, U),
        N("123456789012345.6", U, "=123456789012345.6", "=123456789012345.6", "=123456789012345.6", U, U),
        N("1234567890.12345678", U, "=1234567890.12345678", S, S, U, U),
        // Negative zero: sent as +0 (the floating-point cell is checked bit for bit), as SQL Server stores it.
        N("-0.000", U, "=0", "=0", "=0", U, U),
        // 38 digits in the literal's text, leading zeros and zeros at the end of the fraction counted, is the most SQL Server reads
        // (error 1007 above): admitted where the kind's other rules admit it; 39 is refused for every kind.
        N("00000000000000000000000000000000000015", "=15", "=15", "=15", "=15", U, U),
        N("000000000000000000000000000000000000015", U, U, U, U, U, U),
        N("00000000000000000000000000000000000001", "=1", "=1", "=1", "=1", U, "=1"),
        N("000000000000000000000000000000000000001", U, U, U, U, U, U),
        N("15.000000000000000000000000000000000000", U, "=15", "=15", "=15", U, U),
        N("15.0000000000000000000000000000000000000", U, U, U, U, U, U),
        N("-0554.0000000000000000000000000000000000", U, "=-554", "=-554", "=-554", U, U),
        N("-0554.00000000000000000000000000000000000", U, U, U, U, U, U),
        N("0.0000000000000000000000000000000000000", U, "=0", "=0", "=0", U, U),
        N("0.00000000000000000000000000000000000000", U, U, U, U, U, U),
        // \u2500\u2500 other number spellings \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        All("2.5E0", U),
        All("$5", U),
        All("0x0A", U),
        All("+5", U),
        All("-(5)", U),
        All("- -5", U),
        // \u2500\u2500 text \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        T("'x'", "=x"),
        T("N'x'", "=x"),
        T("'a b'", "=a b"),
        T("'5'", "=5"),
        T("'1'", "=1"),
        T("N'caf\u00E9'", "=caf\u00E9"),
        T("N'\u041F\u0440\u0438\u0432\u0435\u0442'", "=\u041F\u0440\u0438\u0432\u0435\u0442"),
        // Inside the text, invisible characters are written as typed; at either end they are refused as white space is (S12).
        T("N'a\u200Bb'", "=a\u200Bb"),
        T("N'a\uFEFFb'", "=a\uFEFFb"),
        T("N'a\u180Eb'", "=a\u180Eb"),
        All("N'\u200Ba'", S),
        All("N'a\uFEFF'", S),
        All("N'\u180Ea'", S),
        All("N'a\u200B'", S),
        All("'caf\u00E9'", U),
        All("''", S),
        All("N''", S),
        All("' '", S),
        All("'   '", S),
        All("' a'", S),
        All("'a '", S),
        All("'\ta'", S),
        All("'a\n'", S),
        All("N'a\u00A0'", S),
        All("N'\u3000a'", S),
        All("N'\u2028a'", S),
        All("N'a\u0085'", S),
        // A backslash directly before a line break is SQL Server's line continuation: SQL Server removes both (before a
        // carriage return alone, the backslash only), so the text as typed is not the text SQL Server reads (MF1).
        All("N'abc\\\ndef'", U),
        All("N'abc\\\r\ndef'", U),
        All("N'abc\\\rdef'", U),
        All("N'abc\\\\\ndef'", U),
        All("'abc\\\ndef'", U),
        All("N'abc\\\n\\\ndef'", U),
        // A backslash before anything else, a space or a tab before the line break included, is read as typed.
        T("N'abc\\ \ndef'", "=abc\\ \ndef"),
        T("N'abc\\\t\ndef'", "=abc\\\t\ndef"),
        T("N'a\\b'", "=a\\b"),
        T("N'abc\\\u2028def'", "=abc\\\u2028def"),
        T("N'abc\ndef'", "=abc\ndef"),
        T("N'abc\r\ndef'", "=abc\r\ndef"),
        // Half of a surrogate pair is not a character (MF3); a whole pair is.
        All("N'a<U+D800>b'", U),
        All("N'a<U+DC00>b'", U),
        All("N'ab<U+D83D>'", U),
        T("N'a\uD83D\uDE00b'", "=a\uD83D\uDE00b"),
        // \u2500\u2500 dates and times \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        T("'2024-01-15T09:30:00Z'", "=2024-01-15T09:30:00Z", "=2024-01-15T09:30:00Z"),
        T("N'2024-01-15T09:30:00Z'", "=2024-01-15T09:30:00Z", "=2024-01-15T09:30:00Z"),
        T("'2024-01-15T09:30:00+01:00'", "=2024-01-15T09:30:00+01:00", "=2024-01-15T08:30:00Z"),
        T("'2024-01-15T09:30:00-05:30'", "=2024-01-15T09:30:00-05:30", "=2024-01-15T15:00:00Z"),
        T("'2024-01-15T09:30:00+14:00'", "=2024-01-15T09:30:00+14:00", "=2024-01-14T19:30:00Z"),
        T("'2024-01-15T09:30:00-14:00'", "=2024-01-15T09:30:00-14:00", "=2024-01-15T23:30:00Z"),
        T("'2024-02-29T12:00:00Z'", "=2024-02-29T12:00:00Z", "=2024-02-29T12:00:00Z"),
        T("'1753-01-01T00:00:00Z'", "=1753-01-01T00:00:00Z", "=1753-01-01T00:00:00Z"),
        T("'9999-12-31T23:59:59Z'", "=9999-12-31T23:59:59Z", "=9999-12-31T23:59:59Z"),
        T("'2024-01-15T09:30:00+14:01'", "=2024-01-15T09:30:00+14:01"),
        T("'2024-01-15T09:30:00+15:00'", "=2024-01-15T09:30:00+15:00"),
        T("'2024-01-15T09:30:00+01:60'", "=2024-01-15T09:30:00+01:60"),
        T("'2024-01-15T09:30:00.123Z'", "=2024-01-15T09:30:00.123Z"),
        T("'2024-01-15T09:30:00.1234567Z'", "=2024-01-15T09:30:00.1234567Z"),
        T("'2024-01-15T09:30Z'", "=2024-01-15T09:30Z"),
        T("'2024-01-15 09:30Z'", "=2024-01-15 09:30Z"),
        T("'2024-01-15 09:30:00Z'", "=2024-01-15 09:30:00Z"),
        T("'2024-01-15T09:30:00'", "=2024-01-15T09:30:00"),
        T("'01/02/2024 10:00Z'", "=01/02/2024 10:00Z"),
        T("'Jan 15 2024 9:30AM Z'", "=Jan 15 2024 9:30AM Z"),
        T("'2024-01-15t09:30:00z'", "=2024-01-15t09:30:00z"),
        // The letters are upper case only, each on its own: SQL Server refuses a lower-case z (error 241).
        T("'2024-01-15T09:30:00z'", "=2024-01-15T09:30:00z"),
        T("'2024-01-15t09:30:00Z'", "=2024-01-15t09:30:00Z"),
        T("'2024-01-15T09:30:00+0100'", "=2024-01-15T09:30:00+0100"),
        T("'2024-02-30T00:00:00Z'", "=2024-02-30T00:00:00Z"),
        T("'2024-01-15T24:00:00Z'", "=2024-01-15T24:00:00Z"),
        T("'2024-1-15T09:30:00Z'", "=2024-1-15T09:30:00Z"),
        T("'1752-12-31T23:59:59Z'", "=1752-12-31T23:59:59Z"),
        T("'1753-01-01T00:30:00+01:00'", "=1753-01-01T00:30:00+01:00"),
        T("'9999-12-31T23:59:59-01:00'", "=9999-12-31T23:59:59-01:00"),
        All("'2024-01-15T09:30:00Z '", S),
        T("'2024-01-15'", "=2024-01-15", U, "=2024-01-15"),
        T("'2024-02-29'", "=2024-02-29", U, "=2024-02-29"),
        T("'1753-01-01'", "=1753-01-01", U, "=1753-01-01"),
        T("'9999-12-31'", "=9999-12-31", U, "=9999-12-31"),
        T("'2023-02-29'", "=2023-02-29"),
        T("'1752-12-31'", "=1752-12-31"),
        T("'20240115'", "=20240115"),
        T("'2024-01-15Z'", "=2024-01-15Z"),
        T("'2024/01/15'", "=2024/01/15"),
        // \u2500\u2500 row ids \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        T("'" + Guid + "'", "=" + Guid, id: GuidLower),
        T("'{" + Guid + "}'", "={" + Guid + "}", id: GuidLower),
        T("'6f9619ff-8b86-d011-b42d-00c04fc964ff'", "=6f9619ff-8b86-d011-b42d-00c04fc964ff", id: GuidLower),
        T("N'" + Guid + "'", "=" + Guid, id: GuidLower),
        T("'6F9619FF8B86D011B42D00C04FC964FF'", "=6F9619FF8B86D011B42D00C04FC964FF"),
        T("'(" + Guid + ")'", "=(" + Guid + ")"),
        All("'" + Guid + " '", S),
        new("CAST('" + Guid + "' AS uniqueidentifier)", [U, U, U, U, U, U, U, U, U, GuidLower]),
        All("CAST('6F9619FF8B86D011B42D00C04FC964FF' AS uniqueidentifier)", U),
        // Characters XML 1.0 does not allow, which Dataverse refuses in written text (live line E73: U+0000, U+0001, U+FFFE were
        // refused as invalid characters and nothing was written): refused before anything is read.
        All("N'a<U+0000>b'", U),
        All("N'a<U+0001>b'", U),
        All("'a<U+001F>b'", U),
        All("N'a<U+000B>b'", U),
        All("N'a<U+FFFE>b'", U),
        // Damaged ids that .NET's GUID parser reads as other ids, also in its exact "D" and "B" modes: a '+' or '0x' inside a
        // block, a leading '0X' ('+ccccccc-...' is read as 0ccccccc-..., a block '0x12' as 0012). Refused for a lookup by the
        // character rule, never by the parser's verdict.
        T("'+CCCCCCC-0000-0000-0000-000000000001'", "=+CCCCCCC-0000-0000-0000-000000000001"),
        T("'00000000-+000-0000-0000-000000000001'", "=00000000-+000-0000-0000-000000000001"),
        T("'0x000000-0000-0000-0000-000000000001'", "=0x000000-0000-0000-0000-000000000001"),
        T("'00000000-0x12-0000-0000-000000000001'", "=00000000-0x12-0000-0000-000000000001"),
        T("'0X000000-0000-0000-0000-000000000001'", "=0X000000-0000-0000-0000-000000000001"),
        T("'00000000-0000-0000-0000-+00000000001'", "=00000000-0000-0000-0000-+00000000001"),
        T("'00000000-0000-0000-0000-0x0000000001'", "=00000000-0000-0000-0000-0x0000000001"),
        T("'{+CCCCCCC-0000-0000-0000-000000000001}'", "={+CCCCCCC-0000-0000-0000-000000000001}"),
        All("CAST('+CCCCCCC-0000-0000-0000-000000000001' AS uniqueidentifier)", U),
        All("CAST('00000000-0x12-0000-0000-000000000001' AS uniqueidentifier)", U),
        // \u2500\u2500 NULL \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        All("NULL", "=null"),
    ];

    /// <summary>Every cell: the literal, the target and the expected cell.</summary>
    public static IEnumerable<(string Literal, Target Target, string Cell)> Cells()
        => Rows.SelectMany(row => row.Cells.Select((cell, i) => (row.Literal, Targets[i], cell)));

    /// <summary>The statement a cell is checked with.</summary>
    public static string Statement(string literal, Target target) => $"UPDATE account SET {target.Column} = {Sql(literal)} WHERE name = 'n1'";

    /// <summary>
    /// The SQL text of a literal of the table: <c>&lt;U+XXXX&gt;</c> stands for that UTF-16 code unit. Half of a surrogate
    /// pair, and U+FFFE, are written so because the test runner's serialisation of theory data replaces them.
    /// </summary>
    public static string Sql(string literal) => System.Text.RegularExpressions.Regex.Replace(literal, "<U\\+([0-9A-F]{4})>",
        m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>
    /// A literal compared with a column in a write's WHERE (<see cref="CompareStatement"/>): <c>U</c> (refused,
    /// <c>Query.DmlUnsupportedConstruct</c>, nothing read), or <c>=text</c>, the value the FetchXML condition carries: its
    /// <c>value</c> attribute, or for <c>IN</c> the text of its one <c>value</c> element. <see cref="Operator"/> is <c>=</c>,
    /// <c>&lt;&gt;</c>, <c>&lt;</c>, <c>LIKE</c>, <c>IN</c> (the literal is the list's one value) or <c>BETWEEN</c> (the
    /// literal is the lower bound; only refused cases). The kind names one of <see cref="CompareTargets"/>.
    /// </summary>
    public sealed record CompareCase(string Kind, string Literal, string Cell, string Operator = "=");

    /// <summary>
    /// Compared literals: a date and time is sent as its UTC instant in one spelling (yyyy-MM-ddTHH:mm:ssZ), whatever offset it
    /// was written with; text with a control character a FetchXML condition cannot carry is refused, with and without N;
    /// numbers of 38 and 39 digits; the case of the date's letters.
    /// </summary>
    public static readonly CompareCase[] CompareCases =
    [
        new("date and time", "'2024-01-15T09:30:00Z'", "=2024-01-15T09:30:00Z"),
        new("date and time", "N'2024-01-15T09:30:00Z'", "=2024-01-15T09:30:00Z"),
        new("date and time", "'2024-01-15T09:30:00+05:00'", "=2024-01-15T04:30:00Z"),
        new("date and time", "'2024-01-15T09:30:00-05:30'", "=2024-01-15T15:00:00Z"),
        new("date and time", "'2024-01-15T09:30:00+14:00'", "=2024-01-14T19:30:00Z"),
        new("date and time", "'2024-01-15T09:30:00-14:00'", "=2024-01-15T23:30:00Z"),
        new("date and time", "N'2024-01-15T00:30:00+01:00'", "=2024-01-14T23:30:00Z"),
        new("date and time", "'2024-01-15T09:30:00+00:00'", "=2024-01-15T09:30:00Z"),
        new("date and time", "'2024-01-15T09:30:00-00:00'", "=2024-01-15T09:30:00Z"),
        new("date and time", "'2024-01-15T09:30:00z'", "U"),
        new("date and time", "'2024-01-15t09:30:00Z'", "U"),
        new("date and time", "'2024-01-15T09:30:00'", "U"),
        new("date and time", "'2024-01-15T09:30:00.123Z'", "U"),
        new("date only", "'2024-01-15'", "=2024-01-15"),
        new("text", "'a'", "=a"),
        new("text", "N'a'", "=a"),
        new("text", "'a\tb'", "=a\tb"),
        new("text", "'a\u0000b'", "U"),
        new("text", "'a\u0001b'", "U"),
        new("text", "N'a\u0001b'", "U"),
        new("text", "'a\u0008'", "U"),
        new("text", "'a\u000Bb'", "U"),
        new("text", "'a\u000Cb'", "U"),
        new("text", "N'\u000Ea'", "U"),
        new("text", "'a\u001Fb'", "U"),
        new("text", "N'a\u001Fb'", "U"),
        new("text", "'a\u007Fb'", "=a\u007Fb"),
        new("whole number", "00000000000000000000000000000000000015", "=00000000000000000000000000000000000015"),
        new("whole number", "000000000000000000000000000000000000015", "U"),
        new("decimal", "15.000000000000000000000000000000000000", "=15.000000000000000000000000000000000000"),
        new("decimal", "15.0000000000000000000000000000000000000", "U"),
        new("decimal", "00000000000000000000000000000000000015", "=00000000000000000000000000000000000015"),
        new("decimal", "000000000000000000000000000000000000015", "U"),
        new("currency", "-0554.0000000000000000000000000000000000", "=-0554.0000000000000000000000000000000000"),
        new("currency", "-0554.00000000000000000000000000000000000", "U"),
        new("floating point", "15.000000000000000000000000000000000000", "=15.000000000000000000000000000000000000"),
        new("floating point", "15.0000000000000000000000000000000000000", "U"),
        new("choice", "00000000000000000000000000000000000001", "=00000000000000000000000000000000000001"),
        new("choice", "000000000000000000000000000000000000001", "U"),
        // Line breaks in an attribute value are sent as character references, so each is compared as itself (MF5).
        new("text", "N'a\nb'", "=a\nb"),
        new("text", "N'a\rb'", "=a\rb"),
        new("text", "N'a\r\nb'", "=a\r\nb"),
        new("text", "'a\nb'", "=a\nb", "LIKE"),
        new("text", "N'a\rb'", "=a\rb", "<>"),
        // SQL Server's line continuation (MF1): refused in every operator, with and without N.
        new("text", "N'abc\\\ndef'", "U"),
        new("text", "'abc\\\r\ndef'", "U"),
        new("text", "N'abc\\\rdef'", "U"),
        new("text", "N'abc\\\\\ndef'", "U"),
        new("text", "N'abc\\\ndef'", "U", "IN"),
        new("text", "N'abc\\\n%'", "U", "LIKE"),
        // At the very end and at the very start of the text (MF-C).
        new("text", "N'abc\\\n'", "U"),
        new("text", "'abc\\\r\n'", "U", "LIKE"),
        new("text", "N'\\\nabc'", "U"),
        new("text", "N'\\\rabc'", "U", "<>"),
        new("text", "N'abc\\ \ndef'", "=abc\\ \ndef"),
        // Not a sequence of XML 1.0 characters (MF3).
        // (U+FFFF is refused by the parser already, Query.ParseError: DmlReviewRound6FixTests.)
        new("text", "N'a<U+D800>b'", "U"),
        new("text", "N'a<U+DC00>b'", "U"),
        new("text", "N'<U+D83D>%'", "U", "LIKE"),
        new("text", "N'a<U+DE00><U+D83D>b'", "U", "IN"),
        new("text", "N'a<U+FFFE>b'", "U"),
        new("text", "N'a<U+FFFE>b'", "U", "IN"),
        new("text", "N'a\uD83D\uDE00b'", "=a\uD83D\uDE00b"),
        new("text", "N'a\uFFFDb'", "=a\uFFFDb"),
        // An IN value is element text: whether Dataverse reads a line break there unchanged is not shown, and the executor's
        // parse empties an element that is only white space (MF2, MF-A). With and without N (MF-C).
        new("text", "N'line1\r\nline2'", "U", "IN"),
        new("text", "N'c\rd'", "U", "IN"),
        new("text", "N'c\nd'", "U", "IN"),
        new("text", "N'cd\n'", "U", "IN"),
        new("text", "'c\rd'", "U", "IN"),
        new("text", "'c\nd'", "U", "IN"),
        new("text", "''", "U", "IN"),
        new("text", "' '", "U", "IN"),
        new("text", "'\t'", "U", "IN"),
        new("text", "'a'", "=a", "IN"),
        new("text", "N'\t'", "U", "IN"),
        new("text", "N' '", "U", "IN"),
        new("text", "N''", "U", "IN"),
        new("text", "N'\u00A0'", "U", "IN"),
        new("text", "N'a'", "=a", "IN"),
        new("text", "N' a '", "= a ", "IN"),
        new("text", "N'a\tb'", "=a\tb", "IN"),
        new("text", "N'a&<>\"''b'", "=a&<>\"'b", "IN"),
        new("whole number", "-5", "=-5", "IN"),
        // Damaged ids the GUID parser reads as other ids (a '+' or '0x' in a block, a leading '0X'), compared with a lookup and
        // with the row id: refused by the character rule before anything is read.
        new("lookup", "'+CCCCCCC-0000-0000-0000-000000000001'", "U"),
        new("lookup", "'00000000-+000-0000-0000-000000000001'", "U", "IN"),
        new("lookup", "'0x000000-0000-0000-0000-000000000001'", "U"),
        new("lookup", "'00000000-0x12-0000-0000-000000000001'", "U", "IN"),
        new("lookup", "'0X000000-0000-0000-0000-000000000001'", "U"),
        new("lookup", "'00000000-0000-0000-0000-+00000000001'", "U"),
        new("lookup", "'00000000-0000-0000-0000-0x0000000001'", "U", "<>"),
        new("row id", "'+CCCCCCC-0000-0000-0000-000000000001'", "U"),
        new("row id", "'00000000-+000-0000-0000-000000000001'", "U", "IN"),
        new("row id", "'0x000000-0000-0000-0000-000000000001'", "U"),
        new("row id", "'00000000-0x12-0000-0000-000000000001'", "U", "IN"),
        new("row id", "'0X000000-0000-0000-0000-000000000001'", "U"),
        new("row id", "'00000000-0000-0000-0000-+00000000001'", "U"),
        new("row id", "'00000000-0000-0000-0000-0x0000000001'", "U", "<>"),
        new("lookup", "'{+CCCCCCC-0000-0000-0000-000000000001}'", "U"),
        new("row id", "N'{+CCCCCCC-0000-0000-0000-000000000001}'", "U", "IN"),
        new("lookup", "'6F9619FF-8B86-D011-B42D-00C04FC964FF'", "=6F9619FF-8B86-D011-B42D-00C04FC964FF"),
        new("row id", "'6F9619FF-8B86-D011-B42D-00C04FC964FF'", "=6F9619FF-8B86-D011-B42D-00C04FC964FF"),
        // The bounds of a written number hold for a compared one (MF4): places the column keeps, digits a double holds.
        new("currency", "1.2345", "=1.2345"),
        new("currency", "1.23450", "=1.23450"),
        new("currency", "1.23456", "U"),
        new("currency", "1.23456", "U", "IN"),
        new("currency", "1.23456", "U", "<"),
        // A currency column that keeps 2 places (MF-C): a third place is refused, in =, IN and BETWEEN.
        new("currency, 2 places", "1.01", "=1.01"),
        new("currency, 2 places", "1.010", "=1.010"),
        new("currency, 2 places", "1.005", "U"),
        new("currency, 2 places", "1.005", "U", "IN"),
        new("currency, 2 places", "1.005", "U", "BETWEEN"),
        new("currency, 2 places", "1.01", "=1.01", "IN"),
        new("decimal", "0.0000000001", "=0.0000000001"),
        new("decimal", "0.00000000001", "U"),
        new("decimal", "1.23456789012", "U", "IN"),
        new("floating point", "0.1", "=0.1"),
        new("floating point", "1.23495", "=1.23495"),
        // The column keeps 5 places: a sixth can never equal a stored value (live line E49).
        new("floating point", "47.12345", "=47.12345"),
        new("floating point", "47.123450", "=47.123450"),
        new("floating point", "47.123456", "U"),
        new("floating point", "47.123456", "U", "IN"),
        new("floating point", "47.123456", "U", "<"),
        new("floating point", "0.1234567890123456789", "U"),
        new("floating point", "9007199254740993", "U"),
        new("floating point", "0.1234567890123456789", "U", "<"),
    ];

    /// <summary>The statement a compare case is checked with.</summary>
    public static string CompareStatement(string literal, Target target) => CompareStatement(literal, target, "=");

    /// <summary>The statement a compare case with <paramref name="op"/> is checked with.</summary>
    public static string CompareStatement(string literal, Target target, string op) => op switch
    {
        "IN" => $"UPDATE account SET description = 'x' WHERE {target.Column} IN ({Sql(literal)})",
        // The literal is the lower bound; the upper bound is a value every target of the kind admits.
        "BETWEEN" => $"UPDATE account SET description = 'x' WHERE {target.Column} BETWEEN {Sql(literal)} AND 2",
        _ => $"UPDATE account SET description = 'x' WHERE {target.Column} {op} {Sql(literal)}"
    };
}
