using System.Xml.Linq;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>Offline SDK-mock coverage of forms repair-bindings (#1527). No live Dataverse behaviour is claimed.</summary>
[Trait("Category", "Unit")]
public sealed class FormBindingRepairTests
{
    private static XElement Control(XDocument doc, string binding) =>
        doc.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == binding);

    private static XElement Area(string name, string areaId, string cellId, string controlId, string binding) =>
        new(name, new XAttribute("id", areaId), new XElement("rows", new XElement("row", new XElement("cell",
            new XAttribute("id", cellId),
            new XElement("control", new XAttribute("id", controlId), new XAttribute("datafieldname", binding),
                new XAttribute("classid", ControlTypeFixture.TypeId))))));

    /// <summary>Mis-cased bindings in two tab sections, the header and the footer.</summary>
    internal static void MisCasedEverywhere(XDocument doc)
    {
        Control(doc, "name").SetAttributeValue("datafieldname", "Name");
        Control(doc, "telephone1").SetAttributeValue("datafieldname", "TELEPHONE1");
        doc.Root!.Add(Area("header", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}", "{abababab-abab-abab-abab-abababababab}",
            "header_mayo_priorityscore", "Mayo_PriorityScore"));
        doc.Root!.Add(Area("footer", "{efefefef-efef-efef-efef-efefefefefef}", "{bcbcbcbc-bcbc-bcbc-bcbc-bcbcbcbcbcbc}",
            "footer_name", "NAME"));
    }

    /// <summary>A mis-cased binding whose control id is mis-cased too, plus a binding that matches no column.</summary>
    internal static void MisCasedIdAndUnknownBinding(XDocument doc)
    {
        var name = Control(doc, "name");
        name.SetAttributeValue("datafieldname", "Name");
        name.SetAttributeValue("id", "Name");
        ContributorFormFixture.Section(doc, "General").Element("rows")!.Add(new XElement("row", new XElement("cell",
            new XAttribute("id", "{ffffffff-ffff-ffff-ffff-fffffffffff1}"),
            new XElement("control", new XAttribute("id", "new_gone"), new XAttribute("datafieldname", "New_Gone"),
                new XAttribute("classid", ControlTypeFixture.TypeId), new XAttribute("disabled", "false")))));
    }

    [Fact]
    [Trait("AC", "AC-73")]
    public async Task Repair_RewritesOnlyMisCasedBindings_InSectionsHeaderAndFooter()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere);

        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);

        Assert.False(result.DryRun);
        Assert.True(result.FormXmlChanged);
        Assert.Equal(new[]
        {
            ("authored_name_control", "Name", "name", "My Tab / General"),
            ("telephone1", "TELEPHONE1", "telephone1", "My Tab / Other"),
            ("header_mayo_priorityscore", "Mayo_PriorityScore", ContributorFormFixture.Priority, "Header"),
            ("footer_name", "NAME", "name", "Footer"),
        }, result.Repaired.Select(r => (r.ControlId!, r.OldBinding, r.NewBinding, r.Location)));
        Assert.Equal("{55555555-5555-5555-5555-555555555555}", result.Repaired[1].SectionId);
        Assert.Null(result.Repaired[2].SectionId);
        Assert.Null(result.Repaired[3].SectionId);
        Assert.Empty(result.Unchanged);

        // Exactly one XML update, and it differs from the original only in those datafieldname values.
        var written = Assert.Single(h.Writes);
        var expected = XDocument.Parse(h.Original);
        Control(expected, "Name").SetAttributeValue("datafieldname", "name");
        Control(expected, "TELEPHONE1").SetAttributeValue("datafieldname", "telephone1");
        Control(expected, "Mayo_PriorityScore").SetAttributeValue("datafieldname", ContributorFormFixture.Priority);
        Control(expected, "NAME").SetAttributeValue("datafieldname", "name");
        Assert.Equal(expected.ToString(SaveOptions.DisableFormatting), written);
        FormXmlValidator.Validate(XDocument.Parse(written));
        Assert.Equal(0, h.SolutionRequests);
        Assert.Equal(0, h.PublishRequests);
        h.AssertIsolation();

        // Retry after the repair is a no-op.
        var retry = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Empty(retry.Repaired);
        Assert.False(retry.FormXmlChanged);
        Assert.Single(h.Writes);
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public async Task Repair_UnknownBindingAndMisCasedControlId_AreReportedAndLeftUnchanged()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedIdAndUnknownBinding);

        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);

        var repaired = Assert.Single(result.Repaired);
        Assert.Equal(("Name", "Name", "name"), (repaired.ControlId, repaired.OldBinding, repaired.NewBinding));
        Assert.Collection(result.Unchanged,
            id =>
            {
                Assert.Equal(BindingRepairReasons.ControlIdCaseDiffers, id.Reason);
                Assert.Equal(("Name", "Name", "name"), (id.ControlId, id.Binding, id.CanonicalName));
            },
            unknown =>
            {
                Assert.Equal(BindingRepairReasons.NoMatchingColumn, unknown.Reason);
                Assert.Equal(("new_gone", "New_Gone"), (unknown.ControlId, unknown.Binding));
                Assert.Null(unknown.CanonicalName);
                Assert.EndsWith(" / General", unknown.Location);
            });

        var written = XDocument.Parse(Assert.Single(h.Writes));
        var nameControl = written.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "name"
            && c.Ancestors("section").Any(s => s == ContributorFormFixture.Section(written, "General")));
        Assert.Equal("Name", (string?)nameControl.Attribute("id"));
        Assert.Single(written.Descendants("control"), c => (string?)c.Attribute("datafieldname") == "New_Gone");
    }

    [Theory]
    [Trait("AC", "AC-74")]
    [InlineData("New_Field", "new_field", true)]
    [InlineData("new_field", "new_field", false)]
    [InlineData("New_Field1", "new_field", true)]
    [InlineData("new_field12", "new_field", false)]
    [InlineData("header_New_Field", "new_field", true)]
    [InlineData("HEADER_new_field", "new_field", false)]
    [InlineData("footer_NEW_FIELD2", "new_field", true)]
    [InlineData("MyNew_Field", "new_field", false)]
    [InlineData("New_Field_custom", "new_field", false)]
    [InlineData("{4273EDBD-AC1D-40d3-9FB2-095C621B552D}", "new_field", false)]
    [InlineData("", "new_field", false)]
    [InlineData(null, "new_field", false)]
    public void ControlIdCaseDiffers_JudgesOnlyIdsBuiltFromTheLogicalName(string? controlId, string canonical, bool expected)
        => Assert.Equal(expected, FormXmlEditor.ControlIdCaseDiffers(controlId, canonical));

    [Fact]
    [Trait("AC", "AC-73")]
    public void Editor_ChangesNoOtherAttributeOrElement_AndIgnoresHiddenDataAndUnboundControls()
    {
        var doc = XDocument.Parse(ContributorFormFixture.Build("base"));
        Control(doc, "name").SetAttributeValue("datafieldname", "Name");
        doc.Root!.Add(new XElement("hiddencontrols", new XElement("data",
            new XAttribute("id", "TELEPHONE1"), new XAttribute("datafieldname", "TELEPHONE1"), new XAttribute("classid", ControlTypeFixture.TypeId))));
        var before = doc.ToString(SaveOptions.DisableFormatting);

        var (repaired, unchanged) = FormXmlEditor.RepairBindings(doc, ["name", "telephone1", ContributorFormFixture.Priority]);

        Assert.Equal("Name", Assert.Single(repaired).OldBinding);
        Assert.Empty(unchanged);
        Assert.Equal(before.Replace("datafieldname=\"Name\"", "datafieldname=\"name\"", StringComparison.Ordinal),
            doc.ToString(SaveOptions.DisableFormatting));
        Assert.Equal("TELEPHONE1", (string?)doc.Descendants("data").Single().Attribute("datafieldname"));
    }

    [Fact]
    [Trait("AC", "AC-75")]
    public async Task Repair_NothingToRepair_SucceedsWithoutUpdate_AndKeepsExplicitSideEffects()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token);
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Empty(result.Repaired);
        Assert.Empty(result.Unchanged);
        Assert.False(result.FormXmlChanged);
        Assert.Empty(h.Writes);
        Assert.Equal(h.Original, h.StateXml);
        h.AssertIsolation();

        var withEffects = new ContributorFormHarness(cts.Token) { AllowExplicitSideEffects = true };
        var explicitResult = await withEffects.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token);
        Assert.False(explicitResult.FormXmlChanged);
        Assert.True(explicitResult.SolutionAssociationRequested);
        Assert.True(explicitResult.PublicationRequested);
        Assert.Empty(withEffects.Writes);
        Assert.Equal(1, withEffects.SolutionRequests);
        Assert.Equal(1, withEffects.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-76")]
    public async Task DryRun_PlansTheSameRepair_WithoutAcknowledgementOrAnyWrite()
    {
        using var cts = new CancellationTokenSource();
        // Confirmation would be denied: a dry run must not ask for it.
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { DenyConfirmation = true };

        var plan = await h.Service.RepairBindingsAsync(new("account", "Fixture", DryRun: true), ct: cts.Token);

        Assert.True(plan.DryRun);
        Assert.False(plan.FormXmlChanged);
        Assert.False(plan.SolutionAssociationRequested);
        Assert.False(plan.PublicationRequested);
        Assert.Equal(4, plan.Repaired.Count);
        Assert.Empty(h.Writes);
        Assert.DoesNotContain("confirmation", h.Journal);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
        Assert.Equal(h.Original, h.StateXml);

        var real = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere);
        var applied = await real.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Equal(plan.Repaired, applied.Repaired);
        Assert.Equal(plan.Unchanged, applied.Unchanged);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task DeniedConfirmation_NonCustomizableForm_AndCancellation_DoNotWrite()
    {
        using var cts = new CancellationTokenSource();
        var denied = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { DenyConfirmation = true };
        var deniedError = await Assert.ThrowsAsync<PpdsException>(() => denied.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal("Environment.ReadOnly", deniedError.ErrorCode);
        Assert.Equal(0, denied.Acquisitions);
        Assert.Empty(denied.Writes);

        var locked = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { IsCustomizable = false };
        var lockedError = await Assert.ThrowsAsync<PpdsException>(() => locked.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.FormNotCustomizable, lockedError.ErrorCode);
        Assert.Empty(locked.Writes);

        var cancelled = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { BeforeMetadata = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Empty(cancelled.Writes);
        Assert.Equal(0, cancelled.SolutionRequests + cancelled.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task LegacyImplementation_RejectsRepair_WithoutAnyFallback()
    {
        var legacy = new Mock<IFormService> { CallBase = true };
        await Assert.ThrowsAsync<NotSupportedException>(() => legacy.Object.RepairBindingsAsync(new("account", "Fixture")));
        legacy.Verify(s => s.SetFormXmlAsync(It.IsAny<SetFormXmlRequest>(), It.IsAny<IProgressReporter?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
