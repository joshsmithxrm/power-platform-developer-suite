using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

[Trait("Category", "Unit")]
public sealed class FormMoveRowSpanRegressionTests
{
    public static IEnumerable<object?[]> Moves()
    {
        foreach (var service in new[] { false, true })
        {
            foreach (var scenario in new[] { "source-covered-standalone", "source-spanning-sibling", "target-covered-anchor", "target-covered-append", "same-section-source", "same-section-target", "retry-covered-source", "retry-covered-target" })
                yield return [scenario, service, true, false, null];
            foreach (var scenario in new[] { "append-after-expired", "before-span-start", "source-after-expired", "anchor-after-expired", "source-unit-sibling", "same-section-before-span", "retry-safe-reposition", "source-covered-mixed", "source-after-spanning-sibling" })
                yield return [scenario, service, false, false, null];
            foreach (var scenario in new[] { "same-section-no-position", "already-positioned-retry" })
                yield return [scenario, service, false, true, null];
            foreach (var span in new[] { "rowspan", "colspan" }) yield return ["source-unit-sibling", service, true, false, span];
        }
    }

    [Theory]
    [MemberData(nameof(Moves))]
    [Trait("AC", "AC-67")]
    [Trait("AC", "AC-68")]
    [Trait("AC", "AC-69")]
    public async Task Move_PreservesGeometryAndRefusesCrossedSpans(string scenario, bool service, bool reject, bool noop, string? span)
    {
        using var cancel = new CancellationTokenSource();
        var (h, request) = Fixture(scenario, cancel.Token, span);
        var before = XDocument.Parse(h.Original); FormXmlValidator.Validate(before);
        var after = new XDocument(before); var original = after.ToString(SaveOptions.DisableFormatting); bool? changed = null;
        var error = service
            ? await Record.ExceptionAsync(() => h.Service.MoveFieldAsync(reject ? request with { SolutionUniqueName = "fixture_solution", Publish = true } : request, ct: cancel.Token))
            : Record.Exception(() => changed = FormXmlEditor.MoveField(after, request, request.FieldLogicalName, request.BeforeField));
        if (reject)
        {
            Assert.Equal(FormErrorCodes.UnsupportedLayout, Assert.IsType<PpdsException>(error).ErrorCode);
            Assert.Equal(original, after.ToString(SaveOptions.DisableFormatting)); Assert.Equal(h.Original, h.StateXml); Assert.Empty(h.Writes);
        }
        else
        {
            Assert.Null(error); if (service) after = XDocument.Parse(h.StateXml); else Assert.Equal(!noop, changed);
            FormXmlValidator.Validate(after); AssertAllCellsPreserved(before, after); AssertSpanCoveragePreserved(before, after);
            if (service) await h.Service.MoveFieldAsync(request, ct: cancel.Token);
            else Assert.False(FormXmlEditor.MoveField(after, request, request.FieldLogicalName, request.BeforeField));
            Assert.Equal(service && !noop ? 1 : 0, h.Writes.Count);
            if (noop) { Assert.Equal(original, after.ToString(SaveOptions.DisableFormatting)); Assert.Equal(h.Original, h.StateXml); }
        }
        Assert.Equal(0, h.SolutionRequests); Assert.Equal(0, h.PublishRequests);
        Assert.Equal(service ? reject ? 1 : 2 : 0, h.ReadRequests); Assert.Equal(service ? reject ? 1 : 2 : 0, h.MetadataCalls);
        Assert.All(h.ObservedTokens, token => Assert.Equal(cancel.Token, token)); if (service) h.AssertIsolation();
    }

