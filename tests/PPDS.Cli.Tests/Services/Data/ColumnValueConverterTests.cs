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
    public void Converts_by_the_column_type_and_the_same_in_every_culture(string column, string text, string expected)
    {
        var results = InEveryCulture(() =>
        {
            var result = ColumnValueConverter.Convert(text, Columns[column]());
            if (!result.IsAdmitted)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.Refusal));
                Assert.DoesNotContain(text, result.Refusal, StringComparison.Ordinal);
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
    public void Empty_text_is_refused_so_the_caller_decides_what_it_means()
    {
        Assert.False(ColumnValueConverter.Convert("", Text("name")).IsAdmitted);
    }
}
