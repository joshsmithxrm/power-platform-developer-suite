using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Forms no recorded case sends, each of which the evaluator once evaluated or answered with a raw exception. Each probe
/// sends only observed shapes until it reaches the refusal it names, and asserts that refusal: removing the refusal
/// makes its probe fail. LiveObservationsOwed.md lists the live run that would let each one be evaluated.
/// </summary>
[Trait("Category", "Unit")]
public class FetchXmlEvaluatorRefusalProbeTests
{
    private static FakeDataverse Dataverse()
    {
        var p = new FakeTable("p")
            .Column("k", FakeColumnType.Integer).Column("name", FakeColumnType.String).Column("flt", FakeColumnType.Double)
            .Column("dec", FakeColumnType.Decimal).Column("money", FakeColumnType.Money).Column("flag", FakeColumnType.Boolean)
            .Column("dt", FakeColumnType.DateTime).Column("donly", FakeColumnType.DateOnly).Column("createdon", FakeColumnType.DateTime)
            .Column("multi", FakeColumnType.MultiChoice).Column("parentid", FakeColumnType.Lookup).Column("allnull", FakeColumnType.Integer)
            .Choice("color", (1, "Red"), (2, "Blue"))
            .Choice("statecode", (0, "Active"), (1, "Inactive")).Choice("statuscode", (1, "Active"), (2, "Inactive"));
        for (var k = 1; k <= 4; k++)
        {
            p.Row(("k", k), ("name", "n" + k), ("flt", k * 1.5), ("dec", k * 1.25m), ("money", k * 10m), ("flag", k % 2 == 0),
                ("dt", new DateTime(2024, 1, k, 0, 0, 0, DateTimeKind.Utc)), ("donly", new DateTime(2024, 1, k)),
                ("createdon", new DateTime(2024, 1, 1, 0, 0, k, DateTimeKind.Utc)), ("color", new OptionSetValue(k % 2 + 1)),
                ("statecode", new OptionSetValue(0)), ("statuscode", new OptionSetValue(1)));
        }
        return new FakeDataverse().Add(p).Add(new FakeTable("e").Column("k", FakeColumnType.Integer));
    }

    private static string Fetch(string inner, string fetchAttributes = "")
        => $"<fetch{fetchAttributes}><entity name=\"p\">{inner}</entity></fetch>";

    private static string Where(string condition, string attributes = "<attribute name=\"k\" />")
        => Fetch($"{attributes}<filter>{condition}</filter>");

    private const string D = " distinct=\"true\"";
    private const string A = " aggregate=\"true\"";

