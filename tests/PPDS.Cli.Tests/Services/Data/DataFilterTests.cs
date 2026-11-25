using System.Xml.Linq;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// The allow-list and the translation of <see cref="DataFilter"/> (issue 1705): which filters are admitted, the FetchXML
/// conditions each admitted shape becomes, the refusals with their kind and position, and the independent count guard.
/// </summary>
[Trait("Category", "Unit")]
public class DataFilterTests
{
    private static List<string> Conditions(XElement filter) => filter.DescendantsAndSelf("condition").Select(c =>
    {
        var values = c.Attribute("value") is { } v ? new[] { v.Value } : c.Elements("value").Select(e => e.Value).ToArray();
        return $"{c.Attribute("attribute")!.Value} {c.Attribute("operator")!.Value} {string.Join(",", values)}".TrimEnd();
    }).ToList();

    public static TheoryData<string, string[]> AdmittedShapes => new()
    {
        { "name = N'x'", new[] { "name eq x" } },
        { "name = 'x'", new[] { "name eq x" } },
        { "name <> 'x'", new[] { "name ne x", "name not-null" } },
        { "name != 'x'", new[] { "name ne x", "name not-null" } },
        { "revenue < 5", new[] { "revenue lt 5" } },
        { "revenue <= 5.25", new[] { "revenue le 5.25" } },
        { "revenue > -5", new[] { "revenue gt -5" } },
        { "revenue >= -2.5", new[] { "revenue ge -2.5" } },
        { "statecode IN (0, 1)", new[] { "statecode in 0,1" } },
        { "statecode NOT IN (0, 1)", new[] { "statecode not-in 0,1", "statecode not-null" } },
        { "name LIKE 'a_c%'", new[] { "name like a_c%" } },
        { "name LIKE 'abc%'", new[] { "name like abc%" } },
        { "name NOT LIKE '%x'", new[] { "name not-like %x", "name not-null" } },
        { "name IS NULL", new[] { "name null" } },
        { "name IS NOT NULL", new[] { "name not-null" } },
        { "revenue BETWEEN 1 AND 10.5", new[] { "revenue between 1,10.5" } },
        { "revenue NOT BETWEEN 1 AND 10", new[] { "revenue not-between 1,10", "revenue not-null" } },
        { "createdon >= '2024-01-15T09:30:00Z'", new[] { "createdon ge 2024-01-15T09:30:00Z" } },
        { "createdon < '2024-01-15T10:30:00+01:00'", new[] { "createdon lt 2024-01-15T10:30:00+01:00" } },
        { "accountid = '0a1b2c3d-0000-4000-8000-00000000000A'", new[] { "accountid eq 0a1b2c3d-0000-4000-8000-00000000000A" } },
        { "[Name] = 'O''Brien'", new[] { "name eq O'Brien" } },
        { "(statecode = 0 OR revenue > 700) AND name IS NOT NULL",
            new[] { "statecode eq 0", "revenue gt 700", "name not-null" } },
    };

    [Theory]
    [MemberData(nameof(AdmittedShapes))]
    public void AdmittedShape_BecomesExactlyTheseConditions(string filter, string[] expected)
    {
        var admitted = DataFilter.Translate("account", filter);

        Conditions(admitted.Filter).Should().Equal(expected);
        // One condition per part of the filter; each negated one has one not-null partner.
        var negated = expected.Count(e => e.Split(' ')[1] is "ne" or "not-in" or "not-like" or "not-between");
        admitted.ConditionCount.Should().Be(expected.Length - negated);
    }

    [Fact]
    public void NegatedCondition_IsPairedWithNotNull_InsideItsOwnBranch()
    {
        var admitted = DataFilter.Translate("account", "name = 'x' OR name <> 'y'");

        // <filter type="or"><condition eq/><filter type="and"><condition ne/><condition not-null/></filter></filter>
        var root = admitted.Filter;
        root.Attribute("type")!.Value.Should().Be("or");
        var branch = root.Elements().ElementAt(1);
        branch.Name.LocalName.Should().Be("filter");
        branch.Attribute("type")!.Value.Should().Be("and");
        branch.Elements().Select(e => e.Attribute("operator")!.Value).Should().Equal("ne", "not-null");
    }

    [Fact]
    public void SingleCondition_IsWrappedInAnAndFilter()
    {
        var admitted = DataFilter.Translate("account", "statecode = 0");

        admitted.Filter.Name.LocalName.Should().Be("filter");
        admitted.Filter.Attribute("type")!.Value.Should().Be("and");
        admitted.EntityName.Should().Be("account");
        admitted.PrimaryIdAttribute.Should().Be("accountid");
    }

