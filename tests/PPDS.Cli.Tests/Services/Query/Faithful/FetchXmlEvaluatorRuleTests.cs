using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Each Dataverse rule of <see cref="FetchXmlEvaluator"/> against raw FetchXML. "Audit" cites the OBSERVED line of the
/// November 2026 live SELECT audit (#1638, docs/qa/select-fidelity-audit-2026-11.md, "OBSERVED Dataverse behaviours");
/// the same operator on the same shape of data must give the same rows here. Rules without an observation say so.
/// </summary>
[Trait("Category", "Unit")]
public class FetchXmlEvaluatorRuleTests
{
    private static EntityCollection Fetch(string inner, string fetchAttributes = "", FakeDataverse? dataverse = null)
        => (dataverse ?? SelectFixture.Create()).RetrieveMultiple(
            $"<fetch{fetchAttributes}><entity name=\"account\"><attribute name=\"accountnumber\" />{inner}</entity></fetch>");

    private static string Keys(EntityCollection result, bool ordered = false)
    {
        var keys = result.Entities.Select(e => e.GetAttributeValue<string>("accountnumber"));
        return string.Join(",", ordered ? keys : keys.OrderBy(k => int.Parse(k[1..])));
    }

    private static string Where(string condition) => $"<filter>{condition}</filter>";

