using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

// New additive APIs: these are fixed-only. The shared contributor tests carry the bug before/after proof.
[Trait("Category", "Unit")]
[Trait("AC", "AC-61")]
[Trait("AC", "AC-62")]
[Trait("AC", "AC-64")]
public sealed class FormExplicitLayoutTests
{
    [Fact]
    public async Task StrictPermutation_PreservesMixedRowsAttributesCellsAndNonfieldPositions_AndRetryIsNoop()
    {
        var h = Harness(mixed: true);
        var before = XDocument.Parse(h.Original);
        await h.Service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["TELEPHONE1", "NAME"]));
        var after = XDocument.Parse(Assert.Single(h.Writes));
        var rows = Section(after, "General").Element("rows")!;
        Assert.Equal("17", (string?)rows.Elements("row").First().Attribute("height"));
        Assert.Equal(new[] { "telephone1", "name" }, rows.Elements("row").First().Elements("cell").Select(Binding));
        Assert.Equal(2, rows.Elements("row").Count());
        foreach (var cell in before.Descendants("cell"))
            Assert.True(XNode.DeepEquals(cell, after.Descendants("cell").Single(other => (string?)other.Attribute("id") == (string?)cell.Attribute("id"))));
        Assert.True(XNode.DeepEquals(before.Root!.Element("controlDescriptions"), after.Root!.Element("controlDescriptions")));
        Assert.True(XNode.DeepEquals(Section(before, "General").Element("rows")!.Elements("row").Last(), rows.Elements("row").Last()));
        await h.Service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["telephone1", "name"]));
        Assert.Single(h.Writes); h.AssertIsolation();
    }

    [Theory]
    [InlineData("missing", "Forms.InvalidFieldOrder")]
    [InlineData("duplicate", "Forms.InvalidFieldOrder")]
    [InlineData("extra", "Forms.InvalidFieldOrder")]
    [InlineData("unknown", "Forms.ColumnNotFound")]
    [InlineData("ambiguous", "Forms.AmbiguousField")]
    [InlineData("span", "Forms.UnsupportedLayout")]
    public async Task StrictInvalidPlan_RejectsBeforeAllWrites(string scenario, string code)
    {
        var h = scenario == "ambiguous" ? new ContributorFormHarness(CancellationToken.None, "name-twice-selected")
            : Harness(span: scenario == "span");
        string[] fields = scenario switch { "missing" => ["name"], "duplicate" => ["name", "NAME"],
            "extra" => ["name", "telephone1", ContributorFormFixture.Priority], "unknown" => ["unknown", "name"],
            "ambiguous" => ["name"], _ => ["telephone1", "name"] };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ReorderFieldsStrictAsync(
            new("account", "Fixture", "General", fields, "fixture_solution", true)));
        Assert.Equal(code, error.ErrorCode); AssertUnchanged(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_PreservesEntireCellAndSourceSiblings_RowAttributes_AndRetryIsNoop(bool mixed)
    {
        var h = Harness(mixed);
        var before = XDocument.Parse(h.Original);
        var phone = Cell(before, "telephone1");
        var sourceRow = phone.Parent!;
        await h.Service.MoveFieldAsync(new("account", "Fixture", "TELEPHONE1", "General", "Other"));
        var after = XDocument.Parse(Assert.Single(h.Writes));
        var moved = Cell(after, "telephone1");
        Assert.True(XNode.DeepEquals(phone, moved));
        Assert.Equal("Other", Label(moved.Ancestors("section").Single()));
        if (mixed)
        {
            Assert.Equal("17", (string?)Cell(after, "name").Parent!.Attribute("height"));
            Assert.Single(Cell(after, "name").Parent!.Elements("cell"));
            Assert.Empty(moved.Parent!.Attributes());
        }
        else Assert.True(XNode.DeepEquals(sourceRow, moved.Parent));
        Assert.True(XNode.DeepEquals(before.Root!.Element("controlDescriptions"), after.Root!.Element("controlDescriptions")));
        Assert.True(XNode.DeepEquals(before.Descendants("control").Single(control => (string?)control.Attribute("id") == "notescontrol").Parent,
            after.Descendants("control").Single(control => (string?)control.Attribute("id") == "notescontrol").Parent));
        await h.Service.MoveFieldAsync(new("account", "Fixture", "telephone1", "General", "Other"));
        Assert.Single(h.Writes); h.AssertIsolation();
    }

    [Fact]
    public async Task Move_BeforeAnchor_MovesWholeStandaloneRow_AndLostAckRetryHasNoSecondUpdate()
    {
        var h = new ContributorFormHarness(CancellationToken.None) { LoseFirstUpdateAcknowledgement = true };
        var originalPhone = Cell(XDocument.Parse(h.Original), "telephone1");
        var request = new MoveFieldRequest("account", "Fixture", "telephone1", "Other", "General", "NAME");
        var failure = await Assert.ThrowsAsync<PpdsException>(() => h.Service.MoveFieldAsync(request));
        Assert.IsType<TimeoutException>(failure.InnerException);
        await h.Service.MoveFieldAsync(request);
        var after = XDocument.Parse(Assert.Single(h.Writes));
        Assert.True(XNode.DeepEquals(originalPhone, Cell(after, "telephone1")));
        Assert.Equal("telephone1", Binding(Section(after, "General").Element("rows")!.Elements("row").First().Element("cell")!));
        Assert.Empty(Section(after, "Other").Element("rows")!.Elements("row"));
        h.AssertIsolation();
    }

    [Fact]
    public async Task SameSectionMove_NoPositionIsNoop_ExplicitAnchorCanRepositionWithoutCopy()
    {
        var h = Harness();
        await h.Service.MoveFieldAsync(new("account", "Fixture", "name", "General", "General"));
        Assert.Empty(h.Writes);
        await h.Service.MoveFieldAsync(new("account", "Fixture", "telephone1", "General", "General", "name"));
        Assert.Single(h.Writes);
        Assert.Equal(1, XDocument.Parse(h.StateXml).Descendants("control").Count(control => (string?)control.Attribute("datafieldname") == "telephone1"));
        h.AssertIsolation();
    }

    [Fact]
    public async Task Move_IntoRowlessSection_CreatesOnlyOwnedRowsAndPreservesReferences()
    {
        var h = new ContributorFormHarness(CancellationToken.None, "rowless");
        h.AssertPreconditions("rowless");
        var original = XDocument.Parse(h.Original);
        await h.Service.MoveFieldAsync(new("account", "Fixture", "name", "Other", "General"));
        var after = XDocument.Parse(Assert.Single(h.Writes));
        Assert.Single(Section(after, "General").Element("rows")!.Elements("row"));
        Assert.True(XNode.DeepEquals(Cell(original, "name"), Cell(after, "name")));
        Assert.True(XNode.DeepEquals(original.Root!.Element("controlDescriptions"), after.Root!.Element("controlDescriptions")));
        h.AssertIsolation();
    }

    [Fact]
    public async Task Move_PreservesSubgridAndUnrelatedNestedParameters()
    {
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc =>
            FormXmlEditor.AddSubgrid(doc, new("account", "Fixture", "General", "Contacts", "contact", new Guid("eeeeeeee-0000-0000-0000-000000000001"))));
        FormXmlValidator.Validate(XDocument.Parse(h.Original));
        var original = XDocument.Parse(h.Original).Descendants("control").Single(control => (string?)control.Attribute("id") == "subgrid_contacts").Parent!;
        await h.Service.MoveFieldAsync(new("account", "Fixture", "telephone1", "Other", "General", "name"));
        var after = XDocument.Parse(Assert.Single(h.Writes)).Descendants("control").Single(control => (string?)control.Attribute("id") == "subgrid_contacts").Parent!;
        Assert.True(XNode.DeepEquals(original, after)); h.AssertIsolation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOperations_ProtectedNoopRetainsExplicitSideEffectsWithoutXmlUpdate(bool move)
    {
        var h = new ContributorFormHarness(CancellationToken.None) { AllowExplicitSideEffects = true };
        if (move) await h.Service.MoveFieldAsync(new("account", "Fixture", "name", "General", "General", SolutionUniqueName: "fixture_solution", Publish: true));
        else await h.Service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["name"], "fixture_solution", true));
        Assert.Empty(h.Writes); Assert.Equal(1, h.SolutionRequests); Assert.Equal(1, h.PublishRequests); h.AssertIsolation();
    }

    [Theory]
    [InlineData("target-duplicate", "Forms.FieldAlreadyInTarget")]
    [InlineData("source-ambiguous", "Forms.AmbiguousField")]
    [InlineData("missing-source", "Forms.FieldNotInSection")]
    [InlineData("unknown", "Forms.ColumnNotFound")]
    [InlineData("anchor-self", "Forms.InvalidFieldOrder")]
    [InlineData("anchor-missing", "Forms.InvalidFieldOrder")]
    [InlineData("span", "Forms.UnsupportedLayout")]
    [InlineData("mixed-anchor", "Forms.UnsupportedLayout")]
    [InlineData("section-ambiguous", "Forms.AmbiguousElement")]
    public async Task InvalidMove_RejectsBeforeXmlSolutionOrPublish(string scenario, string code)
    {
        var h = scenario switch
        {
            "target-duplicate" => new ContributorFormHarness(CancellationToken.None, "name-elsewhere"),
            "source-ambiguous" => new ContributorFormHarness(CancellationToken.None, "name-twice-selected"),
            "mixed-anchor" => new ContributorFormHarness(CancellationToken.None, transform: doc =>
            {
                Section(doc, "General").SetAttributeValue("columns", "2");
                Cell(doc, "name").Parent!.Add(new XElement("cell", new XAttribute("id", "{ffff9999-0000-0000-0000-000000000001}"),
                    new XAttribute("userspacer", "true")));
            }),
            "section-ambiguous" => new ContributorFormHarness(CancellationToken.None, transform: doc => Section(doc, "Other").Element("labels")!.Element("label")!.SetAttributeValue("description", "General")),
            _ => Harness(span: scenario == "span")
        };
        if (scenario == "mixed-anchor")
        {
            var fixture = XDocument.Parse(h.Original); FormXmlValidator.Validate(fixture);
            Assert.Equal("Other", Label(Cell(fixture, "telephone1").Ancestors("section").Single()));
            Assert.Equal(2, Cell(fixture, "name").Parent!.Elements("cell").Count());
        }
        var field = scenario == "unknown" ? "unknown" : scenario == "missing-source" ? ContributorFormFixture.Priority : scenario is "span" or "mixed-anchor" ? "telephone1" : "name";
        var from = scenario == "mixed-anchor" ? "Other" : "General";
        var to = scenario is "mixed-anchor" or "anchor-self" ? "General" : "Other";
        var before = scenario == "anchor-self" ? "name" : scenario == "anchor-missing" ? ContributorFormFixture.Priority : scenario == "mixed-anchor" ? "name" : null;
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.MoveFieldAsync(new("account", "Fixture", field, from, to, before, "fixture_solution", true)));
        Assert.Equal(code, error.ErrorCode); AssertUnchanged(h);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOperations_ConfirmationDenialAndManagedLocksCannotBeBypassed(bool move)
    {
        foreach (var locked in new[] { false, true })
        {
            var h = new ContributorFormHarness(CancellationToken.None) { DenyConfirmation = !locked, IsManaged = locked, IsCustomizable = !locked };
            var error = await Assert.ThrowsAsync<PpdsException>(() => move
                ? h.Service.MoveFieldAsync(new("account", "Fixture", "name", "General", "General"))
                : h.Service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["name"])));
            Assert.Equal(locked ? FormErrorCodes.FormNotCustomizable : "Environment.ReadOnly", error.ErrorCode);
            Assert.Equal(0, h.MetadataCalls); AssertUnchanged(h);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewOperations_CancellationPreservesIdentityAndReleasesFetchLease(bool move)
    {
        using var cancel = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancel.Token) { BeforeMetadata = cancel.Cancel };
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => move
            ? h.Service.MoveFieldAsync(new("account", "Fixture", "name", "General", "Other"), ct: cancel.Token)
            : h.Service.ReorderFieldsStrictAsync(new("account", "Fixture", "General", ["name"]), ct: cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken); AssertUnchanged(h);
    }

    private static ContributorFormHarness Harness(bool mixed = false, bool span = false)
    {
        var h = new ContributorFormHarness(CancellationToken.None, transform: doc =>
        {
            var phone = Cell(doc, "telephone1"); var phoneRow = phone.Parent!; phoneRow.SetAttributeValue("height", "23"); phoneRow.Remove();
            var rows = Section(doc, "General").Element("rows")!;
            if (mixed) { var nameRow = Cell(doc, "name").Parent!; nameRow.SetAttributeValue("height", "17"); phone.Remove(); nameRow.Add(phone); }
            else
            {
                // Append after Timeline23's22 reserved rows, preserving its original cell.
                rows.Elements("row").Last().AddAfterSelf(Enumerable.Range(0, 22).Select(_ => new XElement("row")));
                rows.Add(phoneRow);
            }
            if (span) phone.SetAttributeValue("rowspan", "2");
        });
        FormXmlValidator.Validate(XDocument.Parse(h.Original)); return h;
    }

    private static void AssertUnchanged(ContributorFormHarness h)
    { Assert.Empty(h.Writes); Assert.Equal(0, h.SolutionRequests); Assert.Equal(0, h.PublishRequests); Assert.Equal(h.Original, h.StateXml); h.AssertIsolation(); }
    private static XElement Section(XDocument doc, string name) => ContributorFormFixture.Section(doc, name);
    private static string? Label(XElement section) => (string?)section.Element("labels")?.Element("label")?.Attribute("description");
    private static string? Binding(XElement cell) => (string?)cell.Element("control")?.Attribute("datafieldname");
    private static XElement Cell(XDocument doc, string field) => doc.Descendants("cell").Single(cell => Binding(cell) == field);
}