    private static (ContributorFormHarness Harness, MoveFieldRequest Request) Fixture(string scenario, CancellationToken token, string? selectedSpan = null)
    {
        var from = "Other"; var to = "General"; string? before = null;
        var h = new ContributorFormHarness(token, transform: doc =>
        {
            var general = ContributorFormFixture.Section(doc, "General"); var other = ContributorFormFixture.Section(doc, "Other");
            general.SetAttributeValue("columns", "2"); other.SetAttributeValue("columns", "2");
            var name = Cell(doc, "name"); var phone = Cell(doc, "telephone1"); var a = name.Parent!; var c = phone.Parent!;
            var timeline = doc.Descendants("control").Single(control => (string?)control.Attribute("id") == "notescontrol").Parent!.Parent!;
            a.Remove(); c.Remove(); timeline.Remove(); a.SetAttributeValue("height", "17"); c.SetAttributeValue("height", "23");
            var unrelated = new XElement("section", general.Attributes().Where(x => x.Name != "id" && x.Name != "name").Select(x => new XAttribute(x)),
                new XAttribute("id", "{abababab-0000-0000-0000-000000000001}"), new XAttribute("name", "span_unrelated"),
                new XElement("labels", new XElement("label", new XAttribute("description", "Unrelated"), new XAttribute("languagecode", "1033"))),
                new XElement("rows", timeline, Enumerable.Range(0, 22).Select(_ => new XElement("row"))));
            general.Parent!.Add(unrelated);
            var g = general.Element("rows")!; var o = other.Element("rows")!;
            var priority = new XElement(phone); priority.SetAttributeValue("id", "{abababab-0000-0000-0000-000000000002}");
            priority.Element("control")!.SetAttributeValue("id", "span_priority"); priority.Element("control")!.SetAttributeValue("uniqueid", "{abababab-0000-0000-0000-000000000003}"); priority.Element("control")!.SetAttributeValue("datafieldname", ContributorFormFixture.Priority);
            var anchor = new XElement("row", priority);
            name.SetAttributeValue("rowspan", "2");
            switch (scenario)
            {
                case "source-covered-standalone": case "source-covered-mixed":
                    from = "General"; to = "Other"; a.Add(Spacer(1)); if (scenario == "source-covered-mixed") { general.SetAttributeValue("columns", "3"); c.Add(Spacer(2)); } g.Add(a, c); break;
                case "source-spanning-sibling": case "source-unit-sibling": case "source-after-spanning-sibling":
                    from = "General"; to = "Other"; name.SetAttributeValue("rowspan", "1"); var sibling = Spacer(1); sibling.SetAttributeValue("rowspan", scenario == "source-unit-sibling" ? "1" : "2"); if (scenario == "source-after-spanning-sibling") c.AddFirst(sibling); else c.Add(sibling); g.Add(a, c, new XElement("row")); break;
                case "target-covered-anchor":
                    before = ContributorFormFixture.Priority; a.Add(Spacer(1)); g.Add(a, anchor); o.Add(c); break;
                case "target-covered-append": name.SetAttributeValue("rowspan", "3"); g.Add(a, new XElement("row", Spacer(1))); o.Add(c); break;
                case "same-section-source": case "retry-covered-source":
                    from = scenario == "same-section-source" ? "General" : "Other"; before = "name"; g.Add(a, c, anchor); break;
                case "same-section-target": case "retry-covered-target":
                    from = scenario == "same-section-target" ? "General" : "Other"; before = ContributorFormFixture.Priority; g.Add(a, anchor, c); break;
                case "append-after-expired": g.Add(a, new XElement("row", Spacer(1))); o.Add(c); break;
                case "before-span-start": before = "name"; g.Add(a, new XElement("row", Spacer(1))); o.Add(c); break;
                case "source-after-expired": from = "General"; to = "Other"; g.Add(a, new XElement("row", Spacer(1)), c); break;
                case "anchor-after-expired": before = ContributorFormFixture.Priority; g.Add(a, new XElement("row", Spacer(1)), anchor); o.Add(c); break;
                case "same-section-before-span": from = "General"; before = "name"; g.Add(a, new XElement("row", Spacer(1)), c); break;
                case "retry-safe-reposition": before = ContributorFormFixture.Priority; g.Add(a, new XElement("row", Spacer(1)), anchor, c); break;
                case "same-section-no-position": from = "General"; g.Add(a, c); break;
                case "already-positioned-retry": before = ContributorFormFixture.Priority; g.Add(a, c, anchor); break;
                default: throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            if (selectedSpan != null) phone.SetAttributeValue(selectedSpan, "2");
            if (selectedSpan == "colspan") general.SetAttributeValue("columns", "3");
        }) { AllowExplicitSideEffects = true };
        // Fixture precondition from Microsoft's published occupancy model: explicit cells plus
        // prior downward reservations must fit. Only the deliberate invalid-append case may
        // have a span extending beyond existing rows. This is not the product guard under test.
        foreach (var section in XDocument.Parse(h.Original).Descendants("section"))
        {
            var rows = section.Element("rows")!.Elements("row").ToArray(); var reservations = new List<(int Width, int Left)>();
            var width = int.Parse(section.Attribute("columns")!.Value);
            for (var index = 0; index < rows.Length; index++)
            {
                var cells = rows[index].Elements("cell").ToArray();
                Assert.True(cells.Sum(cell => (int?)cell.Attribute("colspan") ?? 1) + reservations.Sum(r => r.Width) <= width);
                reservations = reservations.Where(r => r.Left > 1).Select(r => (r.Width, r.Left - 1)).ToList();
                foreach (var cell in cells)
                {
                    var span = (int?)cell.Attribute("rowspan") ?? 1;
                    if (scenario != "target-covered-append") Assert.True(span <= rows.Length - index);
                    if (span > 1) reservations.Add(((int?)cell.Attribute("colspan") ?? 1, span - 1));
                }
            }
        }
        return (h, new MoveFieldRequest("account", "Fixture", "telephone1", from, to, before));
    }

    private static XElement Spacer(int id) => new("cell", new XAttribute("id", "{bada0000-0000-0000-0000-" + id.ToString("x12") + "}"), new XAttribute("userspacer", "true"));
    private static XElement Cell(XDocument doc, string field) => doc.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == field).Parent!;
    private static string? Label(XElement section) => (string?)section.Element("labels")?.Element("label")?.Attribute("description");
    private static void AssertAllCellsPreserved(XDocument before, XDocument after)
    {
        Assert.Equal(before.Descendants("cell").Count(), after.Descendants("cell").Count());
        foreach (var cell in before.Descendants("cell")) Assert.True(XNode.DeepEquals(cell, after.Descendants("cell").Single(other => (string?)other.Attribute("id") == (string?)cell.Attribute("id"))));
        Assert.True(XNode.DeepEquals(before.Root!.Element("controlDescriptions"), after.Root!.Element("controlDescriptions")));
        Assert.True(XNode.DeepEquals(ContributorFormFixture.Section(before, "Unrelated"), ContributorFormFixture.Section(after, "Unrelated")));
    }
    private static void AssertSpanCoveragePreserved(XDocument before, XDocument after)
    {
        var movedId = (string?)Cell(before, "telephone1").Attribute("id");
        string[] Coverage(XElement cell) => cell.Parent!.ElementsAfterSelf("row").Take(int.Parse(cell.Attribute("rowspan")!.Value) - 1)
            .Select(row => string.Join("|", row.Elements("cell").Where(c => (string?)c.Attribute("id") != movedId).Select(c => (string?)c.Attribute("id")))).ToArray();
        foreach (var cell in before.Descendants("cell").Where(c => c.Attribute("rowspan") != null))
            Assert.Equal(Coverage(cell), Coverage(after.Descendants("cell").Single(c => (string?)c.Attribute("id") == (string?)cell.Attribute("id"))));
    }
}