    // R1 (audit: "ne num 5", "not-in num (5,10)", "not-like name 'A%'", "not-begin-with name 'A'",
    // "not-end-with name 'a'", "ne dt ..." each return the NULL rows that T-SQL does not).
    [Theory]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"ne\" value=\"5\" />", "k2,k3,k4,k5,k6,k8,k9,k10")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"not-in\"><value>5</value><value>10</value></condition>", "k3,k4,k5,k6,k8,k9,k10")]
    [InlineData("<condition attribute=\"name\" operator=\"not-like\" value=\"A%\" />", "k3,k4,k5,k6,k7,k9")]
    [InlineData("<condition attribute=\"name\" operator=\"not-begin-with\" value=\"A\" />", "k3,k4,k5,k6,k7,k9")]
    [InlineData("<condition attribute=\"name\" operator=\"not-end-with\" value=\"a\" />", "k4,k5,k6,k7,k8,k10")]
    [InlineData("<condition attribute=\"followupby\" operator=\"ne\" value=\"2024-01-15T00:30:00Z\" />", "k2,k3,k4,k5,k6,k7,k8,k9,k10")]
    public void R1_NegatedOperators_MatchNullRows(string condition, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(condition))));

    // R2 (audit: "not-between num 0..10 ... (same as T-SQL)"): not-between leaves NULL rows out.
    [Fact]
    public void R2_NotBetween_DoesNotMatchNullRows()
        => Assert.Equal("k5,k8,k10", Keys(Fetch(Where("<condition attribute=\"numberofemployees\" operator=\"not-between\"><value>0</value><value>10</value></condition>"))));

    // R2 for the comparison operators: not observed on their own; the audit's "ge dt and lt dt" case leaves the
    // NULL dates out, consistent with it.
    [Theory]
    [InlineData("lt", "5", "k4,k5")]
    [InlineData("ge", "10", "k2,k8,k10")]
    public void R2_ComparisonOperators_DoNotMatchNullRows(string op, string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where($"<condition attribute=\"numberofemployees\" operator=\"{op}\" value=\"{value}\" />"))));

    // R3 (audit: "eq name \"\" (what WHERE name = NULL is sent as): 0 rows").
    [Fact]
    public void R3_EqualsEmptyString_MatchesNothing()
        => Assert.Equal("", Keys(Fetch(Where("<condition attribute=\"name\" operator=\"eq\" value=\"\" />"))));

    // R4 (audit: "eq name 'alpha': keys 1,2,3,30" (case), "eq name 'cafe' ... accent-insensitive",
    // "eq name 'trail' (stored 'trail  '): 1 rows", "eq name 'lead' (stored '  lead'): 0 rows").
    [Theory]
    [InlineData("alpha", "k1,k2")]
    [InlineData("ALPHA", "k1,k2")]
    [InlineData("cafe", "k4,k5")]
    [InlineData("trail", "k7")]
    public void R4_StringEquality_IgnoresCaseAccentsAndTrailingSpaces(string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where($"<condition attribute=\"name\" operator=\"eq\" value=\"{value}\" />"))));

    [Fact]
    public void R4_StringEquality_CountsLeadingSpaces()
    {
        var dataverse = new FakeDataverse().Add(new FakeTable("account")
            .Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String)
            .Row(("accountnumber", "k1"), ("name", "  lead")));

        Assert.Equal("", Keys(Fetch(Where("<condition attribute=\"name\" operator=\"eq\" value=\"lead\" />"), dataverse: dataverse)));
        Assert.Equal("k1", Keys(Fetch(Where("<condition attribute=\"name\" operator=\"eq\" value=\"  lead\" />"), dataverse: dataverse)));
    }

    // R5 (audit: "like name '[ab]%': 14 rows (same as T-SQL)", "begins-with name '[ab]' ... (same as T-SQL)",
    // "begins-with name 'a_': 12 rows" (the _ is a wildcard: T-SQL a[_]% differs), "like name 'a[_]c': 1 rows").
    [Theory]
    [InlineData("like", "[ab]%", "k1,k2,k3,k8,k10")]
    [InlineData("begins-with", "[ab]", "k1,k2,k3,k8,k10")]
    [InlineData("begins-with", "a_", "k1,k2,k8,k10")]
    [InlineData("like", "a[_]c", "k8")]
    [InlineData("like", "%[^a-c]", "k4,k5,k7")]
    public void R5_LikeAndBeginsWith_TreatPercentUnderscoreAndBracketsAsWildcards(string op, string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where($"<condition attribute=\"name\" operator=\"{op}\" value=\"{value}\" />"))));

    // R6 (audit: "order by num asc (NULL position): NULL ... first", "order by num desc: ... NULL last",
    // "order by choice asc (label or value?): ... Blue, Green, Red" (label order)).
    [Theory]
    [InlineData("<order attribute=\"numberofemployees\" /><order attribute=\"accountnumber\" />", "k3,k9,k5,k4,k1,k7,k6,k2,k10,k8")]
    [InlineData("<order attribute=\"numberofemployees\" descending=\"true\" /><order attribute=\"accountnumber\" />", "k8,k10,k2,k6,k1,k7,k4,k5,k3,k9")]
    [InlineData("<order attribute=\"customertypecode\" /><order attribute=\"accountnumber\" />", "k4,k7,k10,k2,k6,k3,k8,k1,k5,k9")]
    public void R6_OrderBy_NullFirstAscendingAndChoiceByLabel(string order, string expected)
        => Assert.Equal(expected, Keys(Fetch(order), ordered: true));

    // R7 (audit: "eq dt '2024-01-15T00:30:00Z': 1 rows", "eq dt '2024-01-15T00:30:00' (no offset): 0 rows",
    // "ge dt '2024-01-15' and lt dt '2024-01-16' (no offset): 1 rows, keys 2", "eq donly '2024-01-15': 1 rows"; caller at UTC−6).
    [Theory]
    [InlineData("<condition attribute=\"followupby\" operator=\"eq\" value=\"2024-01-15T00:30:00Z\" />", "k1")]
    [InlineData("<condition attribute=\"followupby\" operator=\"eq\" value=\"2024-01-15T00:30:00\" />", "")]
    [InlineData("<condition attribute=\"followupby\" operator=\"ge\" value=\"2024-01-15\" /><condition attribute=\"followupby\" operator=\"lt\" value=\"2024-01-16\" />", "k2")]
    [InlineData("<condition attribute=\"ppds_startdate\" operator=\"eq\" value=\"2024-01-15\" />", "k1")]
    public void R7_DateTimeWithoutOffset_IsReadInTheCallersTimeZone(string conditions, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(conditions))));

    // R8 (audit, cause of #1624: Dataverse omits NULL attributes from the returned record).
    [Fact]
    public void R8_NullAttribute_IsAbsentFromTheRecord()
    {
        var result = Fetch("<attribute name=\"name\" />" + Where("<condition attribute=\"accountnumber\" operator=\"in\"><value>k5</value><value>k6</value></condition>"));

        Assert.True(result.Entities.Single(e => e.GetAttributeValue<string>("accountnumber") == "k5").Contains("name"));
        Assert.False(result.Entities.Single(e => e.GetAttributeValue<string>("accountnumber") == "k6").Contains("name"));
    }

    // R9 (not observed): aliased attributes come back under the alias; link-entity attributes as alias.attribute.
    [Fact]
    public void R9_Aliases_NameTheReturnedAttributes()
    {
        var result = SelectFixture.Create().RetrieveMultiple(
            "<fetch><entity name=\"account\"><attribute name=\"name\" alias=\"n\" />" +
            "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\"><attribute name=\"lastname\" /><attribute name=\"lastname\" alias=\"ln\" /></link-entity>" +
            "<filter><condition attribute=\"accountnumber\" operator=\"eq\" value=\"k2\" /></filter></entity></fetch>");

        var entity = Assert.Single(result.Entities);
        Assert.False(entity.Contains("name"));
        Assert.Equal("alpha", entity.GetAttributeValue<AliasedValue>("n").Value);
        Assert.Equal("Brown", entity.GetAttributeValue<AliasedValue>("c.lastname").Value);
        Assert.Equal("Brown", entity.GetAttributeValue<AliasedValue>("ln").Value);
    }

    // R10 (not observed): top and count limit the rows; a page holds count rows.
    [Fact]
    public void R10_TopAndCountLimitTheRows()
    {
        Assert.Equal("k8,k10,k2", Keys(Fetch("<order attribute=\"numberofemployees\" descending=\"true\" />", " top=\"3\""), ordered: true));

        var page2 = Fetch("<order attribute=\"accountnumber\" />", " count=\"4\" page=\"2\"");
        Assert.Equal("k4,k5,k6,k7", Keys(page2, ordered: true)); // string order: k1, k10, k2, ...
        Assert.True(page2.MoreRecords);

        var page3 = Fetch("<order attribute=\"accountnumber\" />", " count=\"4\" page=\"3\"");
        Assert.Equal("k8,k9", Keys(page3, ordered: true));
        Assert.False(page3.MoreRecords);
    }

    [Theory]
    [InlineData(" top=\"3\" page=\"1\"")]
    [InlineData(" top=\"5001\"")]
    public void R10_TopWithPagingOrAbove5000_IsAFault(string attributes)
        => Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch("", attributes));

    // R11 (not observed): inner and outer link-entities; a filter inside an outer link-entity is part of the join.
    [Theory]
    [InlineData("inner", "", "k1,k1,k2,k5")]
    [InlineData("outer", "", "k1,k1,k2,k3,k4,k5,k6,k7,k8,k9,k10")]
    [InlineData("outer", "<filter><condition attribute=\"lastname\" operator=\"eq\" value=\"Smith\" /></filter>", "k1,k2,k3,k4,k5,k6,k7,k8,k9,k10")]
    [InlineData("inner", "<filter><condition attribute=\"lastname\" operator=\"eq\" value=\"Smith\" /></filter>", "k1")]
    public void R11_LinkEntities_JoinLikeSql(string type, string linkFilter, string expected)
        => Assert.Equal(expected, Keys(Fetch($"<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" link-type=\"{type}\" alias=\"c\">{linkFilter}</link-entity>")));

    [Fact]
    public void R11_ConditionWithEntityName_AppliesToTheJoinedRow()
        => Assert.Equal("k3,k4,k6,k7,k8,k9,k10", Keys(Fetch(
            "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" link-type=\"outer\" alias=\"c\" />" +
            Where("<condition entityname=\"c\" attribute=\"lastname\" operator=\"null\" />"))));

    // R12 (not observed): an attribute the table does not have is a fault, in a condition, an order or a column.
    [Theory]
    [InlineData("<filter><condition attribute=\"lastname\" operator=\"eq\" value=\"x\" /></filter>")]
    [InlineData("<order attribute=\"lastname\" />")]
    [InlineData("<attribute name=\"lastname\" />")]
    public void R12_UnknownAttribute_IsAFault(string inner)
        => Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch(inner));

    // Anything not modelled is refused, so a test cannot pass on a guess.
    [Theory]
    [InlineData("<fetch aggregate=\"true\"><entity name=\"account\"><attribute name=\"accountid\" aggregate=\"count\" alias=\"n\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"createdon\" operator=\"last-x-days\" value=\"3\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"eq\" valueof=\"description\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" link-type=\"exists\" alias=\"c\" /></entity></fetch>")]
    public void Unmodelled_FetchXml_IsRefused(string fetchXml)
        => Assert.Throws<NotSupportedException>(() => SelectFixture.Create().RetrieveMultiple(fetchXml));
}
