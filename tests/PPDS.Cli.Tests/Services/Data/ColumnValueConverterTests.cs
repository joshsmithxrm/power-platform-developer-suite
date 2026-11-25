using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Services.Data;
using Xunit;
using static PPDS.Cli.Tests.Services.Data.ColumnValueTestSupport;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// The per-column conversion of #1916 and #1917: each case is converted under the invariant culture, de-DE and fr-FR,
/// and must give the same, stated result in all three.
/// </summary>
[Trait("Category", "Unit")]
public class ColumnValueConverterTests
{
    private const string Refused = "refused";
    private const string AccountId = "8b0f6c2e-3c1d-4a5b-9e7f-0123456789ab";

    private static readonly Dictionary<string, Func<AttributeMetadata>> Columns = new()
    {
        ["text"] = () => Text("name"),
        ["int"] = () => WholeNumber("count"),
        ["bigint"] = () => BigInt("big"),
        ["decimal2"] = () => Decimal("rate", 2),
        ["decimal?"] = () => Decimal("rate", null),
        ["double5"] = () => FloatingPoint("ratio", 5),
        ["money2"] = () => Currency("amount", 2),
        ["money?"] = () => Currency("amount", null),
        ["money-pricing"] = () => Currency("amount", 2, precisionSource: 1),
        ["money-currency"] = () => Currency("amount", 2, precisionSource: 2),
        ["money-source?"] = () => Currency("amount", 2, precisionSource: null),
        ["choice-no-options"] = () => new PicklistAttributeMetadata { LogicalName = "kind" },
        ["bool"] = () => YesNo("flag"),
        ["guid"] = () => Identifier("uid"),
        ["choice"] = () => Choice("kind", 1, 2),
        ["lookup"] = () => Lookup("parentaccountid", "account"),
        ["customer"] = () => Lookup("customerid", "account", "contact"),
        ["userlocal"] = () => Date("when", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime),
        ["userlocal-date"] = () => Date("when", DateTimeBehavior.UserLocal, DateTimeFormat.DateOnly),
        ["dateonly"] = () => Date("day", DateTimeBehavior.DateOnly, DateTimeFormat.DateOnly),
        ["tzi"] = () => Date("clock", DateTimeBehavior.TimeZoneIndependent, DateTimeFormat.DateAndTime),
        ["tzi-date"] = () => Date("clockday", DateTimeBehavior.TimeZoneIndependent, DateTimeFormat.DateOnly),
        ["date?"] = () => Date("when", null, DateTimeFormat.DateAndTime),
        ["multichoice"] = () => MultiChoice("kinds"),
    };