    [Fact]
    public void FetchXml_ReadsOnlyTheId_InIdOrder_ByPage()
    {
        var admitted = DataFilter.Translate("Account", "statecode = 0");

        var first = XElement.Parse(admitted.ToFetchXml(1, null));
        first.Attribute("page")!.Value.Should().Be("1");
        first.Attribute("count")!.Value.Should().Be("5000");
        first.Attribute("paging-cookie").Should().BeNull();
        first.Attribute("top").Should().BeNull();
        var entity = first.Element("entity")!;
        entity.Attribute("name")!.Value.Should().Be("account");
        entity.Elements("attribute").Select(a => a.Attribute("name")!.Value).Should().Equal("accountid");
        entity.Element("order")!.Attribute("attribute")!.Value.Should().Be("accountid");

        var second = XElement.Parse(admitted.ToFetchXml(2, "<cookie page=\"1\" />"));
        second.Attribute("page")!.Value.Should().Be("2");
        second.Attribute("paging-cookie")!.Value.Should().Be("<cookie page=\"1\" />");
    }

    public static TheoryData<string, string> RefusedShapes => new()
    {
        { "CAST(revenue AS int) = 5", "CAST or CONVERT" },
        { "CONVERT(int, revenue) = 5", "CAST or CONVERT" },
        { "COALESCE(name, 'a') = 'a'", "a function" },
        { "IIF(statecode = 0, 1, 0) = 1", "a function" },
        { "CASE WHEN statecode = 0 THEN 1 ELSE 0 END = 1", "a CASE expression" },
        { "name + 'a' = 'xa'", "arithmetic" },
        { "statecode IN (SELECT statecode FROM account)", "a subquery" },
        { "EXISTS (SELECT 1 FROM account)", "a subquery (EXISTS)" },
        { "revenue = (SELECT MAX(revenue) FROM account)", "a subquery" },
        { "revenue = @limit", "a variable" },
        { "5 < revenue", "a literal before the column" },
        { "1 = 1", "a comparison without a column" },
        { "name COLLATE Latin1_General_CS_AS = 'x'", "COLLATE" },
        { "account.name = 'x'", "a column with a table or alias prefix" },
        { "name LIKE '[a]%'", "a LIKE pattern with [ or ]" },
        { "name LIKE 'a!%' ESCAPE '!'", "LIKE with ESCAPE" },
        { "name LIKE accountnumber", "a LIKE pattern that is not quoted text" },
        { "revenue = 1.5E0", "a literal of a kind not supported here" },
        { "revenue = $5", "a literal of a kind not supported here" },
        { "revenue = 0x0A", "a literal of a kind not supported here" },
        { "name = ''", "empty text" },
        { "name = ' x'", "white space" },
        { "name = 'x '", "white space" },
        { "name > 'abc'", "a range comparison" },
        { "name BETWEEN 'a' AND 'b'", "a range comparison" },
        { "revenue = (5)", "a value in parentheses" },
        { "revenue = +5", "a sign or operator on a value" },
        { "-revenue > 5", "a sign or operator on a value" },
        { "revenue !< 5", "the comparison operator" },
        { "name <> NULL", "a comparison with NULL" },
        { "NULL = name", "a comparison with NULL" },
        { "name NOT IN ('a', NULL)", "NULL in an IN list" },
        { "NOT name = 'x'", "NOT (...)" },
        { "accountid = '0a1b2c3d000040008000000000000000'", "a GUID not written as 8-4-4-4-12" },
        { "accountid = '{0a1b2c3d-0000-4000-8000-000000000000}'", "a GUID not written as 8-4-4-4-12" },
        { "createdon = '2024-01-15'", "a date and time without a time zone" },
        { "createdon = '2024-01-15T09:30:00'", "a date and time without a time zone" },
        { "createdon = '2024-01-15 09:30:00Z'", "a date and time without a time zone" },
        { "createdon = '1/2/2024'", "a date and time without a time zone" },
        { "createdon = '2024-01-15T09:30:00+15:00'", "a date and time without a time zone" },
        { "createdon > N'2024-01-15'", "a date and time without a time zone" },
        { "name = N'a' AND (statecode = 0 OR UPPER(name) = N'A')", "a function" },
    };

