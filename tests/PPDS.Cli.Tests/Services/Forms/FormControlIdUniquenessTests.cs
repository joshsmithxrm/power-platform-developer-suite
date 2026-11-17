using System.Xml.Linq;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>
/// A column placed more than once on a form gets a distinct control <c>id</c> per placement
/// (<c>name</c>, <c>name1</c>, <c>name2</c>…), never a second control with the same id.
/// </summary>
[Trait("Category", "Unit")]
[Trait("AC", "AC-72")]
public sealed class FormControlIdUniquenessTests
{
    private static string[] ControlIds(XDocument doc, string field) => doc.Descendants("control")
        .Where(c => (string?)c.Attribute("datafieldname") == field).Select(c => (string)c.Attribute("id")!).ToArray();

    private static void AssertControlIdsUnique(XDocument doc)
    {
        var ids = doc.Descendants("control").Select(c => (string?)c.Attribute("id")).OfType<string>().ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task Add_FieldToSecondSection_GetsSuffixedId_ExistingControlUntouched_AndValidates()
    {
        // Base fixture: telephone1 is already in "Other" with control id "telephone1".
        var h = new ContributorFormHarness(CancellationToken.None);
        var existing = XDocument.Parse(h.Original).Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1");

        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]));

        Assert.Equal(new[] { "telephone1" }, result.AddedFields);
        Assert.Equal(new[] { "My Tab / Other" }, Assert.Single(result.OtherPlacements!).Locations);
        var written = XDocument.Parse(Assert.Single(h.Writes));
        var general = ContributorFormFixture.Section(written, "General").Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1");
        Assert.Equal("telephone11", (string?)general.Attribute("id"));
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
        Assert.Equal(new[] { "telephone1", "telephone11", "telephone12" }, ControlIds(written, "telephone1").Order(StringComparer.Ordinal));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);

        // Already-present detection keys on datafieldname, not on id: the suffixed control still counts.
        var retry = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["TELEPHONE1"]));
        Assert.Equal(new[] { "telephone1" }, retry.AlreadyPresentFields);
        Assert.Empty(retry.OtherPlacements!);
        Assert.Single(h.Writes);
    }

    [Fact]
    public async Task Add_FieldWhoseExistingControlHasAnAuthoredId_KeepsBareLogicalName()
    {
        // "name" lives in General with the authored id "authored_name_control"; the bare id "name" is free.
        var h = new ContributorFormHarness(CancellationToken.None);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Other", ["name"]));
        Assert.Equal(new[] { "My Tab / General" }, Assert.Single(result.OtherPlacements!).Locations);
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal("name", (string?)ContributorFormFixture.Section(written, "Other").Descendants("control")
            .Single(c => (string?)c.Attribute("datafieldname") == "name").Attribute("id"));
        AssertControlIdsUnique(written);
    }

    [Fact]
    public async Task Add_FieldInHeader_HeaderPrefixedIdDoesNotForceASuffix_ButABareHeaderIdDoes()
    {
        var p = ContributorFormFixture.Priority;
        static Action<XDocument> Header(string controlId) => doc => doc.Root!.Add(
            new XElement("header", new XAttribute("id", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}"), new XElement("rows", new XElement("row", new XElement("cell",
                new XAttribute("id", "{abababab-abab-abab-abab-abababababab}"),
                new XElement("control", new XAttribute("id", controlId), new XAttribute("datafieldname", ContributorFormFixture.Priority),
                    new XAttribute("classid", ControlTypeFixture.TypeId)))))));

        var prefixed = new ContributorFormHarness(CancellationToken.None, transform: Header("header_" + p));
        var result = await prefixed.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", [p]));
        Assert.Equal(new[] { "Header" }, Assert.Single(result.OtherPlacements!).Locations);
        await prefixed.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Other", [p]));
        var written = XDocument.Parse(prefixed.StateXml);
        Assert.Equal(p, (string?)ContributorFormFixture.Section(written, "General").Descendants("control")
            .Single(c => (string?)c.Attribute("datafieldname") == p).Attribute("id"));
        Assert.Equal(p + "1", (string?)ContributorFormFixture.Section(written, "Other").Descendants("control")
            .Single(c => (string?)c.Attribute("datafieldname") == p).Attribute("id"));
        Assert.Equal("header_" + p, (string?)written.Root!.Element("header")!.Descendants("control").Single().Attribute("id"));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);

        var bare = new ContributorFormHarness(CancellationToken.None, transform: Header(p));
        await bare.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", [p]));
        var bareWritten = XDocument.Parse(Assert.Single(bare.Writes));
        Assert.Equal(p + "1", (string?)ContributorFormFixture.Section(bareWritten, "General").Descendants("control")
            .Single(c => (string?)c.Attribute("datafieldname") == p).Attribute("id"));
        AssertControlIdsUnique(bareWritten);
    }

    [Fact]
    public async Task LegacyReorder_NewCellForFieldOnlyInHeader_GetsUniqueId()
    {
        var p = ContributorFormFixture.Priority;
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc => doc.Root!.Add(
            new XElement("footer", new XAttribute("id", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}"), new XElement("rows", new XElement("row", new XElement("cell",
                new XAttribute("id", "{abababab-abab-abab-abab-abababababab}"),
                new XElement("control", new XAttribute("id", p), new XAttribute("datafieldname", p),
                    new XAttribute("classid", ControlTypeFixture.TypeId))))))));
        await h.Service.ReorderFieldsWithResultAsync(new("account", "Fixture", "General", ["name", p]));
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal(p + "1", (string?)ContributorFormFixture.Section(written, "General").Descendants("control")
            .Single(c => (string?)c.Attribute("datafieldname") == p).Attribute("id"));
        AssertControlIdsUnique(written);
        FormXmlValidator.Validate(written);
    }

    [Fact]
    public async Task MoveField_KeepsTheSuffixedIdOfTheMovedControl()
    {
        static XElement Empty(string label, string id) => new("section", new XAttribute("id", "{" + id + "}"),
            new XAttribute("name", "section_" + label.ToLowerInvariant()), new XAttribute("showlabel", "1"),
            new XElement("labels", new XElement("label", new XAttribute("description", label), new XAttribute("languagecode", "1033"))),
            new XElement("rows"));
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc => ContributorFormFixture.Section(doc, "General").Parent!.Add(
            Empty("Fourth", "f0f0f0f0-f0f0-f0f0-f0f0-f0f0f0f0f0f0"), Empty("Fifth", "f1f1f1f1-f1f1-f1f1-f1f1-f1f1f1f1f1f1")));
        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Fourth", ["telephone1"]));
        await h.Service.MoveFieldAsync(new("account", "Fixture", "telephone1", "Fourth", "Fifth"));
        Assert.Equal(2, h.Writes.Count);
        var written = XDocument.Parse(h.StateXml);
        // Moving never renames: the moved control keeps the id it was created with; the original is untouched.
        Assert.Equal("telephone11", (string?)ContributorFormFixture.Section(written, "Fifth").Descendants("control").Single().Attribute("id"));
        Assert.Equal("telephone1", (string?)ContributorFormFixture.Section(written, "Other").Descendants("control")
            .Single(c => (string?)c.Attribute("datafieldname") == "telephone1").Attribute("id"));
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
        FormXmlValidator.Validate(XDocument.Parse(h.Original));
        await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]));
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Equal(new[] { "telephone1", "telephone1", "telephone11" }, ControlIds(written, "telephone1").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(new string[0], "name")]
    [InlineData(new[] { "name" }, "name1")]
    [InlineData(new[] { "name", "name1", "name3" }, "name2")]
    [InlineData(new[] { "NAME", "Name1" }, "name2")]
    [InlineData(new[] { "header_name" }, "name")]
    public void NextFieldControlId_IsDeterministicSmallestFreeSuffix(string[] existingIds, string expected)
    {
        var doc = new XDocument(new XElement("form", existingIds.Select(id => new XElement("control", new XAttribute("id", id)))));
        Assert.Equal(expected, FormXmlEditor.NextFieldControlId(doc, "name"));
    }
}