    public static TheoryData<string, string> Probes() => new()
    {
        // distinct (D07, ordered by and returning a whole number, is the only distinct with an order)
        { "distinct with order returning a Double column", Fetch("<attribute name=\"k\" /><attribute name=\"flt\" /><order attribute=\"k\" />", D) },
        { "distinct with order returning a Money column", Fetch("<attribute name=\"k\" /><attribute name=\"money\" /><order attribute=\"k\" />", D) },
        { "distinct with order returning a DateTime column", Fetch("<attribute name=\"k\" /><attribute name=\"dt\" /><order attribute=\"k\" />", D) },
        { "distinct with order returning a Boolean column", Fetch("<attribute name=\"k\" /><attribute name=\"flag\" /><order attribute=\"k\" />", D) },
        { "distinct with order returning a DateOnly column", Fetch("<attribute name=\"k\" /><attribute name=\"donly\" /><order attribute=\"k\" />", D) },
        { "distinct ordered by a column it does not return", Fetch("<attribute name=\"k\" /><order attribute=\"name\" />", D) },
        { "distinct with link attributes", Fetch("<attribute name=\"k\" /><link-entity name=\"p\" from=\"parentid\" to=\"pid\" link-type=\"inner\" alias=\"l\"><attribute name=\"name\" /></link-entity>", D) },
        { "distinct with no attributes", Fetch("", D) },
        { "distinct with top", Fetch("<attribute name=\"k\" />", D + " top=\"2\"") },
        { "distinct without order on a Choice (statecode) column", Fetch("<attribute name=\"statecode\" />", D) },
        // orders no case can send
        { "ORDER BY a MultiChoice column", Fetch("<attribute name=\"k\" /><order attribute=\"multi\" />") },
        { "ORDER BY a Uniqueidentifier column", Fetch("<attribute name=\"k\" /><order attribute=\"pid\" />") },
        { "ORDER BY a Lookup column", Fetch("<attribute name=\"k\" /><order attribute=\"parentid\" />") },
        { "order on createdon, top none", Fetch("<attribute name=\"k\" /><order attribute=\"createdon\" />") },
        // aggregates
        { "all-attributes in an aggregate fetch", Fetch("<all-attributes />", A) },
        { "group by over no rows", Where("<condition attribute=\"k\" operator=\"eq\" value=\"99\" />", "<attribute name=\"k\" groupby=\"true\" alias=\"g\" /><attribute name=\"pid\" aggregate=\"count\" alias=\"n\" />").Replace("<fetch>", "<fetch aggregate=\"true\">") },
        { "aggregate sum of a Integer column over rows whose values are all NULL", Fetch("<attribute name=\"allnull\" aggregate=\"sum\" alias=\"s\" />", A) },
        { "aggregate max of a Integer column over rows whose values are all NULL", Fetch("<attribute name=\"allnull\" aggregate=\"max\" alias=\"m\" />", A) },
        // fetch attributes no replayed case sends
        { "fetch mapping=logical", Fetch("<attribute name=\"k\" />", " mapping=\"logical\"") },
        { "fetch distinct=false", Fetch("<attribute name=\"k\" />", " distinct=\"false\"") },
        { "fetch aggregate=false", Fetch("<attribute name=\"k\" />", " aggregate=\"false\"") },
        // invented option sets
        { "condition eq Choice (statuscode)", Where("<condition attribute=\"statuscode\" operator=\"eq\" value=\"1\" />") },
        // empty names (the record has no "Name = ''" fault)
        { "an empty attribute name", Fetch("<attribute name=\"\" />") },
        { "an empty attribute name", Where("<condition attribute=\"\" operator=\"eq\" value=\"1\" />") },
        { "an empty attribute name", Fetch("<attribute name=\"k\" /><order attribute=\"\" />") },
        { "an empty attribute name", Fetch("<attribute name=\"\" aggregate=\"countcolumn\" alias=\"c\" />", A) },
        // LIKE forms
        { "an empty value with operator like", Where("<condition attribute=\"name\" operator=\"like\" value=\"\" />") },
        { "an empty value with operator begins-with", Where("<condition attribute=\"name\" operator=\"begins-with\" value=\"\" />") },
        { "an empty value with operator ends-with", Where("<condition attribute=\"name\" operator=\"ends-with\" value=\"\" />") },
        { "an empty value with operator not-begin-with", Where("<condition attribute=\"name\" operator=\"not-begin-with\" value=\"\" />") },
        { "a '-' at the edge of, or a '^' inside, a []", Where("<condition attribute=\"name\" operator=\"like\" value=\"[a-]%\" />") },
        { "a '-' at the edge of, or a '^' inside, a []", Where("<condition attribute=\"name\" operator=\"like\" value=\"[-a]%\" />") },
        { "a '-' at the edge of, or a '^' inside, a []", Where("<condition attribute=\"name\" operator=\"like\" value=\"[^^]%\" />") },
        { "a reversed range", Where("<condition attribute=\"name\" operator=\"like\" value=\"[z-a]%\" />") },
        { "a character outside the Basic Multilingual Plane", Where("<condition attribute=\"name\" operator=\"like\" value=\"\U0001F600%\" />") },
        // numbers
        { "a Decimal value of more than 15 digits", Where("<condition attribute=\"dec\" operator=\"eq\" value=\"1.250000000000000000000000000000001\" />") },
        { "a Double value of more than 15 digits", Where($"<condition attribute=\"flt\" operator=\"lt\" value=\"{new string('9', 400)}\" />") },
        { "a Integer value of more than 15 digits", Where($"<condition attribute=\"k\" operator=\"eq\" value=\"{new string('9', 32)}\" />") },
        { "a Decimal value of more than 15 digits", Where($"<condition attribute=\"dec\" operator=\"eq\" value=\"{new string('9', 32)}\" />") },
        { "whole-number fault for a value of the form 9D", Where("<condition attribute=\"k\" operator=\"eq\" value=\"1５\" />") },
        // paging and links
        { "paging-cookie of another page", Fetch("<attribute name=\"k\" /><order attribute=\"k\" />", " count=\"2\" page=\"2\" paging-cookie=\"&lt;cookie page=&quot;5&quot; /&gt;\"") },
        { "paging-cookie of another page", Fetch("<attribute name=\"k\" /><order attribute=\"k\" />", " count=\"2\" page=\"2\" paging-cookie=\"&lt;cookie page=&quot;99999999999999999999&quot; /&gt;\"") },
        { "paging-cookie of another form", Fetch("<attribute name=\"k\" /><order attribute=\"k\" />", " count=\"2\" page=\"2\" paging-cookie=\"&lt;cookie page=&quot;01&quot; /&gt;\"") },
        { "paging-cookie of another form", Fetch("<attribute name=\"k\" /><order attribute=\"k\" />", " count=\"2\" page=\"2\" paging-cookie=\"&lt;cookie page=&quot;1&quot; /&gt;&#10;\"") },
        { "paging-cookie of another form", Fetch("<attribute name=\"k\" /><order attribute=\"k\" />", " count=\"2\" page=\"2\" paging-cookie=\"garbage\"") },
        { "a link to an empty table", Fetch("<attribute name=\"k\" /><link-entity name=\"e\" from=\"k\" to=\"k\" link-type=\"outer\" alias=\"x\" />") },
        { "a link-entity with only one of from and to empty", Fetch("<attribute name=\"k\" /><link-entity name=\"p\" from=\"\" to=\"pid\" link-type=\"outer\" alias=\"x\" />") },
        // date-time and input
        { "at the end of the supported range", Where("<condition attribute=\"dt\" operator=\"eq\" value=\"9999-12-31T23:59:59\" />") },
        { "input that is not XML", "not xml" },
        { "input that is not XML", "" },
    };

    [Theory]
    [MemberData(nameof(Probes))]
    public void AFormNoRecordedCaseSends_IsRefusedByItsOwnRefusal(string refusal, string fetchXml)
    {
        var error = Record.Exception(() => Dataverse().RetrieveMultiple(fetchXml));

        Assert.True(error is NotSupportedException { Message: var m } && m.Contains("not observed", StringComparison.Ordinal) && m.Contains(refusal, StringComparison.Ordinal),
            $"expected the refusal \"{refusal}\", got {(error is null ? "rows" : error.GetType().Name + ": " + error.Message)}");
    }
}