    [Theory]
    [MemberData(nameof(RefusedShapes))]
    public void RefusedShape_IsRefusedNamingItsKind(string filter, string kind)
    {
        var act = () => DataFilter.Translate("account", filter);

        var ex = act.Should().Throw<PpdsException>().Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.FilterNotSupported);
        ex.UserMessage.Should().Contain(kind).And.Contain("at line 1, column ").And.Contain("Nothing was read or changed");
    }

    [Fact]
    public void TextWithoutN_HoldingNonAscii_IsRefused_WithN_IsAdmitted()
    {
        var omega = ((char)0x3A9).ToString();

        var refused = () => DataFilter.Translate("account", $"name = '{omega}'");
        refused.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("text without N");

        Conditions(DataFilter.Translate("account", $"name = N'{omega}'").Filter).Should().Equal($"name eq {omega}");
    }

    [Theory]
    [InlineData("UPPER(name) = 'SECRETMARKER'")]
    [InlineData("name = 'SECRETMARKER '")]
    [InlineData("name = 'SECRETMARKER' OR LEN(name) = 1")]
    [InlineData("name = 'SECRETMARKER' ORDER BY name")]
    [InlineData("name IN ('SECRETMARKER', NULL)")]
    [InlineData("name > 'SECRETMARKER'")]
    public void Refusal_DoesNotRepeatTheLiteralText(string filter)
    {
        var act = () => DataFilter.Translate("account", filter);

        // Some of these are refused as a parse error, some by the allow-list; neither may echo the value.
        act.Should().Throw<PpdsException>().Which.UserMessage.Should().NotContain("SECRETMARKER");
    }

    [Fact]
    public void Refusal_GivesThePositionOfThePart()
    {
        var act = () => DataFilter.Translate("account", "statecode = 0 AND\nLEN(name) = 3");

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a function at line 2, column 1");
    }

    [Theory]
    [InlineData("statecode = 0; DELETE FROM account")]
    [InlineData("statecode = 0 ORDER BY name")]
    [InlineData("statecode = 0) OR (1 = 1")]
    [InlineData("statecode = 0 UNION SELECT accountid FROM account")]
    [InlineData("statecode = 0 OPTION (MAXDOP 1)")]
    [InlineData("statecode eq 0")]
    [InlineData("-- statecode = 0")]
    public void TextThatIsNotOneCondition_IsAParseError(string filter)
    {
        var act = () => DataFilter.Translate("account", filter);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Query.ParseError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyFilter_IsRefused_NeverMeansEveryRecord(string? filter)
    {
        var act = () => DataFilter.Translate("account", filter);

        var ex = act.Should().Throw<PpdsException>().Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
        ex.UserMessage.Should().Contain("data truncate");
    }

    [Theory]
    [InlineData("bad name")]
    [InlineData("account'")]
    [InlineData("")]
    public void EntityThatIsNotALogicalName_IsRefused(string entity)
    {
        var act = () => DataFilter.Translate(entity, "statecode = 0");

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    // --- The independent count guard ---------------------------------------------------------------------------

    private static BooleanExpression ParseCondition(string text)
    {
        using var reader = new StringReader(text);
        var condition = new TSql160Parser(true).ParseBooleanExpression(reader, out var errors);
        errors.Should().BeEmpty();
        return condition;
    }

    [Fact]
    public void CountGuard_AFilterWithEveryCondition_Passes()
    {
        var condition = ParseCondition("statecode = 0 AND name <> 'x'");
        var xml = XElement.Parse(
            "<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" />" +
            "<filter type=\"and\"><condition attribute=\"name\" operator=\"ne\" value=\"x\" />" +
            "<condition attribute=\"name\" operator=\"not-null\" /></filter></filter>");

        DataFilter.VerifyEveryConditionEmitted(condition, xml).Should().Be(2);
    }

    [Fact]
    public void CountGuard_ADroppedCondition_StopsTheCommand()
    {
        // What the query engine's generator emits for "statecode = 0 AND UPPER(name) = 'X'": the function is left out.
        var condition = ParseCondition("statecode = 0 AND UPPER(name) = 'X'");
        var xml = XElement.Parse(
            "<filter type=\"and\"><condition attribute=\"statecode\" operator=\"eq\" value=\"0\" /></filter>");

        var act = () => DataFilter.VerifyEveryConditionEmitted(condition, xml);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Operation.Internal);
    }

    [Fact]
    public void CountGuard_ANegatedConditionWithoutItsNotNull_StopsTheCommand()
    {
        var condition = ParseCondition("name <> 'x'");
        var xml = XElement.Parse("<filter type=\"and\"><condition attribute=\"name\" operator=\"ne\" value=\"x\" /></filter>");

        var act = () => DataFilter.VerifyEveryConditionEmitted(condition, xml);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Operation.Internal);
    }

    [Fact]
    public void CountGuard_ANotNullOnAnotherColumn_IsNotAPartner()
    {
        var condition = ParseCondition("name <> 'x' AND accountnumber IS NOT NULL");
        var xml = XElement.Parse(
            "<filter type=\"and\"><condition attribute=\"name\" operator=\"ne\" value=\"x\" />" +
            "<condition attribute=\"accountnumber\" operator=\"not-null\" /></filter>");

        var act = () => DataFilter.VerifyEveryConditionEmitted(condition, xml);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Operation.Internal);
    }
}
