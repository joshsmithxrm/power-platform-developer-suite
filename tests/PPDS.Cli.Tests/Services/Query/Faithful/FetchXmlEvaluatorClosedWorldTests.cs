using System.Globalization;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The closed world of <see cref="FakeDataverse"/>: a FetchXML shape or value form with no observation behind it in the
/// November 2026 audit is refused with "not observed: ..." before any row is read, so a test that depends on it fails
/// instead of passing on a guess. Also pins behaviours that only an exact row set shows.
/// </summary>
[Trait("Category", "Unit")]
public class FetchXmlEvaluatorClosedWorldTests
{
    private static EntityCollection Fetch(string inner, string fetchAttributes = "", FakeDataverse? dataverse = null)
        => (dataverse ?? SelectFixture.Create()).RetrieveMultiple(
            $"<fetch{fetchAttributes}><entity name=\"account\"><attribute name=\"accountnumber\" />{inner}</entity></fetch>");

    private static string Keys(EntityCollection result, bool ordered = false)
    {
        var keys = result.Entities.Select(e => e.GetAttributeValue<string>("accountnumber"));
        return string.Join(",", ordered ? keys : keys.OrderBy(k => int.Parse(k[1..], CultureInfo.InvariantCulture)));
    }

    private static FakeDataverse Empty() => new FakeDataverse()
        .Add(new FakeTable("account").Column("accountnumber", FakeColumnType.String).Column("name", FakeColumnType.String)
            .Column("revenue", FakeColumnType.Money).Column("parentaccountid", FakeColumnType.Lookup))
        .Add(new FakeTable("contact").Column("lastname", FakeColumnType.String).Column("parentcustomerid", FakeColumnType.Lookup));

    private static void Refused(string inner, string fetchAttributes = "")
    {
        var ex = Assert.Throws<NotSupportedException>(() => Fetch(inner, fetchAttributes, Empty()));
        Assert.Contains("not observed:", ex.Message);
    }

    private const string Link = "<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\">";

    // ───────────── Shapes the audit never observed ─────────────

    [Theory]
    [InlineData("<filter />")]
    [InlineData("<filter type=\"or\" />")]
    [InlineData("<filter type=\"or\"><condition attribute=\"name\" operator=\"eq\" value=\"Beta\" /><filter type=\"and\"></filter></filter>")]
    [InlineData(Link + "<filter /></link-entity>")]
    public void EmptyFilter_IsRefused(string inner) => Refused(inner);

    [Theory]
    [InlineData("<filter><condition entityname=\"account\" attribute=\"name\" operator=\"null\" /></filter>")]
    [InlineData("<filter><condition entityname=\"\" attribute=\"name\" operator=\"null\" /></filter>")]
    [InlineData("<filter><condition entityname=\"x\" attribute=\"name\" operator=\"null\" /></filter>")]
    [InlineData(Link + "<filter><condition entityname=\"c\" attribute=\"lastname\" operator=\"null\" /></filter></link-entity>")]
    [InlineData("<order entityname=\"account\" attribute=\"name\" />")]
    public void EntityName_OtherThanAnEnclosingLinkAlias_IsRefused(string inner) => Refused(inner);

    [Theory]
    [InlineData("<attribute name=\"name\" alias=\"n\" /><attribute name=\"revenue\" alias=\"n\" />")]
    [InlineData("<attribute name=\"revenue\" alias=\"name\" /><attribute name=\"name\" />")]
    [InlineData("<attribute name=\"name\" alias=\"accountnumber\" />")]
    [InlineData(Link + "<attribute name=\"lastname\" alias=\"name\" /></link-entity><attribute name=\"name\" />")]
    public void TwoAttributesUnderOneKey_AreRefused_EvenWithNoRows(string inner) => Refused(inner);

    // Audit PRJ12: the same attribute twice (no alias) comes back once.
    [Fact]
    public void RepeatedAttribute_ComesBackOnce()
    {
        var entity = Fetch("<attribute name=\"name\" /><attribute name=\"name\" /><filter><condition attribute=\"accountnumber\" operator=\"eq\" value=\"k3\" /></filter>").Entities.Single();
        Assert.Equal("Beta", entity.GetAttributeValue<string>("name"));
    }

