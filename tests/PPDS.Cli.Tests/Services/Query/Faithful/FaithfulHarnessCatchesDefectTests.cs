using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;
using Xunit.Sdk;
using static PPDS.Cli.Tests.Services.Query.Faithful.FaithfulSelect;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// The acceptance criteria of #1639: an engine that drops a condition, a TOP or an alias fails the SELECT tests offline.
/// Each test runs a statement from <see cref="FaithfulSelectTests"/> twice with the same assertion: once as the engine
/// translates it (passes), and once with the engine's FetchXML changed the way the defect would change it (fails).
/// The old harness returned every canned row whatever the FetchXML said, so the second run passed as well.
/// The engine's own TOP limit is the exception: see <see cref="DroppedTop_FailsTheTest"/>.
/// </summary>
[Trait("Category", "Unit")]
public class FaithfulHarnessCatchesDefectTests
{
    [Fact]
    public async Task DroppedCondition_FailsTheTest()
    {
        const string sql = "SELECT accountnumber FROM account WHERE name = 'alpha' OR numberofemployees = 100";
        const string expected = "k1,k2,k8";

        await AssertKeysAsync(sql, expected);

        // The engine loses one condition of the OR (the shape of #1617).
        var dropped = SelectFixture.Create();
        dropped.Rewrite = fetch => Edit(fetch, doc => doc.Descendants("condition").First(c => (string?)c.Attribute("attribute") == "numberofemployees").Remove());
        await Assert.ThrowsAsync<TrueException>(() => AssertKeysAsync(sql, expected, dataverse: dropped));

        // The engine loses the whole filter (the shape of #1612 and #1613).
        var noFilter = SelectFixture.Create();
        noFilter.Rewrite = fetch => Edit(fetch, doc => doc.Descendants("filter").Remove());
        await Assert.ThrowsAsync<TrueException>(() => AssertKeysAsync(sql, expected, dataverse: noFilter));
    }

    [Fact]
    public async Task InvertedCondition_FailsTheTest()
    {
        const string sql = "SELECT accountnumber FROM account WHERE numberofemployees > 5";
        const string expected = "k2,k6,k8,k10";

        await AssertKeysAsync(sql, expected);

        var inverted = SelectFixture.Create();
        inverted.Rewrite = fetch => fetch.Replace("operator=\"gt\"", "operator=\"le\"");
        await Assert.ThrowsAsync<TrueException>(() => AssertKeysAsync(sql, expected, dataverse: inverted));
    }

    [Fact]
    public async Task DroppedTop_FailsTheTest()
    {
        // accountnumber breaks the k1/k7 tie on numberofemployees: the order of tied rows is not observed.
        const string sql = "SELECT TOP 3 accountnumber FROM account ORDER BY numberofemployees DESC, accountnumber";
        const string expected = "k8,k10,k2";

        await AssertKeysAsync(sql, expected, ordered: true);

        // An engine that loses the TOP runs the statement without it (the shape of #1619, where TOP (n) is ignored):
        // the fake returns every row, so the same expectation fails.
        await Assert.ThrowsAsync<TrueException>(() =>
            AssertKeysAsync("SELECT accountnumber FROM account ORDER BY numberofemployees DESC, accountnumber", expected, ordered: true));

        // The fake honours a TOP in the FetchXML (rule R10). The planner also applies TOP as the scan's own row limit,
        // so a TOP lost only from the FetchXML changes no result, and the test rightly still passes.
        var fetchOnly = SelectFixture.Create();
        fetchOnly.Rewrite = fetch => Edit(fetch, doc =>
        {
            doc.Root!.Attribute("top")?.Remove();
            doc.Root!.Attribute("count")?.Remove();
        });
        await AssertKeysAsync(sql, expected, ordered: true, dataverse: fetchOnly);
        Assert.All(fetchOnly.FetchXml, f => Assert.DoesNotContain("count=\"3\"", f));
    }

    [Fact]
    public async Task DroppedOrderBy_FailsTheTest()
    {
        const string sql = "SELECT accountnumber FROM account ORDER BY numberofemployees DESC, accountnumber";
        const string expected = "k8,k10,k2,k6,k1,k7,k4,k5,k3,k9";

        await AssertKeysAsync(sql, expected, ordered: true);

        var noOrder = SelectFixture.Create();
        noOrder.Rewrite = fetch => Edit(fetch, doc => doc.Descendants("order").Remove());
        await Assert.ThrowsAsync<TrueException>(() => AssertKeysAsync(sql, expected, ordered: true, dataverse: noOrder));
    }

    [Fact]
    public async Task DroppedOrWrongAlias_FailsTheTest()
    {
        const string sql = "SELECT accountnumber AS k, name AS n FROM account WHERE accountnumber = 'k1'";

        static async Task AssertAliases(FakeDataverse dataverse)
        {
            var result = await RunAsync(sql, dataverse);
            Assert.Equal(new object?[] { "k1" }, Column(result, "k"));
            Assert.Equal(new object?[] { "Alpha" }, Column(result, "n"));
        }

        await AssertAliases(SelectFixture.Create());

        var noAlias = SelectFixture.Create();
        noAlias.Rewrite = fetch => Regex.Replace(fetch, " alias=\"n\"", "");
        await Assert.ThrowsAsync<EqualException>(() => AssertAliases(noAlias));

        var wrongAlias = SelectFixture.Create();
        // The two aliases swapped.
        wrongAlias.Rewrite = fetch => fetch.Replace("alias=\"k\"", "alias=\"_\"").Replace("alias=\"n\"", "alias=\"k\"").Replace("alias=\"_\"", "alias=\"n\"");
        await Assert.ThrowsAsync<EqualException>(() => AssertAliases(wrongAlias));
    }

    private static string Edit(string fetchXml, Action<XDocument> change)
    {
        var doc = XDocument.Parse(fetchXml);
        change(doc);
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
