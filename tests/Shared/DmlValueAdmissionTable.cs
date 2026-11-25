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
        List<OptionValueDto>? options = null, List<string>? targets = null) => new()
    {
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
        new("floating point", "address1_latitude", "float", A("address1_latitude", "Double")),
        new("date and time", "followupby", "datetimeoffset(0)", A("followupby", "DateTime", "UserLocal", "DateAndTime")),
        new("date only", "birthday", "date", A("birthday", "DateTime", "DateOnly", "DateOnly")),
        new("yes/no", "donotemail", "bit", A("donotemail", "Boolean")),
        new("choice", "customertypecode", "int", A("customertypecode", "Picklist",
            options: [new OptionValueDto { Value = 1, Label = "o1" }, new OptionValueDto { Value = 2, Label = "o2" }])),
        new("lookup", "parentaccountid", "uniqueidentifier", A("parentaccountid", "Lookup", targets: ["account"])),
    ];

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
        N("0.3333333333", U, "=0.3333333333", S, "=0.3333333333", U, U),
        N("0.33333333333", U, S, S, "=0.33333333333", U, U),
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
        T("N'\u200Ba'", "=\u200Ba"),
        T("N'a\uFEFF'", "=a\uFEFF"),
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
        // \u2500\u2500 NULL \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500
        All("NULL", "=null"),
    ];

    /// <summary>Every cell: the literal, the target and the expected cell.</summary>
    public static IEnumerable<(string Literal, Target Target, string Cell)> Cells()
        => Rows.SelectMany(row => row.Cells.Select((cell, i) => (row.Literal, Targets[i], cell)));

    /// <summary>The statement a cell is checked with.</summary>
    public static string Statement(string literal, Target target) => $"UPDATE account SET {target.Column} = {literal} WHERE name = 'n1'";
}