    [Theory]
    // The cells of #1916 and the value of #1917, for decimal, floating point and currency.
    [InlineData("decimal2", "1,5", Refused)]
    [InlineData("decimal2", "2,75", Refused)]
    [InlineData("decimal2", "1.5", "decimal:1.5")]
    [InlineData("decimal2", "1,234.5", Refused)]
    [InlineData("decimal2", "1.234,5", Refused)]
    [InlineData("double5", "1,5", Refused)]
    [InlineData("double5", "1.5", "double:1.5")]
    [InlineData("double5", "1,234.5", Refused)]
    [InlineData("double5", "1.234,5", Refused)]
    [InlineData("money2", "1,5", Refused)]
    [InlineData("money2", "1.5", "money:1.5")]
    [InlineData("money2", "1,234.5", Refused)]
    [InlineData("money2", "1.234,5", Refused)]
    // The exact number form.
    [InlineData("decimal2", "-12.25", "decimal:-12.25")]
    [InlineData("decimal2", "+3", "decimal:3")]
    [InlineData("decimal2", "1.500", "decimal:1.500")]
    [InlineData("decimal2", "1.505", Refused)]
    [InlineData("decimal2", "1e3", Refused)]
    [InlineData("decimal2", " 1.5", Refused)]
    [InlineData("decimal2", "1.5 ", Refused)]
    [InlineData("decimal2", ".5", Refused)]
    [InlineData("decimal2", "5.", Refused)]
    [InlineData("decimal2", "1 234", Refused)]
    [InlineData("decimal2", "1 234", Refused)]
    [InlineData("decimal2", "(5)", Refused)]
    [InlineData("decimal2", "١", Refused)]
    [InlineData("decimal2", "12345678901234567890123456789012", Refused)]
    [InlineData("decimal?", "7", "decimal:7")]
    [InlineData("decimal?", "7.5", Refused)]
    [InlineData("double5", "0.1", "double:0.1")]
    [InlineData("double5", "-0.0", "double:0")]
    [InlineData("double5", "9007199254740993", Refused)]
    [InlineData("double5", "1E3", Refused)]
    [InlineData("double5", "1.123456", Refused)]
    [InlineData("money2", "$1.50", Refused)]
    [InlineData("money2", "1.50€", Refused)]
    [InlineData("money2", "100.255", Refused)]
    [InlineData("money?", "1.2345", "money:1.2345")]
    [InlineData("money?", "1.23456", Refused)]
    [InlineData("int", "15", "int:15")]
    [InlineData("int", "-7", "int:-7")]
    [InlineData("int", "1.5", Refused)]
    [InlineData("int", "1,5", Refused)]
    [InlineData("int", "1.000", Refused)]
    [InlineData("int", "2147483648", Refused)]
    [InlineData("bigint", "2147483648", "long:2147483648")]
    [InlineData("bigint", "1,000", Refused)]
    // Text is never a number, and a number-looking text is text.
    [InlineData("text", "123", "text:123")]
    [InlineData("text", "1,5", "text:1,5")]
    [InlineData("text", AccountId, "text:" + AccountId)]
    // Yes/no, unique identifier, choice and lookup.
    [InlineData("bool", "true", "bool:True")]
    [InlineData("bool", "No", "bool:False")]
    [InlineData("bool", "ja", Refused)]
    [InlineData("guid", AccountId, "guid:" + AccountId)]
    [InlineData("guid", "{" + AccountId + "}", "guid:" + AccountId)]
    [InlineData("guid", "8b0f6c2e3c1d4a5b9e7f0123456789ab", Refused)]
    [InlineData("choice", "2", "option:2")]
    [InlineData("choice", "3", Refused)]
    [InlineData("choice", "Active", Refused)]
    [InlineData("choice", "1.0", Refused)]
    [InlineData("lookup", AccountId, "ref:account:" + AccountId)]
    [InlineData("lookup", "Contoso", Refused)]
    [InlineData("customer", AccountId, Refused)]
    // Dates by behaviour.
    [InlineData("userlocal", "2024-01-15T09:30:00Z", "date:2024-01-15T09:30:00:Utc")]
    [InlineData("userlocal", "2024-01-15T09:30:00+05:00", "date:2024-01-15T04:30:00:Utc")]
    [InlineData("userlocal", "2024-01-15T23:30:00-02:00", "date:2024-01-16T01:30:00:Utc")]
    [InlineData("userlocal", "2024-01-15T09:30:00", Refused)]
    [InlineData("userlocal", "2024-01-15T09:30:00+0500", Refused)]
    [InlineData("userlocal", "2024-01-15T09:30:00.123Z", Refused)]
    [InlineData("userlocal", "2024-01-15T09:30:00+01:60", Refused)] // review round 2, mutant m27: minutes past 59
    [InlineData("userlocal", "2024-01-15T09:30:00+99:00", Refused)] // and an offset past 14 hours
    [InlineData("userlocal", "2024-01-15T09:30:00+14:00", "date:2024-01-14T19:30:00:Utc")]
    [InlineData("userlocal", "2024-01-15 09:30:00Z", Refused)]
    [InlineData("userlocal", "2024-01-15", Refused)]
    [InlineData("userlocal", "15.01.2024 09:30", Refused)]
    [InlineData("userlocal", "01/15/2024 9:30 AM", Refused)]
    [InlineData("userlocal", "1752-12-31T23:59:59Z", Refused)]
    [InlineData("userlocal-date", "2024-01-15", Refused)]
    [InlineData("userlocal-date", "2024-01-15T00:00:00+01:00", "date:2024-01-14T23:00:00:Utc")]
    [InlineData("dateonly", "2024-01-15", "date:2024-01-15T00:00:00:Unspecified")]
    [InlineData("dateonly", "2024-01-15T00:00:00Z", Refused)]
    [InlineData("dateonly", "15.01.2024", Refused)]
    [InlineData("dateonly", "2024-02-30", Refused)]
    [InlineData("dateonly", "1752-12-31", Refused)]
    [InlineData("tzi", "2024-01-15T09:30:00Z", "date:2024-01-15T09:30:00:Utc")]
    [InlineData("tzi", "2024-01-15T09:30:00+05:00", Refused)]
    [InlineData("tzi-date", "2024-01-15", "date:2024-01-15T00:00:00:Unspecified")]
    [InlineData("date?", "2024-01-15T09:30:00Z", Refused)]
    // Types that cannot be written.
    [InlineData("multichoice", "1", Refused)]
    // Round 1: currency whose places come from elsewhere takes whole amounts only.
    [InlineData("money-pricing", "5", "money:5")]
    [InlineData("money-pricing", "5.00", "money:5.00")]
    [InlineData("money-pricing", "1.5", Refused)]
    [InlineData("money-currency", "1.5", Refused)]
    [InlineData("money-currency", "-12", "money:-12")]
    [InlineData("money-source?", "1.5", Refused)]
    [InlineData("money2", "1.25", "money:1.25")]
    // Round 1: more than 28 significant digits or places is refused, not rounded.
    [InlineData("decimal?", "1234567890123456789012345678", "decimal:1234567890123456789012345678")]
    [InlineData("decimal?", "12345678901234567890123456789", Refused)]
    [InlineData("decimal2", "0.10000000000000000000000000000", "decimal:0.1000000000000000000000000000")]
    [InlineData("double5", "12345678901234567890123456789", Refused)]
    [InlineData("money?", "12345678901234567890123456789", Refused)]
    // Round 1: the double check is on the text's digits: the double's shortest round-trip spelling must have exactly the
    // text's significant digits and magnitude (as 0.1 is admitted, so is 10^24, spelled 1E+24 by the double).
    [InlineData("double5", "1000000000000000000000000", "double:1E+24")]
    [InlineData("double5", "1000000000000000000000001", Refused)]
    [InlineData("double5", "123456789012345", "double:123456789012345")]
    [InlineData("double5", "0.30000", "double:0.3")]
    // Round 1: a choice column with no options in its metadata, white space around a GUID, and pinned forms.
    [InlineData("choice-no-options", "1", Refused)]
    // Ids: the spellings are tested once, in RecordIdTextTests (main's rule, #1915); these show that both column kinds
    // use it, including its refusal of the empty (all-zero) GUID.
    [InlineData("guid", "+ccccccc-0000-0000-0000-000000000001", Refused)]
    [InlineData("lookup", "8b0f6c2e-0x1d-4a5b-9e7f-0123456789ab", Refused)]
    [InlineData("guid", "8B0F6C2E-3C1D-4A5B-9E7F-0123456789AB", "guid:" + AccountId)]
    [InlineData("guid", "00000000-0000-0000-0000-000000000000", Refused)]
    [InlineData("lookup", "00000000-0000-0000-0000-000000000000", Refused)]
    [InlineData("bool", "on", Refused)]
    [InlineData("bool", "off", Refused)]
    [InlineData("bool", " true", Refused)]
    [InlineData("int", " 5", Refused)]
    [InlineData("int", "5 ", Refused)]
    [InlineData("int", "\u0665", Refused)]
    // Round 1: text is sent as given, nothing trimmed; NUL, other controls and lone surrogates are refused.
    [InlineData("text", "  padded  ", "text:  padded  ")]
    [InlineData("text", "He said \"hi\"", "text:He said \"hi\"")]
    [InlineData("text", "line1\r\nline2\tend", "text:line1\r\nline2\tend")]
    [InlineData("text", "a\u0000b", Refused)]
    [InlineData("text", "a\u0001b", Refused)]
    [InlineData("text", "\uD83D\uDE00", "text:\uD83D\uDE00")]
    public void Converts_by_the_column_type_and_the_same_in_every_culture(string column, string text, string expected)
    {
        var results = InEveryCulture(() =>
        {
            var result = ColumnValueConverter.Convert(text, Columns[column]());
            if (!result.IsAdmitted)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Refusal));
                if (text.Trim().Length > 0 && column != "bool")
                {
                    Assert.DoesNotContain(text.Trim(), result.Refusal, StringComparison.Ordinal);
                }
            }

