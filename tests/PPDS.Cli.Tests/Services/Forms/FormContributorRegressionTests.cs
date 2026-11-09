using System.Xml.Linq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

// These tests use only pre-existing APIs so the exact bytes can run before and after.
[Trait("Category", "Unit")]
[Trait("AC", "AC-59")]
[Trait("AC", "AC-60")]
[Trait("AC", "AC-63")]
public sealed class FormContributorRegressionTests
{
    [Fact]
    public async Task RepeatAdd_SameSection_IsNoopAndPreservesFirstWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var h = NewHarness(cancellation.Token);
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", [ContributorFormFixture.Priority]), ct: cancellation.Token);
        var firstWrite = Assert.Single(h.Writes);
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["MAYO_PRIORITYSCORE"]), ct: cancellation.Token);
        Assert.Single(h.Writes);
        Assert.Equal(firstWrite, h.StateXml);
        Assert.Single(Controls(h.StateXml, ContributorFormFixture.Priority, "General"));
        h.AssertIsolation();
    }

    [Fact]
    public async Task ExistingCaseVariants_DoNotWriteOrReplaceCustomLabels()
    {
        using var cancellation = new CancellationTokenSource();
        var h = NewHarness(cancellation.Token);
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["NAME", "name"]), ct: cancellation.Token);
        Assert.Empty(h.Writes);
        Assert.Equal(h.Original, h.StateXml);
        h.AssertIsolation();
    }

    [Fact]
    public async Task DuplicateNewInput_AddsOneCanonicalControl()
    {
        using var cancellation = new CancellationTokenSource();
        var h = NewHarness(cancellation.Token);
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", [ContributorFormFixture.Priority, "MAYO_PRIORITYSCORE"]), ct: cancellation.Token);
        Assert.Single(h.Writes);
        var control = Assert.Single(Controls(h.StateXml, ContributorFormFixture.Priority, "General"));
        Assert.Equal(ContributorFormFixture.Priority, (string?)control.Attribute("id"));
        Assert.Equal(ClassIdResolver.ResolveForField("Integer"), (string?)control.Attribute("classid"));
        Assert.Equal("Priority score", (string?)control.Parent!.Element("labels")?.Element("label")?.Attribute("description"));
        h.AssertIsolation();
    }

    [Fact]
    public async Task MixedExistingNewAndDuplicate_PreservesOriginalAndAddsOnlyOne()
    {
        using var cancellation = new CancellationTokenSource();
        var h = NewHarness(cancellation.Token);
        var originalName = ContributorFormFixture.NameCell(XDocument.Parse(h.Original));
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["NAME", "name", ContributorFormFixture.Priority, "MAYO_PRIORITYSCORE"]), ct: cancellation.Token);
        Assert.Single(h.Writes);
        Assert.Single(Controls(h.StateXml, "name", "General"));
        Assert.Single(Controls(h.StateXml, ContributorFormFixture.Priority, "General"));
        Assert.True(XNode.DeepEquals(originalName, ContributorFormFixture.NameCell(XDocument.Parse(h.StateXml))));
        h.AssertIsolation();
    }

    [Fact]
    public async Task Add_PreservesLegitimatePlacementInAnotherSection()
    {
        using var cancellation = new CancellationTokenSource();
        var h = NewHarness(cancellation.Token);
        var originalOther = ContributorFormFixture.Section(XDocument.Parse(h.Original), "Other");
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["TELEPHONE1"]), ct: cancellation.Token);
        Assert.Single(Controls(h.StateXml, "telephone1", "General"));
        Assert.Single(Controls(h.StateXml, "telephone1", "Other"));
        Assert.True(XNode.DeepEquals(originalOther, ContributorFormFixture.Section(XDocument.Parse(h.StateXml), "Other")));
        h.AssertIsolation();
    }

    [Fact]
    public async Task AppliedWriteWithLostAcknowledgement_RetryDoesNotAddControlsOrUpdateAgain()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token) { LoseFirstUpdateAcknowledgement = true };
        h.AssertPreconditions("base");
        var first = await Assert.ThrowsAsync<PpdsException>(() => h.Service.AddFieldAsync(
            new("account", "Fixture", "General", [ContributorFormFixture.Priority]), ct: cancellation.Token));
        Assert.IsType<TimeoutException>(first.InnerException);
        var applied = Assert.Single(h.Writes);
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["MAYO_PRIORITYSCORE"]), ct: cancellation.Token);
        Assert.Single(h.Writes);
        Assert.Equal(applied, h.StateXml);
        h.AssertIsolation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReorderNewCell_UsesMetadataLabelAndCanonicalBinding(bool emptyDisplayName)
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token, emptyDisplayName: emptyDisplayName);
        h.AssertPreconditions("base");
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", ["MAYO_PRIORITYSCORE", "name"]), ct: cancellation.Token);
        var control = Assert.Single(Controls(h.StateXml, ContributorFormFixture.Priority, "General"));
        Assert.Equal(ContributorFormFixture.Priority, (string?)control.Attribute("id"));
        Assert.Equal(ClassIdResolver.ResolveForField("Integer"), (string?)control.Attribute("classid"));
        Assert.Equal(emptyDisplayName ? ContributorFormFixture.Priority : "Priority score", (string?)control.Parent!.Element("labels")?.Element("label")?.Attribute("description"));
        Assert.True(XNode.DeepEquals(ContributorFormFixture.NameCell(XDocument.Parse(h.Original)), ContributorFormFixture.NameCell(XDocument.Parse(h.StateXml))));
        h.AssertIsolation();
    }

    [Theory]
    [InlineData("base")]
    [InlineData("phone-in-two-sections")]
    public async Task Reorder_FieldOnlyElsewhere_RejectsWithoutAnyWrite(string fixture)
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token, fixture);
        h.AssertPreconditions(fixture);
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ReorderFieldsAsync(
            new("account", "Fixture", "General", ["telephone1", "name"], "fixture_solution", true), ct: cancellation.Token));
        Assert.Equal("Forms.FieldInAnotherSection", error.ErrorCode);
        Assert.Empty(h.Writes);
        Assert.Equal(0, h.SolutionRequests);
        Assert.Equal(0, h.PublishRequests);
        Assert.Equal(h.Original, h.StateXml);
        h.AssertIsolation();
    }

    [Fact]
    public async Task ReorderExistingTarget_PreservesOtherSectionInstanceAndAuthoredLabel()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token, "name-elsewhere");
        h.AssertPreconditions("name-elsewhere");
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", ["NAME"]), ct: cancellation.Token);
        Assert.True(XNode.DeepEquals(ContributorFormFixture.Section(XDocument.Parse(h.Original), "Other"),
            ContributorFormFixture.Section(XDocument.Parse(h.StateXml), "Other")));
        Assert.True(XNode.DeepEquals(ContributorFormFixture.NameCell(XDocument.Parse(h.Original)), ContributorFormFixture.NameCell(XDocument.Parse(h.StateXml))));
        h.AssertIsolation();
    }

    [Fact]
    public async Task Reorder_AuthoritativeListStillDropsUnlistedBoundFieldAndKeepsTimeline()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token, "unreferenced-unlisted");
        h.AssertPreconditions("unreferenced-unlisted");
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", [ContributorFormFixture.Priority, "name"]), ct: cancellation.Token);
        Assert.Empty(Controls(h.StateXml, "unlisted_extra", "General"));
        var before = XDocument.Parse(h.Original).Descendants("control").Single(c => (string?)c.Attribute("id") == "notescontrol");
        var after = XDocument.Parse(h.StateXml).Descendants("control").Single(c => (string?)c.Attribute("id") == "notescontrol");
        Assert.True(XNode.DeepEquals(before.Parent, after.Parent));
        h.AssertIsolation();
    }

    [Fact]
    public async Task AllExisting_ExplicitSideEffectsRemainSeparateFromXmlUpdate()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token) { AllowExplicitSideEffects = true };
        h.AssertPreconditions("base");
        await h.Service.AddFieldAsync(new("account", "Fixture", "General", ["NAME"], "fixture_solution", true), ct: cancellation.Token);
        Assert.Empty(h.Writes);
        Assert.Equal(1, h.SolutionRequests);
        Assert.Equal(1, h.PublishRequests);
        Assert.Equal(h.Original, h.StateXml);
        h.AssertIsolation();
    }

    [Fact]
    public async Task AllExisting_ManagedLockedFormStillFailsBeforeMetadataOrWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token) { IsManaged = true, IsCustomizable = false };
        h.AssertPreconditions("base");
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.AddFieldAsync(
            new("account", "Fixture", "General", ["NAME"]), ct: cancellation.Token));
        Assert.Equal(FormErrorCodes.FormNotCustomizable, error.ErrorCode);
        Assert.Equal(0, h.MetadataCalls);
        Assert.Empty(h.Writes);
        h.AssertIsolation();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyReorder_AmbiguousSelectedBindingsOrRepeatedArguments_RejectBeforeWriting(bool duplicateControls)
    {
        var h = new ContributorFormHarness(CancellationToken.None, duplicateControls ? "name-twice-selected" : "base");
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ReorderFieldsAsync(new("account", "Fixture", "General",
            duplicateControls ? ["name"] : ["name", "NAME"], "fixture_solution", true)));
        Assert.Equal(duplicateControls ? "Forms.AmbiguousField" : "Forms.InvalidFieldOrder", error.ErrorCode);
        Assert.Empty(h.Writes); Assert.Equal(h.Original, h.StateXml);
        Assert.Equal(0, h.SolutionRequests); Assert.Equal(0, h.PublishRequests); h.AssertIsolation();
    }

    private static ContributorFormHarness NewHarness(CancellationToken token)
    {
        var h = new ContributorFormHarness(token);
        h.AssertPreconditions("base");
        return h;
    }

    private static IEnumerable<XElement> Controls(string xml, string field, string section) =>
        ContributorFormFixture.Section(XDocument.Parse(xml), section).Descendants("control").Where(c =>
            string.Equals((string?)c.Attribute("datafieldname"), field, StringComparison.OrdinalIgnoreCase));
}
