using System.Xml.Linq;
using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using Xunit;

namespace PPDS.Cli.Tests.Services.Data;

/// <summary>
/// The allow-list and the translation of <see cref="DataFilter"/> (issue 1705): which filters are admitted, the FetchXML
/// conditions each admitted shape becomes, the refusals with their kind and position, and the condition count guard.
/// The columns are those of <see cref="FilterTestColumns.Account"/>.
/// </summary>
[Trait("Category", "Unit")]
public class DataFilterTests
{
    private static BoundDataFilter Bound(string filter, string entity = "account")
        => DataFilter.Translate(entity, filter).Bind(FilterTestColumns.Account());

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
        { "description = N'x'", new[] { "description eq x" } },
        { "numberofemployees < 5", new[] { "numberofemployees lt 5" } },
        { "numberofemployees >= -2147483648", new[] { "numberofemployees ge -2147483648" } },
        { "price <= 5.25", new[] { "price le 5.25" } },
        { "revenue > -5", new[] { "revenue gt -5" } },
        { "revenue >= -2.5", new[] { "revenue ge -2.5" } },
        { "ratio = 0.1", new[] { "ratio eq 0.1" } },
        { "numberofemployees IN (0, 1)", new[] { "numberofemployees in 0,1" } },
        { "numberofemployees NOT IN (0, 1)", new[] { "numberofemployees not-in 0,1", "numberofemployees not-null" } },
        { "name LIKE 'a_c%'", new[] { "name like a_c%" } },
        { "name LIKE 'abc%'", new[] { "name like abc%" } },
        { "name NOT LIKE '%x'", new[] { "name not-like %x", "name not-null" } },
        { "name IS NULL", new[] { "name null" } },
        { "name IS NOT NULL", new[] { "name not-null" } },
        { "revenue BETWEEN 1 AND 10.5", new[] { "revenue between 1,10.5" } },
        { "revenue NOT BETWEEN 1 AND 10", new[] { "revenue not-between 1,10", "revenue not-null" } },
        { "createdon >= '2024-01-15T09:30:00Z'", new[] { "createdon ge 2024-01-15T09:30:00Z" } },
        { "birthdate = '2024-01-15'", new[] { "birthdate eq 2024-01-15" } },
        { "accountid = '0a1b2c3d-0000-4000-8000-00000000000A'", new[] { "accountid eq 0a1b2c3d-0000-4000-8000-00000000000A" } },
        { "primarycontactid IN ('0a1b2c3d-0000-4000-8000-00000000000A')", new[] { "primarycontactid in 0a1b2c3d-0000-4000-8000-00000000000A" } },
        { "[Name] = 'O''Brien'", new[] { "name eq O'Brien" } },
        { "code = '1.2'", new[] { "code eq 1.2" } },
        { "(numberofemployees = 0 OR revenue > 700) AND name IS NOT NULL",
            new[] { "numberofemployees eq 0", "revenue gt 700", "name not-null" } },
    };

    [Theory]
    [MemberData(nameof(AdmittedShapes))]
    public void AdmittedShape_BecomesExactlyTheseConditions(string filter, string[] expected)
    {
        var bound = Bound(filter);

        Conditions(bound.Filter).Should().Equal(expected);
        // One condition per part of the filter; each negated one has one not-null partner.
        var negated = expected.Count(e => e.Split(' ')[1] is "ne" or "not-in" or "not-like" or "not-between");
        bound.ConditionCount.Should().Be(expected.Length - negated);
    }

    [Fact]
    public void NegatedCondition_IsPairedWithNotNull_InsideItsOwnBranch()
    {
        var root = Bound("name = 'x' OR name <> 'y'").Filter;

        // <filter type="or"><condition eq/><filter type="and"><condition ne/><condition not-null/></filter></filter>
        root.Attribute("type")!.Value.Should().Be("or");
        var branch = root.Elements().ElementAt(1);
        branch.Name.LocalName.Should().Be("filter");
        branch.Attribute("type")!.Value.Should().Be("and");
        branch.Elements().Select(e => e.Attribute("operator")!.Value).Should().Equal("ne", "not-null");
    }

    [Fact]
    public void AndAndOr_KeepTheirMeaningAndPrecedence()
    {
        var root = Bound("name = 'a' OR name = 'b' AND numberofemployees = 1").Filter;

        // SQL: AND binds tighter than OR.
        root.Attribute("type")!.Value.Should().Be("or");
        root.Elements().ElementAt(1).Attribute("type")!.Value.Should().Be("and");
    }

    [Fact]
    public void SingleCondition_IsWrappedInAnAndFilter()
    {
        var bound = Bound("numberofemployees = 0");

        bound.Filter.Name.LocalName.Should().Be("filter");
        bound.Filter.Attribute("type")!.Value.Should().Be("and");
        bound.EntityName.Should().Be("account");
        bound.PrimaryIdAttribute.Should().Be("accountid");
    }

    [Fact]
    public void FetchXml_ReadsOnlyTheId_InIdOrder_ByPage()
    {
        var bound = Bound("numberofemployees = 0", entity: "Account");

        var first = XElement.Parse(bound.ToFetchXml(1, null));
        first.Attribute("page")!.Value.Should().Be("1");
        first.Attribute("count")!.Value.Should().Be("5000");
        first.Attribute("paging-cookie").Should().BeNull();
        first.Attribute("top").Should().BeNull();
        var entity = first.Element("entity")!;
        entity.Attribute("name")!.Value.Should().Be("account");
        entity.Elements("attribute").Select(a => a.Attribute("name")!.Value).Should().Equal("accountid");
        entity.Element("order")!.Attribute("attribute")!.Value.Should().Be("accountid");

        var second = XElement.Parse(bound.ToFetchXml(2, "<cookie page=\"1\" />"));
        second.Attribute("page")!.Value.Should().Be("2");
        second.Attribute("paging-cookie")!.Value.Should().Be("<cookie page=\"1\" />");
    }

    /// <summary>Refused by the structure or the text alone, before any connection.</summary>
    public static TheoryData<string, string> RefusedBeforeConnecting => new()
    {
        { "CAST(revenue AS int) = 5", "CAST or CONVERT" },
        { "CONVERT(int, revenue) = 5", "CAST or CONVERT" },
        { "COALESCE(name, 'a') = 'a'", "a function" },
        { "IIF(numberofemployees = 0, 1, 0) = 1", "a function" },
        { "CASE WHEN numberofemployees = 0 THEN 1 ELSE 0 END = 1", "a CASE expression" },
        { "name + 'a' = 'xa'", "arithmetic" },
        { "numberofemployees IN (SELECT numberofemployees FROM account)", "a subquery" },
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
        { "revenue = (5)", "a value in parentheses" },
        { "revenue = +5", "a sign or operator on a value" },
        { "-revenue > 5", "a sign or operator on a value" },
        { "revenue !< 5", "the comparison operator" },
        { "name <> NULL", "a comparison with NULL" },
        { "NULL = name", "a comparison with NULL" },
        { "name NOT IN ('a', NULL)", "NULL in an IN list" },
        { "NOT name = 'x'", "NOT (...)" },
        { "name = N'a' AND (numberofemployees = 0 OR UPPER(name) = N'A')", "a function" },
    };

    [Theory]
    [MemberData(nameof(RefusedBeforeConnecting))]
    public void RefusedShape_IsRefusedByTranslate_NamingItsKind(string filter, string kind)
    {
        var act = () => DataFilter.Translate("account", filter);

        var ex = act.Should().Throw<PpdsException>().Which;
        ex.ErrorCode.Should().Be(ErrorCodes.Validation.FilterNotSupported);
        ex.UserMessage.Should().Contain(kind).And.Contain("at line 1, column ").And.Contain("Nothing was read or changed");
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x09)]
    [InlineData(0x0A)]
    [InlineData(0x1F)]
    [InlineData(0x7F)]
    [InlineData(0x85)]
    [InlineData(0x9F)]
    public void ControlCharacter_InText_IsRefusedByTranslate(int code)
    {
        var filter = "name = N'a" + (char)code + "b'";

        var act = () => DataFilter.Translate("account", filter);

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a control character");
    }

    [Fact]
    public void ControlCharacter_InALikePattern_IsRefusedByTranslate()
    {
        var act = () => DataFilter.Translate("account", "name LIKE N'a" + (char)0x01 + "%'");

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a control character");
    }

    [Fact]
    public void TextWithoutN_HoldingNonAscii_IsRefused_WithN_IsAdmitted()
    {
        var omega = ((char)0x3A9).ToString();

        var refused = () => DataFilter.Translate("account", $"name = '{omega}'");
        refused.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("text without N");

        Conditions(Bound($"name = N'{omega}'").Filter).Should().Equal($"name eq {omega}");
    }

    [Fact]
    public void InList_OfTheCap_IsAdmitted_OneMore_IsRefused()
    {
        string List(int n) => "numberofemployees IN (" + string.Join(", ", Enumerable.Range(0, n)) + ")";

        Bound(List(DataFilter.MaxInListValues)).ConditionCount.Should().Be(1);
        var act = () => DataFilter.Translate("account", List(DataFilter.MaxInListValues + 1));
        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("an IN list of more than 1000 values");
    }

    [Fact]
    public void ValuesInTheWholeFilter_OverTheCap_AreRefused()
    {
        var list = string.Join(", ", Enumerable.Range(0, 700));
        var filter = $"numberofemployees IN ({list}) OR numberofemployees IN ({list}) OR numberofemployees IN ({list})";

        var act = () => DataFilter.Translate("account", filter);

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a filter with more than 2000 values");
    }

    [Theory]
    [InlineData("UPPER(name) = 'SECRETMARKER'")]
    [InlineData("name = 'SECRETMARKER '")]
    [InlineData("name = 'SECRETMARKER' OR LEN(name) = 1")]
    [InlineData("name = 'SECRETMARKER' ORDER BY name")]
    [InlineData("name IN ('SECRETMARKER', NULL)")]
    [InlineData("name > 'SECRETMARKER'")]
    [InlineData("numberofemployees = 'SECRETMARKER'")]
    [InlineData("createdon = 'SECRETMARKER'")]
    public void Refusal_DoesNotRepeatTheLiteralText(string filter)
    {
        var act = () => Bound(filter);

        // Some are parse errors, some refused by the structure, some by the column's type; none may echo the value.
        act.Should().Throw<PpdsException>().Which.UserMessage.Should().NotContain("SECRETMARKER");
    }

    [Fact]
    public void Refusal_GivesThePositionOfThePart()
    {
        var act = () => DataFilter.Translate("account", "numberofemployees = 0 AND\nLEN(name) = 3");

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a function at line 2, column 1");
    }

    [Fact]
    public void ColumnRefusal_GivesThePositionOfTheColumn()
    {
        var act = () => Bound("name = N'x' AND nosuchcolumn = 1");

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a column the table does not have at line 1, column 17");
    }

    [Fact]
    public void ValueRefusal_GivesThePositionOfTheValue()
    {
        var act = () => Bound("code = 5");

        act.Should().Throw<PpdsException>().Which.UserMessage.Should().Contain("a number compared with a text column at line 1, column 8");
    }

    [Theory]
    [InlineData("numberofemployees = 0; DELETE FROM account")]
    [InlineData("numberofemployees = 0 ORDER BY name")]
    [InlineData("numberofemployees = 0) OR (1 = 1")]
    [InlineData("numberofemployees = 0 UNION SELECT accountid FROM account")]
    [InlineData("numberofemployees = 0 OPTION (MAXDOP 1)")]
    [InlineData("numberofemployees eq 0")]
    [InlineData("-- numberofemployees = 0")]
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
        var act = () => DataFilter.Translate(entity, "numberofemployees = 0");

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Validation.InvalidValue);
    }

    // --- The condition count guard -------------------------------------------------------------------------------

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
        var condition = ParseCondition("numberofemployees = 0 AND name <> 'x'");
        var xml = XElement.Parse(
            "<filter type=\"and\"><condition attribute=\"numberofemployees\" operator=\"eq\" value=\"0\" />" +
            "<filter type=\"and\"><condition attribute=\"name\" operator=\"ne\" value=\"x\" />" +
            "<condition attribute=\"name\" operator=\"not-null\" /></filter></filter>");

        DataFilter.VerifyEveryConditionEmitted(condition, xml).Should().Be(2);
    }

    [Fact]
    public void CountGuard_ADroppedCondition_StopsTheCommand()
    {
        // What the query engine's generator emits for "numberofemployees = 0 AND UPPER(name) = 'X'": the function is left out.
        var condition = ParseCondition("numberofemployees = 0 AND UPPER(name) = 'X'");
        var xml = XElement.Parse(
            "<filter type=\"and\"><condition attribute=\"numberofemployees\" operator=\"eq\" value=\"0\" /></filter>");

        var act = () => DataFilter.VerifyEveryConditionEmitted(condition, xml);

        act.Should().Throw<PpdsException>().Which.ErrorCode.Should().Be(ErrorCodes.Operation.Internal);
    }

    [Fact]
    public void CountGuard_AnAddedCondition_StopsTheCommand()
    {
        var condition = ParseCondition("name = 'x'");
        var xml = XElement.Parse(
            "<filter type=\"and\"><condition attribute=\"name\" operator=\"eq\" value=\"x\" />" +
            "<condition attribute=\"name\" operator=\"eq\" value=\"y\" /></filter>");

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
