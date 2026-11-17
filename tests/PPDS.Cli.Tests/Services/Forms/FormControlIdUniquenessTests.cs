using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>
/// A column placed more than once on a form gets a distinct control <c>id</c> per placement
/// (<c>name</c>, <c>name1</c>, <c>name2</c>…), never a second control with the same id, and a suffixed
/// id never takes the logical name of another column of the table.
/// </summary>
[Trait("Category", "Unit")]
[Trait("AC", "AC-72")]
public sealed class FormControlIdUniquenessTests
{
    private const string TextBox = "{4273EDBD-AC1D-40d3-9FB2-095C621B552D}";

    private static string[] ControlIds(XDocument doc, string field) => doc.Descendants("control")
        .Where(c => (string?)c.Attribute("datafieldname") == field).Select(c => (string)c.Attribute("id")!).ToArray();

    private static string IdIn(XDocument doc, string section, string field) => (string)ContributorFormFixture.Section(doc, section)
        .Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == field).Attribute("id")!;

    private static void AssertControlIdsUnique(XDocument doc)
    {
        var ids = doc.Descendants("control").Select(c => (string?)c.Attribute("id")).Where(id => !string.IsNullOrEmpty(id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static XElement EmptySection(string label, string id) => new("section", new XAttribute("id", "{" + id + "}"),
        new XAttribute("name", "section_" + label.ToLowerInvariant()), new XAttribute("showlabel", "1"),
        new XElement("labels", new XElement("label", new XAttribute("description", label), new XAttribute("languagecode", "1033"))),
        new XElement("rows"));

    private static XElement Area(string area, string controlId, string field) => new(area,
        new XAttribute("id", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}"), new XElement("rows", new XElement("row", new XElement("cell",
            new XAttribute("id", "{abababab-abab-abab-abab-abababababab}"),
            new XElement("control", new XAttribute("id", controlId), new XAttribute("datafieldname", field),
                new XAttribute("classid", ControlTypeFixture.TypeId))))));

    // ── add-field (CLI service path) ──────────────────────────────────────

    [Fact]
    public async Task Add_FieldToSecondSection_GetsSuffixedId_ExistingControlUntouched_AndValidates()
    {
        // Base fixture: telephone1 is already in "Other" with control id "telephone1".
        var h = new ContributorFormHarness(CancellationToken.None);
        var existing = XDocument.Parse(h.Original).Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1");

        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]));

        Assert.Equal(new[] { "telephone1" }, result.AddedFields);
        Assert.Equal(new[] { "My Tab / Other" }, Assert.Single(result.OtherPlacements!).Locations);
        Assert.Equal(new[] { new AddedFieldControl("telephone1", "telephone11") }, result.AddedControls);
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal("telephone11", IdIn(written, "General", "telephone1"));
        var other = ContributorFormFixture.Section(written, "Other").Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1");
        Assert.True(XNode.DeepEquals(existing, other), "the existing placement must be unchanged");
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written); // round trip: our own validator accepts what the writer produced
    }

    [Fact]
    public async Task Add_ThirdPlacement_TakesNextFreeSuffix_AndRetryIsStillNoOp()
    {
        var h = new ContributorFormHarness(CancellationToken.None, "phone-in-two-sections", transform: doc =>
            // Make the "Third" placement use the designer-style first suffix so the next one must be 2.
            ContributorFormFixture.Section(doc, "Third").Descendants("control").Single().SetAttributeValue("id", "telephone11"));

        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]));
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal("telephone12", IdIn(written, "General", "telephone1"));
        Assert.Equal("telephone1", IdIn(written, "Other", "telephone1"));
        Assert.Equal("telephone11", IdIn(written, "Third", "telephone1"));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);

        // Already-present detection keys on datafieldname, not on id: the suffixed control still counts.
        var retry = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["TELEPHONE1"]));
        Assert.Equal(new[] { "telephone1" }, retry.AlreadyPresentFields);
        Assert.Empty(retry.OtherPlacements!);
        Assert.Empty(retry.AddedControls!);
        Assert.Single(h.Writes);
    }

    [Fact]
    public async Task Add_AuthoredIdLeavesBareNameFree_AndTheNextPlacementIsSuffixed()
    {
        // "name" lives in General with the authored id "authored_name_control", so the bare id "name" is free for
        // the second placement; the third placement must then not reuse it.
        var h = new ContributorFormHarness(CancellationToken.None, "phone-in-two-sections");
        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Other", ["name"]));
        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Third", ["name"]));
        var written = XDocument.Parse(h.StateXml);
        Assert.Equal("authored_name_control", IdIn(written, "General", "name"));
        Assert.Equal("name", IdIn(written, "Other", "name"));
        Assert.Equal("name1", IdIn(written, "Third", "name"));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("footer")]
    public async Task Add_FieldInHeaderOrFooter_PrefixedIdDoesNotForceASuffix_ButABareIdDoes(string area)
    {
        var p = ContributorFormFixture.Priority;
        var prefixed = new ContributorFormHarness(CancellationToken.None, transform: doc => doc.Root!.Add(Area(area, area + "_" + p, p)));
        var result = await prefixed.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", [p]));
        Assert.Equal(new[] { area == "header" ? "Header" : "Footer" }, Assert.Single(result.OtherPlacements!).Locations);
        await prefixed.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Other", [p]));
        var written = XDocument.Parse(prefixed.StateXml);
        Assert.Equal(p, IdIn(written, "General", p));
        Assert.Equal(p + "1", IdIn(written, "Other", p));
        Assert.Equal(area + "_" + p, (string?)written.Root!.Element(area)!.Descendants("control").Single().Attribute("id"));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);

        var bare = new ContributorFormHarness(CancellationToken.None, transform: doc => doc.Root!.Add(Area(area, p, p)));
        await bare.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", [p]));
        var bareWritten = XDocument.Parse(Assert.Single(bare.Writes));
        Assert.Equal(p + "1", IdIn(bareWritten, "General", p));
        Assert.Equal(p, (string?)bareWritten.Root!.Element(area)!.Descendants("control").Single().Attribute("id"));
        AssertControlIdsUnique(bareWritten);
    }

    [Fact]
    public async Task Add_NonFieldControlHoldingTheName_ForcesASuffixOnTheFirstPlacement()
    {
        // A sub-grid (any unbound control) whose id equals the column's logical name occupies that id.
        var p = ContributorFormFixture.Priority;
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc => ContributorFormFixture.Section(doc, "Other")
            .Element("rows")!.Add(new XElement("row", new XElement("cell", new XAttribute("id", "{12121212-1212-1212-1212-121212121212}"),
                new XElement("control", new XAttribute("id", p), new XAttribute("classid", ClassIdResolver.SubgridClassId),
                    new XAttribute("indicationOfSubgrid", "true"))))));
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", [p]));
        Assert.Empty(result.OtherPlacements!);
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal(p + "1", IdIn(written, "General", p));
        AssertControlIdsUnique(written);
    }

    // ── S2: suffixed ids never take another column's logical name ──────────

    [Fact]
    public async Task Add_SuffixSkipsAnotherColumnsLogicalName_InBothOrders_AndRetryIsNoOp()
    {
        // telephone1 is in Other (id telephone1). The table also has real columns telephone11 and telephone12.
        string[] extra = ["telephone11", "telephone12"];

        // Suffixed placement first: telephone1 to General must skip telephone11 and telephone12 (real columns).
        var first = new ContributorFormHarness(CancellationToken.None, extraColumns: extra);
        var added = await first.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]));
        Assert.Equal(new[] { new AddedFieldControl("telephone1", "telephone13") }, added.AddedControls);
        // ...and the real column telephone11 added later keeps its bare name.
        await first.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone11"]));
        var firstXml = XDocument.Parse(first.StateXml);
        Assert.Equal("telephone13", IdIn(firstXml, "General", "telephone1"));
        Assert.Equal("telephone11", IdIn(firstXml, "General", "telephone11"));
        AssertControlIdsUnique(firstXml);
        FormXmlValidator.Validate(firstXml);
        var retry = await first.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1", "TELEPHONE11"]));
        Assert.Equal(new[] { "telephone1", "telephone11" }, retry.AlreadyPresentFields);
        Assert.Equal(2, first.Writes.Count);

        // Real column first, in the same batch: telephone11 keeps its name, the second telephone1 skips 11 and 12.
        var batch = new ContributorFormHarness(CancellationToken.None, extraColumns: extra);
        var both = await batch.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone11", "telephone1"]));
        Assert.Equal(new[] { new AddedFieldControl("telephone11", "telephone11"), new AddedFieldControl("telephone1", "telephone13") },
            both.AddedControls);
        AssertControlIdsUnique(XDocument.Parse(batch.StateXml));
    }

    [Fact]
    public async Task Add_RealColumnWhoseNameIsAlreadyASuffixedControlId_GetsASuffixItself()
    {
        // Reverse order with an id already written (e.g. by an earlier version, or authored): a control bound to
        // telephone1 already holds id "telephone11". Existing ids are never renamed, so the real column telephone11
        // cannot use its bare name and is suffixed instead (telephone111). Accepted and documented behaviour.
        var h = new ContributorFormHarness(CancellationToken.None, "phone-in-two-sections", extraColumns: ["telephone11"],
            transform: doc => ContributorFormFixture.Section(doc, "Third").Descendants("control").Single().SetAttributeValue("id", "telephone11"));
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone11"]));
        Assert.Equal(new[] { new AddedFieldControl("telephone11", "telephone111") }, result.AddedControls);
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal("telephone11", IdIn(written, "Third", "telephone1"));
        AssertControlIdsUnique(written);
    }

    // ── legacy reorder-fields ─────────────────────────────────────────────

    [Theory]
    [InlineData("header")]
    [InlineData("footer")]
    public async Task LegacyReorder_NewCellForFieldOnlyInHeaderOrFooter_GetsUniqueId(string area)
    {
        var p = ContributorFormFixture.Priority;
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc => doc.Root!.Add(Area(area, p, p)));
        await h.Service.ReorderFieldsWithResultAsync(new("account", "Fixture", "General", ["name", p]));
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal(p + "1", IdIn(written, "General", p));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);
    }

    private static XDocument FooterForm(string footerField) => new(new XElement("form",
        new XElement("tabs", new XElement("tab", new XAttribute("id", "{10000000-0000-0000-0000-000000000001}"),
            new XElement("columns", new XElement("column", new XElement("sections",
                new XElement("section", new XAttribute("id", "{20000000-0000-0000-0000-000000000002}"), new XAttribute("name", "general"),
                    new XElement("rows"))))))),
        Area("footer", footerField, footerField)));

    private static (string, string, string) Resolve(string field) => (field, TextBox, field);

    [Theory]
    [InlineData("x", "x1")]
    [InlineData("x1", "x")]
    public void LegacyReorder_ReservesIdsOfEarlierNewCells_WithoutColumnNames(string firstField, string secondField)
    {
        string[] fields = [firstField, secondField];
        // Footer holds bare id "x"; neither x nor x1 is in a section. Without column names, x's candidate is x1, which
        // the other new cell also wants. Planned-but-unattached cells must reserve their ids.
        var doc = FooterForm("x");
        FormXmlEditor.ReorderFields(doc, "general", fields, Resolve);
        var ids = doc.Descendants("section").Single().Descendants("control").ToDictionary(c => (string)c.Attribute("datafieldname")!, c => (string)c.Attribute("id")!);
        // [x, x1]: x takes x1, so x1 must move on to x11. [x1, x]: x1 keeps x1, so x must move on to x2.
        Assert.Equal(fields[0] == "x" ? "x1" : "x2", ids["x"]);
        Assert.Equal(fields[0] == "x" ? "x11" : "x1", ids["x1"]);
        AssertControlIdsUnique(doc);
    }

    [Theory]
    [InlineData("x", "x1")]
    [InlineData("x1", "x")]
    public void LegacyReorder_WithColumnNames_SuffixSkipsTheOtherColumn_InBothOrders(string firstField, string secondField)
    {
        string[] fields = [firstField, secondField];
        var doc = FooterForm("x");
        FormXmlEditor.ReorderFields(doc, "general", fields, Resolve, ["x", "x1"]);
        var ids = doc.Descendants("section").Single().Descendants("control").ToDictionary(c => (string)c.Attribute("datafieldname")!, c => (string)c.Attribute("id")!);
        Assert.Equal("x2", ids["x"]);
        Assert.Equal("x1", ids["x1"]);
        AssertControlIdsUnique(doc);
    }

    [Fact]
    public void LegacyReorder_IdOfACellItDrops_IsFreeForTheNewCell()
    {
        // The selected section holds a second placement of x with id "x1"; the reorder lists only the real column x1,
        // so that cell is dropped and "x1" does not appear in the written XML; the new x1 control takes its bare name.
        var doc = FooterForm("x");
        doc.Descendants("section").Single().Element("rows")!.Add(new XElement("row", new XElement("cell",
            new XAttribute("id", "{30000000-0000-0000-0000-000000000003}"),
            new XElement("control", new XAttribute("id", "x1"), new XAttribute("datafieldname", "x"), new XAttribute("classid", TextBox)))));
        FormXmlEditor.ReorderFields(doc, "general", ["x1"], Resolve, ["x", "x1"]);
        var control = doc.Descendants("section").Single().Descendants("control").Single();
        Assert.Equal(("x1", "x1"), ((string)control.Attribute("datafieldname")!, (string)control.Attribute("id")!));
        AssertControlIdsUnique(doc);
    }

    // ── move / existing duplicates ────────────────────────────────────────

    [Fact]
    public async Task MoveField_KeepsTheSuffixedIdOfTheMovedControl()
    {
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc => ContributorFormFixture.Section(doc, "General").Parent!.Add(
            EmptySection("Fourth", "f0f0f0f0-f0f0-f0f0-f0f0-f0f0f0f0f0f0"), EmptySection("Fifth", "f1f1f1f1-f1f1-f1f1-f1f1-f1f1f1f1f1f1")));
        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Fourth", ["telephone1"]));
        await h.Service.MoveFieldAsync(new("account", "Fixture", "telephone1", "Fourth", "Fifth"));
        Assert.Equal(2, h.Writes.Count);
        var written = XDocument.Parse(h.StateXml);
        // Moving never renames: the moved control keeps the id it was created with; the original is untouched.
        Assert.Equal("telephone11", IdIn(written, "Fifth", "telephone1"));
        Assert.Equal("telephone1", IdIn(written, "Other", "telephone1"));
        Assert.DoesNotContain(ContributorFormFixture.Section(written, "Fourth").Descendants("control"), _ => true);
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);
    }

    [Fact]
    public async Task ExistingDuplicateControlIds_AreNotRewritten_AndTheFormStaysEditable()
    {
        // Forms written by earlier add-field versions can already repeat a control id. They must stay editable
        // (no validator rejection), keep their existing ids, and the next placement still gets a free id.
        var h = new ContributorFormHarness(CancellationToken.None, "phone-in-two-sections", transform: doc =>
            ContributorFormFixture.Section(doc, "Third").Descendants("control").Single().SetAttributeValue("id", "telephone1"));
        var original = XDocument.Parse(h.Original);
        FormXmlValidator.Validate(original);
        XElement Phone(XDocument d, string s) => ContributorFormFixture.Section(d, s).Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1");

        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]));
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal("telephone11", IdIn(written, "General", "telephone1"));
        Assert.True(XNode.DeepEquals(Phone(original, "Other"), Phone(written, "Other")));
        Assert.True(XNode.DeepEquals(Phone(original, "Third"), Phone(written, "Third")));

        // Other writers still work on the duplicate-id form: legacy reorder of a section and a scoped removal.
        await h.Service.ReorderFieldsWithResultAsync(new("account", "Fixture", "Other", ["telephone1"]));
        await h.Service.RemoveFieldAsync(new("account", "Fixture", "telephone1", "Third"));
        Assert.Equal(3, h.Writes.Count);
        var final = XDocument.Parse(h.StateXml);
        Assert.Equal(new[] { "telephone1", "telephone11" }, ControlIds(final, "telephone1").Order(StringComparer.Ordinal));
        AssertControlIdsUnique(final);
    }

    // ── sub-grids (two-directional uniqueness) ────────────────────────────

    [Fact]
    public void AddSubgrid_RejectsAnIdAFieldControlAlreadyUses()
    {
        var doc = FooterForm("subgrid_contacts");
        var ex = Assert.Throws<PpdsException>(() => FormXmlEditor.AddSubgrid(doc,
            new("account", "Fixture", "general", "Contacts", "contact", new Guid("eeeeeeee-0000-0000-0000-000000000001"))));
        Assert.Equal(FormErrorCodes.DuplicateSubgridName, ex.ErrorCode);
        Assert.Empty(doc.Descendants("section").Single().Descendants("control"));
    }

    // ── the id rule itself ────────────────────────────────────────────────

    [Theory]
    [InlineData(new string[0], "name")]
    [InlineData(new[] { "name" }, "name")] // only a control *without* an id attribute: see the missing-id row
    [InlineData(new[] { "name" }, "name1", true)]
    [InlineData(new[] { "name", "name1", "name3" }, "name2", true)]
    [InlineData(new[] { "NAME", "Name1" }, "name2", true)]
    [InlineData(new[] { "header_name" }, "name", true)]
    [InlineData(new[] { "" }, "name", true)] // empty id occupies nothing
    public void NextFieldControlId_IsDeterministicSmallestFreeSuffix(string[] existingIds, string expected, bool withIdAttribute = false)
    {
        var doc = new XDocument(new XElement("form", existingIds.Select(id => withIdAttribute
            ? new XElement("control", new XAttribute("id", id))
            : new XElement("control", new XAttribute("datafieldname", id)))));
        Assert.Equal(expected, FormXmlEditor.NextFieldControlId(doc, "name"));
    }

    [Theory]
    [InlineData(new[] { "name" }, new[] { "name1" }, "name2")]
    [InlineData(new[] { "name" }, new[] { "NAME1", "name2" }, "name3")]
    [InlineData(new[] { "name", "name2" }, new[] { "name1" }, "name3")]
    [InlineData(new string[0], new[] { "name1" }, "name")]
    public void NextFieldControlId_SkipsOtherColumnNames(string[] usedIds, string[] columns, string expected)
    {
        var used = new HashSet<string>(usedIds, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(expected, FormXmlEditor.NextFieldControlId(used, "name", columns));
    }

    [Fact]
    public void NextFieldControlId_ReservedPendingIdsAreHonoured()
    {
        // Callers that plan several cells before attaching them add each planned id to the used set.
        var used = FormXmlEditor.UsedControlIds(FooterForm("x"));
        var first = FormXmlEditor.NextFieldControlId(used, "x");
        used.Add(first);
        Assert.Equal(("x1", "x2"), (first, FormXmlEditor.NextFieldControlId(used, "x")));
    }
}
