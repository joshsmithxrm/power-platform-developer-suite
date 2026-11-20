using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Xunit;

namespace PPDS.Cli.Tests.Services.Query.Faithful;

/// <summary>
/// Forms no recorded case sends, each of which the evaluator once evaluated or answered with a raw exception. Each must
/// now end in a "not observed" refusal (or, where one is observed, the Dataverse fault), never in rows and never in a raw
/// exception. LiveObservationsOwed.md lists the live run that would let each one be evaluated.
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
            .Column("multi", FakeColumnType.MultiChoice).Column("parentid", FakeColumnType.Lookup)
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

    public static TheoryData<string, string> Probes() => new()
    {
        // distinct, beyond D01-D08 (D07 is the only distinct with an order: a whole number)
        { "distinct with an order returning a float", Fetch("<attribute name=\"flt\" /><order attribute=\"flt\" />", " distinct=\"true\"") },
        { "distinct with an order returning a money column", Fetch("<attribute name=\"money\" /><order attribute=\"money\" />", " distinct=\"true\"") },
        { "distinct with an order returning a date-time", Fetch("<attribute name=\"dt\" /><order attribute=\"dt\" />", " distinct=\"true\"") },
        { "distinct ordered by an unselected column", Fetch("<attribute name=\"k\" /><order attribute=\"name\" />", " distinct=\"true\"") },
        { "distinct with a link attribute", Fetch("<attribute name=\"k\" /><link-entity name=\"p\" from=\"pid\" to=\"pid\" link-type=\"inner\" alias=\"l\"><attribute name=\"name\" /></link-entity>", " distinct=\"true\"") },
        { "distinct with no attributes", Fetch("", " distinct=\"true\"") },
        // aggregates
        { "an aggregate fetch with all-attributes", Fetch("<all-attributes />", " aggregate=\"true\"") },
        { "group by over no rows", Where("<condition attribute=\"k\" operator=\"eq\" value=\"99\" />", "<attribute name=\"k\" groupby=\"true\" alias=\"g\" /><attribute name=\"k\" aggregate=\"count\" alias=\"n\" />").Replace("<fetch>", "<fetch aggregate=\"true\">") },
        // fetch attributes no replayed case sends
        { "mapping=logical", Fetch("<attribute name=\"k\" />", " mapping=\"logical\"") },
        { "distinct=false", Fetch("<attribute name=\"k\" />", " distinct=\"false\"") },
        { "aggregate=false", Fetch("<attribute name=\"k\" />", " aggregate=\"false\"") },
        // invented values
        { "an order on createdon in a plain query", Fetch("<attribute name=\"k\" /><order attribute=\"createdon\" />") },
        { "a statuscode condition", Where("<condition attribute=\"statuscode\" operator=\"eq\" value=\"1\" />") },
        { "distinct on statecode", Fetch("<attribute name=\"statecode\" />", " distinct=\"true\"") },
        // LIKE forms
        { "LIKE ''", Where("<condition attribute=\"name\" operator=\"like\" value=\"\" />") },
        { "LIKE [a-]", Where("<condition attribute=\"name\" operator=\"like\" value=\"[a-]%\" />") },
        { "LIKE [-a]", Where("<condition attribute=\"name\" operator=\"like\" value=\"[-a]%\" />") },
        { "LIKE [^^]", Where("<condition attribute=\"name\" operator=\"like\" value=\"[^^]%\" />") },
        { "LIKE [z-a]% (raw RegexParseException before)", Where("<condition attribute=\"name\" operator=\"like\" value=\"[z-a]%\" />") },
        { "LIKE with an emoji (raw ArgumentException before)", Where("<condition attribute=\"name\" operator=\"like\" value=\"\U0001F600%\" />") },
        // numbers
        { "a 34-digit decimal", Where("<condition attribute=\"dec\" operator=\"eq\" value=\"1.250000000000000000000000000000001\" />") },
        { "a 400-digit float", Where($"<condition attribute=\"flt\" operator=\"lt\" value=\"{new string('9', 400)}\" />") },
        { "a 32-digit whole number (raw OverflowException before)", Where($"<condition attribute=\"k\" operator=\"eq\" value=\"{new string('9', 32)}\" />") },
        { "a 32-digit decimal (raw OverflowException before)", Where($"<condition attribute=\"dec\" operator=\"eq\" value=\"{new string('9', 32)}\" />") },
        { "a full-width digit (raw FormatException before)", Where("<condition attribute=\"k\" operator=\"eq\" value=\"1５\" />") },
        // paging and links
        { "a paging cookie for another page", Fetch("<attribute name=\"k\" /><order attribute=\"k\" />", " count=\"2\" page=\"2\" paging-cookie=\"&lt;cookie page=&quot;5&quot; /&gt;\"") },
        { "a link to an empty table", Fetch("<attribute name=\"k\" /><link-entity name=\"e\" from=\"k\" to=\"k\" link-type=\"outer\" alias=\"x\" />") },
        { "a link with only one key empty (Name = '' fault before)", Fetch("<attribute name=\"k\" /><link-entity name=\"p\" from=\"\" to=\"pid\" link-type=\"outer\" alias=\"x\" />") },
        // date-time and input
        { "a local date-time at the end of the range (raw ArgumentOutOfRangeException before)", Where("<condition attribute=\"dt\" operator=\"eq\" value=\"9999-12-31T23:59:59\" />") },
        { "input that is not XML (raw XmlException before)", "not xml" },
        { "empty input (raw XmlException before)", "" },
    };

    [Theory]
    [MemberData(nameof(Probes))]
    public void AFormNoRecordedCaseSends_IsRefusedAsNotObserved(string form, string fetchXml)
    {
        var error = Record.Exception(() => Dataverse().RetrieveMultiple(fetchXml));

        Assert.True(error is NotSupportedException { Message: var m } && m.Contains("not observed", StringComparison.Ordinal)
                    || error is FaultException<OrganizationServiceFault>,
            $"{form}: expected a \"not observed\" refusal, got {(error is null ? "rows" : error.GetType().Name + ": " + error.Message)}");
    }
}