    [Theory]
    [InlineData("<link-entity name=\"contact\" from=\"lastname\" to=\"name\" alias=\"c\" />")]
    [InlineData("<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"name\" alias=\"c\" />")]
    [InlineData("<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" /><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" />")]
    [InlineData("<link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"\" />")]
    public void UnobservedJoins_AreRefused(string inner) => Refused(inner);

    // Audit O03 ("look up attribute by name is case-sensitive"): names are matched exactly.
    [Fact]
    public void Names_AreCaseSensitive()
    {
        Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch("<attribute name=\"Name\" />", dataverse: Empty()));
        // An unknown table is the fault of audit C01 and E07.
        Assert.Throws<FaultException<OrganizationServiceFault>>(() => Empty().RetrieveMultiple("<fetch><entity name=\"Account\" /></fetch>"));
        Assert.Throws<FaultException<OrganizationServiceFault>>(() => Fetch("<link-entity name=\"lead\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\" />", dataverse: Empty()));
    }

    [Theory]
    [InlineData("<fetch mapping=\"internal\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch distinct=\"0\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch returntotalrecordcount=\"1\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch xmlns:x=\"urn:x\"><entity name=\"account\" /></fetch>")]
    [InlineData("<fetch><entity name=\"account\" x:y=\"1\" xmlns:x=\"urn:x\" /></fetch>")]
    [InlineData("<fetch><x:entity name=\"account\" xmlns:x=\"urn:x\" /></fetch>")]
    [InlineData("<fetch>text<entity name=\"account\" /></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter>text<condition attribute=\"name\" operator=\"null\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"null\">text</condition></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><link-entity name=\"contact\" from=\"parentcustomerid\" to=\"accountid\" alias=\"c\">text</link-entity></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><attribute name=\"name\"><x /></attribute></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><attribute name=\"name\">text</attribute></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><attribute /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><attribute name=\"name\" alias=\"\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><all-attributes x=\"1\" /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><order attribute=\"name\"><x /></order></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><order /></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition operator=\"null\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"in\"><value x=\"1\">a</value></condition></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"in\"><value><x /></value></condition></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"eq\"><value>a</value><value>b</value></condition></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"name\" operator=\"in\" /></filter></entity></fetch>")]
    [InlineData("<fetch><entity name=\"account\"><filter><condition attribute=\"revenue\" operator=\"between\"><value>1</value><value>2</value><value>3</value></condition></filter></entity></fetch>")]
    public void RefusalGates_HoldOnAnEmptyTable(string fetchXml)
    {
        var ex = Assert.Throws<NotSupportedException>(() => Empty().RetrieveMultiple(fetchXml));
        Assert.Contains("not observed:", ex.Message);
    }

    // ───────────── Rows the fake will not hold ─────────────

    [Fact]
    public void FixtureRows_WithUnobservedValues_AreRefused()
    {
        var table = new FakeTable("account").Column("name", FakeColumnType.String).Column("parentaccountid", FakeColumnType.Lookup);
        Assert.Throws<NotSupportedException>(() => table.Row(("name", "   ")));
        Assert.Throws<NotSupportedException>(() => table.Row(("parentaccountid", new EntityReference("account", Guid.NewGuid()))));
        Assert.Throws<ArgumentException>(() => table.Row(("Name", "x")));
    }

    // ───────────── R7 (audit W104 '20240115' and X05 '@d': a Dataverse fault) ─────────────

    [Theory]
    [InlineData("20240115")]
    [InlineData("@d")]
    public void R7_DigitsOnlyOrVariableText_IsTheObservedFault(string value)
    {
        var ex = Assert.Throws<FaultException<OrganizationServiceFault>>(() =>
            Fetch($"<filter><condition attribute=\"followupby\" operator=\"gt\" value=\"{value}\" /></filter>"));
        Assert.Contains("The date-time format for", ex.Message);
    }

    [Theory]
    [InlineData("2024-02-30")]
    [InlineData("2024-01-15T25:00:00")]
    [InlineData("01-15-2024")]
    [InlineData("2024-01-15T00:30:00+0600")]
    public void R7_OtherDateForms_AreRefused(string value)
        => Assert.Throws<NotSupportedException>(() => Fetch($"<filter><condition attribute=\"followupby\" operator=\"eq\" value=\"{value}\" /></filter>"));

    // ───────────── R14 distinct ─────────────

    [Fact(Skip = "Waiting for a live observation (LiveObservationsOwed.md): condition not-null Money")]
    public void R14_Distinct_ComparesMoneyByValue_AndKeepsColumnsApart()
    {
        var table = new FakeTable("account").Column("accountnumber", FakeColumnType.String)
            .Column("revenue", FakeColumnType.Money).Column("a", FakeColumnType.Integer).Column("b", FakeColumnType.Integer)
            .Row(("revenue", new Money(100m))).Row(("revenue", new Money(100.00m)))
            .Row(("a", 1), ("b", 2)).Row(("a", 2), ("b", 1));
        var dataverse = new FakeDataverse().Add(table);

        Assert.Single(dataverse.RetrieveMultiple("<fetch distinct=\"true\"><entity name=\"account\"><attribute name=\"revenue\" /><filter><condition attribute=\"revenue\" operator=\"not-null\" /></filter></entity></fetch>").Entities);
        Assert.Equal(2, dataverse.RetrieveMultiple("<fetch distinct=\"true\"><entity name=\"account\"><attribute name=\"a\" /><attribute name=\"b\" /><filter><condition attribute=\"a\" operator=\"not-null\" /></filter></entity></fetch>").Entities.Count);
    }

    // ───────────── R15 lookups (audit V11, V12: the lookup's name is the parent's name) ─────────────

    [Fact]
    public void R15_LookupFormattedValue_IsTheRecordName()
    {
        var k3 = Fetch("<attribute name=\"parentaccountid\" /><filter><condition attribute=\"accountnumber\" operator=\"eq\" value=\"k3\" /></filter>").Entities.Single();
        Assert.Equal("Alpha", k3.FormattedValues["parentaccountid"]);
    }

    [Fact]
    public async Task R15_LookupName_ReachesTheNameColumnThroughTheEngine()
    {
        var result = await FaithfulSelect.RunAsync("SELECT accountnumber, parentaccountid FROM account WHERE accountnumber = 'k3'");
        Assert.Equal("Alpha", FaithfulSelect.Column(result, "parentaccountidname").Single());
    }

    [Fact]
    public void R15_WholeNumbers_FormatWithGroupSeparators() // audit PG01/PG03 '4,640', D01 '-100'
    {
        var table = new FakeTable("account").Column("accountnumber", FakeColumnType.String).Column("n", FakeColumnType.Integer)
            .Row(("accountnumber", "k1"), ("n", 999)).Row(("accountnumber", "k2"), ("n", 1000)).Row(("accountnumber", "k3"), ("n", -999));
        var result = new FakeDataverse().Add(table).RetrieveMultiple("<fetch><entity name=\"account\"><attribute name=\"n\" /><order attribute=\"n\" /></entity></fetch>");

        Assert.Equal(new[] { "-999", "999", "1,000" }, result.Entities.Select(e => e.FormattedValues["n"]));
    }

    // ───────────── Pins for behaviours only an exact row set shows ─────────────

    [Theory]
    [InlineData("abc%", "k10")]          // % matches zero characters
    [InlineData("a__", "k8,k10")]        // _ matches exactly one character
    [InlineData("_eta", "k3,k9")]
    [InlineData("Beta_", "")]
    public void Like_PercentAndUnderscore_MatchZeroOrMoreAndExactlyOne(string pattern, string expected)
        => Assert.Equal(expected, Keys(Fetch($"<filter><condition attribute=\"name\" operator=\"like\" value=\"{pattern}\" /></filter>")));

    [Fact(Skip = "Waiting for a live observation (LiveObservationsOwed.md): link without link-type; a link nested under a link")]
    public void NestedLinkEntity_JoinsOnItsParentLink()
    {
        var result = Fetch(Link + "<link-entity name=\"account\" from=\"accountid\" to=\"parentcustomerid\" alias=\"a2\"><attribute name=\"name\" /></link-entity></link-entity>");
        Assert.Equal(new[] { "k1:Alpha", "k1:Alpha", "k2:alpha", "k5:cafe" },
            result.Entities.Select(e => $"{e.GetAttributeValue<string>("accountnumber")}:{e.GetAttributeValue<AliasedValue>("a2.name").Value}").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SecondSortKey_Descending()
        => Assert.Equal("k9,k3,k5,k4,k7,k1,k6,k2,k10,k8", Keys(Fetch(
            "<order attribute=\"numberofemployees\" /><order attribute=\"accountnumber\" descending=\"true\" />"), ordered: true));

    [Fact]
    public void AllAttributes_ReturnsEveryNonNullColumn()
    {
        var k1 = SelectFixture.Create().RetrieveMultiple(
            "<fetch><entity name=\"account\"><all-attributes /><filter><condition attribute=\"accountnumber\" operator=\"eq\" value=\"k1\" /></filter></entity></fetch>").Entities.Single();
        Assert.Equal(
            new[] { "accountid", "accountnumber", "createdon", "customertypecode", "donotemail", "followupby", "name", "numberofemployees", "ppds_startdate", "revenue" },
            k1.Attributes.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("lt", "1.5", "k1")]
    public void Double_ComparesNumerically(string op, string value, string expected)
    {
        var table = new FakeTable("account").Column("accountnumber", FakeColumnType.String).Column("f", FakeColumnType.Double)
            .Row(("accountnumber", "k1"), ("f", 1.25)).Row(("accountnumber", "k2"), ("f", 2.0)).Row(("accountnumber", "k3"), ("f", 10.0));
        Assert.Equal(expected, Keys(Fetch($"<filter><condition attribute=\"f\" operator=\"{op}\" value=\"{value}\" /></filter>", dataverse: new FakeDataverse().Add(table))));
    }

    [Theory(Skip = "Waiting for a live observation (LiveObservationsOwed.md): condition ge Double")]
    [InlineData("ge", "2.25", "k3")]
    public void Double_ComparesNumerically_Ge(string op, string value, string expected) => Double_ComparesNumerically(op, value, expected);

    [Fact(Skip = "Waiting for a live observation (LiveObservationsOwed.md): returntotalrecordcount")]
    public void Paging_ReportsMoreRecordsCookieAndTotal()
    {
        var first = Fetch("<order attribute=\"accountnumber\" />", " count=\"4\" page=\"1\" returntotalrecordcount=\"true\"");
        Assert.True(first.MoreRecords);
        Assert.NotNull(first.PagingCookie);
        Assert.Equal(10, first.TotalRecordCount);

        var last = Fetch("<order attribute=\"accountnumber\" />", " count=\"5\" page=\"2\"");
        Assert.False(last.MoreRecords);
        Assert.Null(last.PagingCookie);
        Assert.Equal(5, last.Entities.Count);
    }

    // ───────────── FakeDataverse plumbing ─────────────

    [Fact]
    public async Task Client_HonoursCancellation_AndCountsRecords()
    {
        var dataverse = SelectFixture.Create();
        await using var client = await dataverse.CreatePool().GetClientAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.RetrieveMultipleAsync(new FetchExpression("<fetch><entity name=\"account\" /></fetch>"), cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.ExecuteAsync(new RetrieveTotalRecordCountRequest { EntityNames = ["account"] }, cts.Token));

        var count = (RetrieveTotalRecordCountResponse)await client.ExecuteAsync(new RetrieveTotalRecordCountRequest { EntityNames = ["account", "contact"] }, CancellationToken.None);
        Assert.Equal(10L, count.EntityRecordCountCollection["account"]);
        Assert.Equal(5L, count.EntityRecordCountCollection["contact"]);
    }
}