            return result.IsAdmitted ? Describe(result.Value) : Refused;
        });

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Theory]
    [InlineData("dateonly", "dd.MM.yyyy", "15.01.2024", "date:2024-01-15T00:00:00:Unspecified")]
    [InlineData("dateonly", "dd.MM.yyyy", "2024-01-15", Refused)]
    [InlineData("dateonly", "yyyy-MM-dd HH:mm", "2024-01-15 10:00", Refused)]
    [InlineData("userlocal", "dd.MM.yyyy HH:mm zzz", "15.01.2024 09:30 +01:00", "date:2024-01-15T08:30:00:Utc")]
    [InlineData("userlocal", "MM/dd/yyyy HH:mm", "01/15/2024 09:30", Refused)]
    [InlineData("tzi", "MM/dd/yyyy HH:mm", "01/15/2024 09:30", Refused)]
    public void An_explicit_date_format_is_read_in_the_invariant_culture_and_must_still_be_exact(
        string column, string format, string text, string expected)
    {
        var options = new ColumnValueOptions { DateFormat = format };
        var results = InEveryCulture(() =>
        {
            var result = ColumnValueConverter.Convert(text, Columns[column](), options);
            return result.IsAdmitted ? Describe(result.Value) : Refused;
        });

        Assert.All(results, r => Assert.Equal(expected, r));
    }

    [Fact]
    public void A_label_of_the_mapping_file_is_matched_exactly_before_the_integer_form()
    {
        var options = new ColumnValueOptions { OptionLabels = new Dictionary<string, int> { ["Active"] = 1 } };
        Assert.Equal("option:1", Describe(ColumnValueConverter.Convert("Active", Choice("kind", 1, 2), options).Value));
        Assert.False(ColumnValueConverter.Convert("active", Choice("kind", 1, 2), options).IsAdmitted);
        Assert.False(ColumnValueConverter.Convert("Inactive", Choice("kind", 1, 2), options).IsAdmitted);
    }

    [Fact]
    public void A_lone_surrogate_in_text_is_refused()
    {
        // Built here: an attribute argument cannot hold a lone surrogate.
        foreach (var text in new[] { "a" + (char)0xD800 + "b", "a" + (char)0xDC00, ((char)0xDBFF).ToString(), "a\uFFFE" })
        {
            Assert.All(InEveryCulture(() => ColumnValueConverter.Convert(text, Text("name")).IsAdmitted), Assert.False);
        }
    }

    [Fact]
    public void Empty_text_is_refused_so_the_caller_decides_what_it_means()
    {
        Assert.False(ColumnValueConverter.Convert("", Text("name")).IsAdmitted);
    }

    [Theory]
    [InlineData("dd/MM/yyyy", true, "15/01/2024")]
    [InlineData("yyyy-MM-dd'T'HH:mm:ssK", true, "2024-01-15T00:00:00")]
    [InlineData("d MMMM yyyy", true, "15 January 2024")]
    [InlineData("%d/M/yyyy", true, "15/1/2024")]
    [InlineData("dd/MM/yyyy 'yy'", true, "15/01/2024 yy")]          // quoted text is not a part
    [InlineData("dd\\/MM\\/yyyy", true, "15/01/2024")]              // an escaped character is not a part
    [InlineData("ddd MMM yyyy", false, "Mon Jan 2024")]             // ddd is the weekday name, not the day of the month
    [InlineData("dd/MM/'yyyy'", false, "15/01/yyyy")]               // the only year is quoted text
    [InlineData("dd/MM/yyyyy", false, "15/01/02024")]
    [InlineData("dd/MM/yy", false, "15/01/24")]                  // a two-digit year: the century comes from a setting
    [InlineData("dd/MM/yyyy HH:mm:ss.FFF", false, "15/01/2024 00:00:00")]
    [InlineData("D", false, "Monday, 15 January 2024")]
    [InlineData("\\d/MM/yyyy", false, "d/01/2024")]                 // round 3: an escaped letter is not a day
    [InlineData("dd/MM 'x\\'yyyy'", false, "15/01 x'yyyy")]          // round 3: an escaped quote does not end quoted text
    [InlineData("dd/MM \"x\\\"yyyy\"", false, "15/01 x\"yyyy")]
    public void A_date_format_must_name_a_four_digit_year_a_month_and_a_day(string format, bool admitted, string cell)
    {
        // Review round 2, must-fix 2, and round 3, must-fixes 2 and 3: a missing part was taken from the machine. Each
        // cell matches its format, so without the refusal it would be read, with the missing part from the machine.
        Assert.True(DateTime.TryParseExact(cell, format, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _));
        Assert.Equal(admitted, ColumnValueConverter.DateFormatRefusal(format) is null);

        foreach (var column in new[]
                 {
                     Date("day", DateTimeBehavior.DateOnly, DateTimeFormat.DateOnly),
                     Date("when", DateTimeBehavior.UserLocal, DateTimeFormat.DateAndTime),
                 })
        {
            var result = ColumnValueConverter.Convert(cell, column, new ColumnValueOptions { DateFormat = format });
            if (!admitted)
            {
                Assert.False(result.IsAdmitted);
                Assert.Equal(ColumnValueConverter.DateFormatRefusal(format), result.Refusal);
            }
            else if (column.DateTimeBehavior == DateTimeBehavior.DateOnly)
            {
                Assert.True(result.IsAdmitted, result.Refusal);
            }
        }
    }

    [Fact]
    public void A_date_format_with_quoted_text_that_is_not_closed_is_refused()
    {
        Assert.NotNull(ColumnValueConverter.DateFormatRefusal("dd/MM/yyyy 'x"));
        Assert.NotNull(ColumnValueConverter.DateFormatRefusal("dd/MM/yyyy 'x\\'"));
    }
}
