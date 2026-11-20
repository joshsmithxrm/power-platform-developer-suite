using System.Globalization;
using System.ServiceModel;
using System.Text;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Each Dataverse rule of <see cref="FetchXmlEvaluator"/> against raw FetchXML. "Audit" cites the November 2026 live
/// SELECT audit (#1638, docs/qa/select-fidelity-audit-2026-11.md): an OBSERVED line (raw FetchXML through the SDK) or a case
/// id (a statement through PPDS whose Dataverse behaviour the audit recorded). Rules without an observation say so.
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
        return string.Join(",", ordered ? keys : keys.OrderBy(k => int.Parse(k[1..], CultureInfo.InvariantCulture)));
    }

    private static string Where(string condition) => $"<filter>{condition}</filter>";

    private static string Cond(string attribute, string op, string value)
        => $"<condition attribute=\"{attribute}\" operator=\"{op}\" value=\"{value}\" />";

    /// <summary>An account table with no rows: a refusal must not depend on a row reaching the condition.</summary>
    private static FakeDataverse Empty() => new FakeDataverse().Add(new FakeTable("account")
        .Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String)
        .Column("numberofemployees", FakeColumnType.Integer).Column("revenue", FakeColumnType.Money)
        .Column("parentaccountid", FakeColumnType.Lookup))
        .Add(new FakeTable("contact").Column("lastname", FakeColumnType.String).Column("parentcustomerid", FakeColumnType.Lookup));

    // ───────────── R1 (audit OBSERVED "ne num 5", "not-in num (5,10)", "not-like name 'A%'", "not-begin-with name 'A'",
    // "not-end-with name 'a'", "ne dt ...": each returns the NULL rows that T-SQL does not) ─────────────

    [Theory]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"ne\" value=\"5\" />", "k2,k3,k4,k5,k6,k8,k9,k10")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"not-in\"><value>5</value><value>10</value></condition>", "k3,k4,k5,k6,k8,k9,k10")]
    [InlineData("<condition attribute=\"name\" operator=\"not-like\" value=\"A%\" />", "k3,k4,k5,k6,k7,k9")]
    [InlineData("<condition attribute=\"name\" operator=\"not-begin-with\" value=\"A\" />", "k3,k4,k5,k6,k7,k9")]
    [InlineData("<condition attribute=\"name\" operator=\"not-end-with\" value=\"a\" />", "k4,k5,k6,k7,k8,k10")]
    [InlineData("<condition attribute=\"followupby\" operator=\"ne\" value=\"2024-01-15T00:30:00Z\" />", "k2,k3,k4,k5,k6,k7,k8,k9,k10")]
    public void R1_NegatedOperators_MatchNullRows(string condition, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(condition))));

    // ───────────── R2 ─────────────

    // Audit OBSERVED "not-between num 0..10 ... (same as T-SQL)".
    [Fact]
    public void R2_NotBetween_DoesNotMatchNullRows()
        => Assert.Equal("k5,k8,k10", Keys(Fetch(Where("<condition attribute=\"numberofemployees\" operator=\"not-between\"><value>0</value><value>10</value></condition>"))));

    // Not observed one by one (consistent with the observed ge/lt date probe). k3 and k9 have no employees, k6 no name:
    // none of them may appear. The boundaries pin inclusive and exclusive comparisons.
    [Theory]
    [InlineData("numberofemployees", "eq", "5", "k1,k7")]
    [InlineData("numberofemployees", "lt", "5", "k4,k5")]
    [InlineData("numberofemployees", "le", "5", "k1,k4,k5,k7")]
    [InlineData("numberofemployees", "gt", "10", "k8,k10")]
    [InlineData("numberofemployees", "ge", "10", "k2,k8,k10")]
    [InlineData("name", "like", "%", "k1,k2,k3,k4,k5,k7,k8,k9,k10")]
    [InlineData("name", "begins-with", "", "k1,k2,k3,k4,k5,k7,k8,k9,k10")]
    [InlineData("name", "ends-with", "", "k1,k2,k3,k4,k5,k7,k8,k9,k10")]
    public void R2_OtherOperators_DoNotMatchNullRows(string attribute, string op, string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(Cond(attribute, op, value)))));

    [Theory]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"in\"><value>5</value><value>100</value></condition>", "k1,k7,k8")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"between\"><value>5</value><value>10</value></condition>", "k1,k2,k6,k7")]
    public void R2_InAndBetween_DoNotMatchNullRows_AndAreInclusive(string condition, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(condition))));

    // ───────────── R3 (audit OBSERVED "eq name \"\" (what WHERE name = NULL is sent as): 0 rows") ─────────────

    [Fact]
    public void R3_EqualsEmptyString_MatchesNothing()
        => Assert.Equal("", Keys(Fetch(Where(Cond("name", "eq", "")))));

    [Fact]
    public void R3_StoredEmptyString_IsNull()
    {
        var dataverse = new FakeDataverse().Add(new FakeTable("account")
            .Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String)
            .Row(("accountnumber", "k1"), ("name", "")).Row(("accountnumber", "k2"), ("name", "x")));

        Assert.Equal("k1", Keys(Fetch(Where("<condition attribute=\"name\" operator=\"null\" />"), dataverse: dataverse)));
        Assert.Equal("", Keys(Fetch(Where(Cond("name", "eq", "")), dataverse: dataverse)));
        Assert.Equal("k1", Keys(Fetch(Where(Cond("name", "ne", "x")), dataverse: dataverse))); // R1 on a NULL
        var k1 = Fetch("<attribute name=\"name\" />" + Where(Cond("accountnumber", "eq", "k1")), dataverse: dataverse).Entities.Single();
        Assert.False(k1.Contains("name")); // R8
    }

    // ───────────── R4 (audit OBSERVED "eq name 'alpha'", "eq name 'cafe' (accent-insensitive)", "eq name 'trail'",
    // "eq name 'lead'", "order by name asc") ─────────────

    [Theory]
    [InlineData("alpha", "k1,k2")]
    [InlineData("ALPHA", "k1,k2")]
    [InlineData("cafe", "k4,k5")]
    [InlineData("trail", "k7")]
    [InlineData("trail   ", "k7")]
    public void R4_StringEquality_IgnoresCaseAccentsAndTrailingSpaces(string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(Cond("name", "eq", value)))));

    [Fact]
    public void R4_StringEquality_CountsLeadingSpaces()
    {
        var dataverse = new FakeDataverse().Add(new FakeTable("account")
            .Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String)
            .Row(("accountnumber", "k1"), ("name", "  lead")));

        Assert.Equal("", Keys(Fetch(Where(Cond("name", "eq", "lead")), dataverse: dataverse)));
        Assert.Equal("k1", Keys(Fetch(Where(Cond("name", "eq", "  lead")), dataverse: dataverse)));
    }

    /// <summary>The audit's OBSERVED "order by name asc", without the NULLs and O'Brien (an apostrophe is refused).</summary>
    private static readonly string[] ObservedNameOrder =
    [
        "  lead", "[bracket]", "100", "20", "50% off", "a%c", "a[b]c", "a_c", "ab", "Ab", "abc", "abd", "Alpha", "alpha", "ALPHA",
        "Alpha ", "Ångström", "b", "Beta", "c%d", "Café", "cafe", "dup", "dup", "e_f", "naïve", "trail  ", "under_score",
        "underXscore", "x", "X", "Zeta", "zz"
    ];

    [Fact]
    public void R4_StringOrder_IsTheObservedOrder_OnEveryMachine()
    {
        var table = new FakeTable("account").Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String);
        var shuffled = ObservedNameOrder.Select((n, i) => (n, i)).OrderBy(x => (x.i * 7919) % ObservedNameOrder.Length).ToList();
        foreach (var (name, i) in shuffled)
            table.Row(("accountnumber", "k" + i), ("name", name));

        var result = new FakeDataverse().Add(table).RetrieveMultiple(
            "<fetch><entity name=\"account\"><attribute name=\"name\" /><order attribute=\"name\" /></entity></fetch>");

        // Ties (case, accent and trailing-space variants) keep table order, so compare without them.
        static string Plain(string s) => new string(s.TrimEnd(' ').Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray()).ToUpperInvariant();
        Assert.Equal(ObservedNameOrder.Select(Plain), result.Entities.Select(e => Plain(e.GetAttributeValue<string>("name"))));
    }

    [Theory]
    [InlineData("lt", "b", "k1,k2,k8,k10")]
    [InlineData("ge", "Zeta", "k9")]
    public void R4_StringRange_UsesTheSameOrder(string op, string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(Cond("name", op, value)))));

    [Theory]
    [InlineData("O'Brien")]
    [InlineData("straße")]
    [InlineData("ｱ")]
    public void R4_CharactersWhoseCollationIsNotObserved_AreRefused(string value)
        => Assert.Throws<NotSupportedException>(() => Fetch(Where(Cond("name", "eq", value)), dataverse: Empty()));

    // ───────────── R5 (audit OBSERVED "like name '[ab]%'", "begins-with name '[ab]'", "begins-with name 'a_'",
    // "like name 'a[_]c'", "not-end-with name 'a'" (key 30 'Alpha ' included); W63 '[^a]%', W69 LIKE 'alpha' (not 'Alpha '),
    // W79 '[a-c]%' through PPDS) ─────────────

    [Theory]
    [InlineData("like", "[ab]%", "k1,k2,k3,k8,k10")]
    [InlineData("begins-with", "[ab]", "k1,k2,k3,k8,k10")]
    [InlineData("begins-with", "a_", "k1,k2,k8,k10")]
    [InlineData("like", "a[_]c", "k8")]
    [InlineData("like", "[a-c]%", "k1,k2,k3,k4,k5,k8,k10")]
    [InlineData("like", "[^a]%", "k3,k4,k5,k7,k9")]
    [InlineData("like", "%[^a-c]", "k4,k5,k7")]
    [InlineData("like", "café", "k4,k5")]
    [InlineData("like", "trail", "")]
    [InlineData("like", "trail%", "k7")]
    [InlineData("ends-with", "c", "k8,k10")]
    [InlineData("not-end-with", "l", "k1,k2,k3,k4,k5,k6,k7,k8,k9,k10")]
    public void R5_Like_WildcardsClassesAccentsAndTrailingSpaces(string op, string value, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(Cond("name", op, value)))));

    // ───────────── R6 (audit OBSERVED "order by num asc/desc (NULL position)", "order by choice asc (label or value?)") ─────────────

    [Theory]
    [InlineData("<order attribute=\"numberofemployees\" /><order attribute=\"accountnumber\" />", "k3,k9,k5,k4,k1,k7,k6,k2,k10,k8")]
    [InlineData("<order attribute=\"numberofemployees\" descending=\"true\" /><order attribute=\"accountnumber\" />", "k8,k10,k2,k6,k1,k7,k4,k5,k3,k9")]
    [InlineData("<order attribute=\"customertypecode\" /><order attribute=\"accountnumber\" />", "k4,k7,k10,k2,k6,k3,k8,k1,k5,k9")]
    public void R6_OrderBy_NullFirstAscendingAndChoiceByLabel(string order, string expected)
        => Assert.Equal(expected, Keys(Fetch(order), ordered: true));

    // Not observed: a link-entity's orders apply after the entity's own.
    [Fact]
    public void R6_LinkEntityOrders_ApplyAfterTheEntityOrders()
    {
        var result = Fetch("<order attribute=\"accountnumber\" descending=\"true\" />" +
            "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\"><attribute name=\"lastname\" /><order attribute=\"lastname\" /></link-entity>");

        Assert.Equal(new[] { "k5:Green", "k2:Brown", "k1:Jones", "k1:Smith" },
            result.Entities.Select(e => $"{e.GetAttributeValue<string>("accountnumber")}:{e.GetAttributeValue<AliasedValue>("c.lastname").Value}"));
    }

    [Theory]
    [InlineData("parentaccountid")]
    [InlineData("accountid")]
    public void R6_OrderByLookupOrId_IsRefused(string attribute)
        => Assert.Throws<NotSupportedException>(() => Fetch($"<order attribute=\"{attribute}\" />", dataverse: Empty()));

    // ───────────── R7 (audit OBSERVED "eq dt '2024-01-15T00:30:00Z': 1 rows", "... (no offset): 0 rows",
    // "ge dt '2024-01-15' and lt dt '2024-01-16' (no offset): keys 2", "eq donly '2024-01-15'"; caller at UTC−6) ─────────────

    [Theory]
    [InlineData("<condition attribute=\"followupby\" operator=\"eq\" value=\"2024-01-15T00:30:00Z\" />", "k1")]
    [InlineData("<condition attribute=\"followupby\" operator=\"eq\" value=\"2024-01-15T00:30:00\" />", "")]
    [InlineData("<condition attribute=\"followupby\" operator=\"eq\" value=\"2024-01-14T18:30:00-06:00\" />", "k1")]
    [InlineData("<condition attribute=\"followupby\" operator=\"ge\" value=\"2024-01-15\" /><condition attribute=\"followupby\" operator=\"lt\" value=\"2024-01-16\" />", "k2")]
    [InlineData("<condition attribute=\"ppds_startdate\" operator=\"eq\" value=\"2024-01-15\" />", "k1")]
    public void R7_DateTimeWithoutOffset_IsReadInTheCallersTimeZone(string conditions, string expected)
        => Assert.Equal(expected, Keys(Fetch(Where(conditions))));

    [Theory]
    [InlineData("followupby", "01-15-2024")]
    [InlineData("followupby", "1/15/2024")]
    [InlineData("ppds_startdate", "2024-01-15T00:00:00")]
    public void R7_OtherDateForms_AreRefused(string attribute, string value)
        => Assert.Throws<NotSupportedException>(() => Fetch(Where(Cond(attribute, "eq", value))));

    // ───────────── R8 (audit, cause of #1624) ─────────────

    [Fact]
    public void R8_NullAttribute_IsAbsentFromTheRecord()
    {
        var result = Fetch("<attribute name=\"name\" />" + Where("<condition attribute=\"accountnumber\" operator=\"in\"><value>k5</value><value>k6</value></condition>"));

        Assert.True(result.Entities.Single(e => e.GetAttributeValue<string>("accountnumber") == "k5").Contains("name"));
        Assert.False(result.Entities.Single(e => e.GetAttributeValue<string>("accountnumber") == "k6").Contains("name"));
    }

    // ───────────── R9 (not observed) ─────────────

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

    // ───────────── R10 (not observed) ─────────────

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

    [Fact]
    public void R10_TopWithPaging_IsAFault()
        => Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch("", " top=\"3\" page=\"1\"", dataverse: Empty()));

    [Theory]
    [InlineData(" top=\"5001\"")]
    [InlineData(" count=\"5001\"")]
    public void R10_TopOrCountAbove5000_IsRefused(string attributes)
        => Assert.Throws<NotSupportedException>(() => Fetch("", attributes, dataverse: Empty()));

    // ───────────── R11 (not observed) ─────────────

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

    [Fact]
    public void R11_NullKeys_NeverJoin()
        // Only k3 has a parent account (k1), and k3 is the only account whose parent is k1: one pair, not the NULL ones.
        => Assert.Equal("k3", Keys(Fetch("<link-entity name=\"account\" from=\"parentaccountid\" to=\"parentaccountid\" alias=\"p\" />")));

    [Fact]
    public void R11_InnerLinkUnderOuterLink_IsRefused()
        => Assert.Throws<NotSupportedException>(() => Fetch(
            "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" link-type=\"outer\" alias=\"c\">" +
            "<link-entity name=\"account\" from=\"accountid\" to=\"parentcustomerid\" alias=\"a2\" /></link-entity>", dataverse: Empty()));

    // ───────────── R12 (audit O03, J21, S06-S08: "'P' entity doesn't contain attribute with Name = ...") ─────────────

    [Theory]
    [InlineData("<filter><condition attribute=\"lastname\" operator=\"eq\" value=\"x\" /></filter>")]
    [InlineData("<order attribute=\"lastname\" />")]
    [InlineData("<attribute name=\"lastname\" />")]
    [InlineData("<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\"><filter><condition attribute=\"name\" operator=\"null\" /></filter></link-entity>")]
    public void R12_UnknownAttribute_IsAFault_EvenWithNoRows(string inner)
        => Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch(inner, dataverse: Empty()));

    // ───────────── R13 (audit W15: "ppds_num = 5.0" is a FormatException fault) ─────────────

    [Theory]
    [InlineData("numberofemployees", "5.0")]
    [InlineData("numberofemployees", "1e1")]
    [InlineData("numberofemployees", " 5")]
    [InlineData("numberofemployees", "5 ")]
    [InlineData("numberofemployees", "5,0")]
    [InlineData("numberofemployees", "99999999999")]
    [InlineData("revenue", "1e2")]
    [InlineData("revenue", " 100")]
    [InlineData("parentaccountid", "not-a-guid")]
    public void R13_ValueThatDoesNotConvert_IsAFault_EvenWithNoRows(string attribute, string value)
        => Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch(Where(Cond(attribute, "eq", value)), dataverse: Empty()));

    [Fact]
    public void R13_ValidValues_StillMatch()
    {
        Assert.Equal("k1,k7", Keys(Fetch(Where(Cond("numberofemployees", "eq", "5")))));
        Assert.Equal("k5", Keys(Fetch(Where(Cond("numberofemployees", "eq", "-3")))));
        Assert.Equal("k3", Keys(Fetch(Where(Cond("revenue", "eq", "250.5")))));
    }

    // ───────────── R14 (audit D05 "SELECT DISTINCT ppds_name": 28 of 36 rows, column [ppds_name] only; PG08) ─────────────

    [Fact]
    public void R14_Distinct_UsesTheStringCollation_AndReturnsNoId()
    {
        var result = SelectFixture.Create().RetrieveMultiple(
            "<fetch distinct=\"true\"><entity name=\"account\"><attribute name=\"name\" /></entity></fetch>");

        // Alpha/alpha and Café/cafe collapse; the row without a name is one more.
        Assert.Equal(8, result.Entities.Count);
        Assert.All(result.Entities, e => Assert.Equal(Guid.Empty, e.Id));
        Assert.All(result.Entities, e => Assert.False(e.Contains("accountid")));
    }

    // ───────────── R15 formatted values (audit PRJ01 '11', O09 'No', O07 labels, OBSERVED "formatted value of ppds_dt") ─────────────

    [Fact]
    public void R15_FormattedValues_AsObserved_AndLoudWhereNotModelled()
    {
        var k1 = Fetch("<attribute name=\"numberofemployees\" /><attribute name=\"donotemail\" /><attribute name=\"followupby\" />" +
            "<attribute name=\"customertypecode\" /><attribute name=\"revenue\" /><attribute name=\"ppds_startdate\" />" +
            Where(Cond("accountnumber", "eq", "k1"))).Entities.Single();
        var k2 = Fetch("<attribute name=\"donotemail\" />" + Where(Cond("accountnumber", "eq", "k2"))).Entities.Single();

        Assert.Equal("5", k1.FormattedValues["numberofemployees"]);
        Assert.Equal("Yes", k1.FormattedValues["donotemail"]);
        Assert.Equal("No", k2.FormattedValues["donotemail"]);
        Assert.Equal("1/14/2024 6:30 PM", k1.FormattedValues["followupby"]);
        Assert.Equal("Supplier", k1.FormattedValues["customertypecode"]);
        Assert.Equal(FetchXmlEvaluator.NotModelledFormattedValue, k1.FormattedValues["revenue"]);
        Assert.Equal(FetchXmlEvaluator.NotModelledFormattedValue, k1.FormattedValues["ppds_startdate"]);
        Assert.False(k1.FormattedValues.ContainsKey("accountnumber"));
    }

    // ───────────── The whole document is validated before any row: everything unknown is refused ─────────────

    [Theory]
    [InlineData("<fetch version=\"1.0\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch aggregate=\"true\"><entity name=\"account\"><attribute name=\"accountid\" aggregate=\"count\" alias=\"n\" /></entity></fetch>")]
    [InlineData("<fetch aggregate=\"1\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch distinct=\"1\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch no-lock=\"True\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch><entity name=\"account\" /><entity name=\"contact\" /></fetch>")]
    [InlineData("<fetch><entity name=\"account\" enableprefiltering=\"true\" /></fetch>")]
    [InlineData("<fetch><entity name=\"account\">text</entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><foo /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><attribute name=\"name\" groupby=\"true\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><all-attributes><x /></all-attributes></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><order attribute=\"name\" alias=\"n\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><order attribute=\"name\" descending=\"1\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter type=\"xor\"><condition attribute=\"name\" operator=\"null\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter isquickfindfields=\"true\"><condition attribute=\"name\" operator=\"null\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"last-x-days\" value=\"3\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"eq\" valueof=\"accountnumber\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"eq\"><foo /></condition></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"in\" value=\"a\"><value>b</value></condition></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"numberofemployees\" operator=\"like\" value=\"5%\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"numberofemployees\" operator=\"not-like\" value=\"5%\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"parentaccountid\" operator=\"gt\" value=\"00000000-0000-0000-0000-000000000000\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"like\" value=\"[ab\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" link-type=\"exists\" alias=\"c\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" visible=\"false\" /></entity></fetch>")]
    public void Unmodelled_FetchXml_IsRefused_EvenWithNoRows(string fetchXml)
        => Assert.Throws<NotSupportedException>(() => Empty().RetrieveMultiple(fetchXml));

    // Value checks run before the NULL rule: on an all-NULL column an invalid value is still a fault, not "every row".
    [Theory]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"ne\" value=\"@n\" />")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"ne\" />")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"not-in\" />")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"between\"><value>1</value></condition>")]
    [InlineData("<condition attribute=\"numberofemployees\" operator=\"null\" value=\"1\" />")]
    public void InvalidCondition_OnAnAllNullColumn_IsAFault(string condition)
    {
        var allNull = new FakeDataverse().Add(new FakeTable("account")
            .Column("accountnumber", FakeColumnType.String).Column("numberofemployees", FakeColumnType.Integer)
            .Row(("accountnumber", "k1")));

        Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch(Where(condition), dataverse: allNull));
    }

    // A link with no matching row must not hide an invalid condition inside it or after it.
    [Fact]
    public void InvalidCondition_AfterAJoinWithNoMatches_IsStillRefused()
        => Assert.Throws<NotSupportedException>(() => Fetch(
            "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\">" +
            "<filter><condition attribute=\"lastname\" operator=\"eq\" value=\"nobody\" /></filter></link-entity>" +
            Where("<condition attribute=\"name\" operator=\"on-or-after\" value=\"2024-01-01\" />")));
}
