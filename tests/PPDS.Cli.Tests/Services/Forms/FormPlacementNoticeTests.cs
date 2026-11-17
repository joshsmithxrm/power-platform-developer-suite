using System.Xml.Linq;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

[Trait("Category", "Unit")]
public sealed class FormPlacementNoticeTests
{
    private static void CaseVariantName(XDocument doc) =>
        doc.Descendants("control").First(c => (string?)c.Attribute("datafieldname") == "name")
            .SetAttributeValue("datafieldname", "Name");

    private static IEnumerable<XElement> Controls(string xml, string field) =>
        XDocument.Parse(xml).Descendants("control").Where(c => (string?)c.Attribute("datafieldname") == field);

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task Add_FieldInAnotherSection_ReportsOtherPlacementAndStillAdds()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["TELEPHONE1"]), ct: cts.Token);
        Assert.Equal(new[] { "telephone1" }, result.AddedFields);
        var notice = Assert.Single(result.OtherPlacements!);
        Assert.Equal("telephone1", notice.Field);
        Assert.Equal(new[] { "My Tab / Other" }, notice.Locations);
        Assert.Empty(result.NonCanonicalBindings!);
        Assert.Single(h.Writes);
        var written = XDocument.Parse(h.StateXml);
        Assert.Single(ContributorFormFixture.Section(written, "General").Descendants("control"),
            c => (string?)c.Attribute("datafieldname") == "telephone1");
        Assert.Single(ContributorFormFixture.Section(written, "Other").Descendants("control"),
            c => (string?)c.Attribute("datafieldname") == "telephone1");
    }

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task Add_FieldInTwoOtherSections_ReportsBothPlacements()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, "phone-in-two-sections");
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["telephone1"]), ct: cts.Token);
        var notice = Assert.Single(result.OtherPlacements!);
        Assert.Equal(new[] { "My Tab / Other", "My Tab / Third" }, notice.Locations);
        Assert.Equal(new[] { "telephone1" }, result.AddedFields);
    }

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task Add_FieldInFormHeader_ReportsHeaderPlacement()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: doc => doc.Root!.Add(
            new XElement("header", new XAttribute("id", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}"), new XElement("rows", new XElement("row", new XElement("cell",
                new XAttribute("id", "{abababab-abab-abab-abab-abababababab}"),
                new XElement("control", new XAttribute("id", "header_priority"),
                    new XAttribute("datafieldname", ContributorFormFixture.Priority),
                    new XAttribute("classid", ControlTypeFixture.TypeId))))))));
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General",
            [ContributorFormFixture.Priority]), ct: cts.Token);
        var notice = Assert.Single(result.OtherPlacements!);
        Assert.Equal(new[] { "Header" }, notice.Locations);
        Assert.Equal(new[] { ContributorFormFixture.Priority }, result.AddedFields);
    }

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task Add_FieldNewToTheForm_ReportsNoOtherPlacementAndAdds()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General",
            [ContributorFormFixture.Priority]), ct: cts.Token);
        Assert.Equal(new[] { ContributorFormFixture.Priority }, result.AddedFields);
        Assert.Empty(result.OtherPlacements!);
        Assert.Empty(result.NonCanonicalBindings!);
        Assert.Single(h.Writes);
    }

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task Add_FieldAlreadyInTargetSection_IsNoOpWithoutOtherPlacementNotice()
    {
        // telephone1 lives in "Other"; re-adding it to "Other" is the same-section no-op and must not report itself.
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "Other", ["telephone1"]), ct: cts.Token);
        Assert.Equal(new[] { "telephone1" }, result.AlreadyPresentFields);
        Assert.Empty(result.AddedFields!);
        Assert.Empty(result.OtherPlacements!);
        Assert.Empty(result.NonCanonicalBindings!);
        Assert.Empty(h.Writes);
    }

    [Fact]
    [Trait("AC", "AC-70")]
    public async Task Add_SameSectionReAdd_RemainsNoOpWithoutNotices()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["NAME"]), ct: cts.Token);
        Assert.False(result.FormXmlChanged);
        Assert.Empty(result.OtherPlacements!);
        Assert.Empty(result.NonCanonicalBindings!);
        Assert.Empty(h.Writes);
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task Add_ExistingCaseVariantBinding_IsReportedDistinctlyAndNotRewritten()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: CaseVariantName);
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["name"]), ct: cts.Token);
        Assert.Equal(new[] { "name" }, result.AlreadyPresentFields);
        var binding = Assert.Single(result.NonCanonicalBindings!);
        Assert.Equal("Name", binding.StoredName);
        Assert.Equal("name", binding.CanonicalName);
        Assert.Empty(h.Writes);
        Assert.Equal(h.Original, h.StateXml);
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task Add_SecondControlWithCaseVariantBinding_IsReportedEvenWhenNotFirst()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, "name-twice-selected", transform: doc =>
            doc.Descendants("section").First().Descendants("control").Where(c => (string?)c.Attribute("datafieldname") == "name")
                .Last().SetAttributeValue("datafieldname", "NAME"));
        var result = await h.Service.AddFieldsWithResultAsync(new("account", "Fixture", "General", ["name"]), ct: cts.Token);
        var binding = Assert.Single(result.NonCanonicalBindings!);
        Assert.Equal("NAME", binding.StoredName);
        Assert.Empty(h.Writes);
    }

    [Fact]
    [Trait("AC", "AC-71")]
    public async Task Reorder_ExistingCaseVariantBinding_IsReportedAndKept()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: CaseVariantName);
        var before = Controls(h.Original, "Name").Single().Parent!;
        var result = await h.Service.ReorderFieldsWithResultAsync(new("account", "Fixture", "General", ["name"]), ct: cts.Token);
        var binding = Assert.Single(result.NonCanonicalBindings!);
        Assert.Equal("Name", binding.StoredName);
        Assert.Equal("name", binding.CanonicalName);
        Assert.Single(h.Writes);
        Assert.Empty(Controls(h.StateXml, "name"));
        var after = Assert.Single(Controls(h.StateXml, "Name")).Parent!;
        Assert.True(XNode.DeepEquals(before, after), "the existing cell (labels, id, disabled) must be unchanged");
    }
}
