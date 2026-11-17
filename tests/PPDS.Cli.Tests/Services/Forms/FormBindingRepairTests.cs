using System.Xml.Linq;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>
/// Offline SDK-mock coverage of forms repair-bindings (#1527). The synthetic server stores the submitted text verbatim and
/// returns it on read-back; no live Dataverse behaviour (storage, normalisation or rendering) is claimed.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FormBindingRepairTests
{
    private static XElement Control(XDocument doc, string binding) =>
        doc.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == binding);

    private static XElement Area(string name, string areaId, string cellId, string? controlId, string binding) =>
        new(name, new XAttribute("id", areaId), new XElement("rows", new XElement("row", new XElement("cell",
            new XAttribute("id", cellId),
            new XElement("control", controlId is null ? null : new XAttribute("id", controlId), new XAttribute("datafieldname", binding),
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
        AddGeneralControl(doc, "{ffffffff-ffff-ffff-ffff-fffffffffff1}", "new_gone", "New_Gone");
    }

    /// <summary>A header control without an id (JSON omits null controlId and sectionId).</summary>
    internal static void MisCasedHeaderWithoutId(XDocument doc)
        => doc.Root!.Add(Area("header", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}", "{abababab-abab-abab-abab-abababababab}",
            null, "Mayo_PriorityScore"));

    private static void AddGeneralControl(XDocument doc, string cellId, string controlId, string binding, params XAttribute[] extra)
        => ContributorFormFixture.Section(doc, "General").Element("rows")!.Add(new XElement("row", new XElement("cell",
            new XAttribute("id", cellId),
            new XElement("control", new XAttribute("id", controlId), new XAttribute("datafieldname", binding),
                new XAttribute("classid", ControlTypeFixture.TypeId), new XAttribute("disabled", "false"), extra))));

    // ── AC-73: repair, text preservation, read-back ─────────────────────────

    [Fact]
    [Trait("AC", "AC-73")]
    public async Task Repair_RewritesOnlyMisCasedBindings_InSectionsHeaderAndFooter_AndReadsBack()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere);

        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);

        Assert.False(result.DryRun);
        Assert.True(result.FormXmlChanged);
        Assert.True(result.ReadBackVerified);
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
        Assert.All(result.Repaired, r => Assert.False(r.ControlIdCaseDiffers));
        Assert.Empty(result.Unchanged);

        // One XML update: the stored text with only those four attribute values replaced.
        var written = Assert.Single(h.Writes);
        var expected = h.Original.Replace("\"Name\"", "\"name\"").Replace("\"TELEPHONE1\"", "\"telephone1\"")
            .Replace("\"Mayo_PriorityScore\"", $"\"{ContributorFormFixture.Priority}\"").Replace("\"NAME\"", "\"name\"");
        Assert.Equal(expected, written);
        FormXmlValidator.Validate(XDocument.Parse(written));
        Assert.Equal(2, h.ReadRequests); // the plan read and the read-back
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
        h.AssertIsolation();

        // Against a mock that stores the submitted text verbatim, a second run plans nothing. This proves the planner's
        // idempotence only; how Dataverse stores the value is not observed offline.
        var retry = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Empty(retry.Repaired);
        Assert.False(retry.FormXmlChanged);
        Assert.False(retry.ReadBackVerified);
        Assert.Single(h.Writes);
    }

    [Fact]
    [Trait("AC", "AC-73")]
    public async Task Repair_PreservesNonNormalisedStoredText_ExceptTheRepairedValues()
    {
        // Not pre-normalised: XML declaration, comment, CRLF and tab indentation, a CDATA section with CRLF inside, a
        // whitespace-only element value, a single-quoted binding with spaces around '=', a character reference in a label,
        // and attributes in a non-default order.
        var doc = XDocument.Parse(ContributorFormFixture.Build("base"));
        Control(doc, "name").SetAttributeValue("datafieldname", "Name");
        Control(doc, "telephone1").SetAttributeValue("datafieldname", "TELEPHONE1");
        var notes = doc.Descendants("control").Single(c => (string?)c.Attribute("id") == "notescontrol").Element("parameters")!;
        notes.Element("UClientActivities")!.Value = "WHITESPACE_ONLY";
        notes.Element("UClientActivityCardMap")!.ReplaceNodes(new XCData("{\"email\":\r\n{\"title\":\"subject\"}}"));
        var pretty = doc.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("  ", "\t");
        var raw = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<!-- exported by hand -->\r\n" + pretty
            .Replace("datafieldname=\"Name\"", "datafieldname = 'Name'")
            .Replace("description=\"Authored account label\"", "description=\"Authored &#97;ccount label\"")
            .Replace("WHITESPACE_ONLY", "   ") + "\r\n";
        Assert.Contains("datafieldname = 'Name'", raw);
        Assert.Contains("<![CDATA[", raw);
        Assert.Contains("<UClientActivities>   </UClientActivities>", raw);

        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, rawXml: raw);
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);

        Assert.Equal(2, result.Repaired.Count);
        Assert.True(result.ReadBackVerified);
        var expected = raw.Replace("datafieldname = 'Name'", "datafieldname = 'name'")
            .Replace("datafieldname=\"TELEPHONE1\"", "datafieldname=\"telephone1\"");
        Assert.Equal(expected, Assert.Single(h.Writes)); // byte-for-byte apart from the two values
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public async Task Repair_BindingSpelledWithCharacterReference_IsReportedNotRewritten()
    {
        var raw = ContributorFormFixture.Build("base").Replace("datafieldname=\"name\"", "datafieldname=\"&#78;ame\"");
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, rawXml: raw);
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Empty(result.Repaired);
        var item = Assert.Single(result.Unchanged);
        Assert.Equal((BindingRepairReasons.BindingNotPlainText, "Name", "name"), (item.Reason, item.Binding, item.CanonicalName));
        Assert.Empty(h.Writes);
    }

    // ── AC-74: reported, never rewritten ────────────────────────────────────

    [Fact]
    [Trait("AC", "AC-74")]
    public async Task Repair_UnknownBinding_AndControlIdCase_AreReportedWithoutListingARepairedControlTwice()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: doc =>
        {
            MisCasedIdAndUnknownBinding(doc);
            // An exact binding whose id is mis-cased: reported, nothing to rewrite.
            AddGeneralControl(doc, "{ffffffff-ffff-ffff-ffff-fffffffffff2}", "Telephone1", "telephone1");
        });

        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);

        var repaired = Assert.Single(result.Repaired);
        Assert.Equal(("Name", "Name", "name", true), (repaired.ControlId, repaired.OldBinding, repaired.NewBinding, repaired.ControlIdCaseDiffers));
        Assert.DoesNotContain(result.Unchanged, u => u.ControlId == "Name");
        Assert.Collection(result.Unchanged,
            unknown =>
            {
                Assert.Equal(BindingRepairReasons.NoMatchingColumn, unknown.Reason);
                Assert.Equal(("new_gone", "New_Gone"), (unknown.ControlId, unknown.Binding));
                Assert.Null(unknown.CanonicalName);
                Assert.Equal("My Tab / General", unknown.Location);
            },
            id =>
            {
                Assert.Equal(BindingRepairReasons.ControlIdCaseDiffers, id.Reason);
                Assert.Equal(("Telephone1", "telephone1", "telephone1"), (id.ControlId, id.Binding, id.CanonicalName));
            });

        var written = Assert.Single(h.Writes);
        Assert.Contains("id=\"Name\"", written);
        Assert.Contains("id=\"Telephone1\"", written);
        Assert.Contains("datafieldname=\"New_Gone\"", written);
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public async Task Repair_ReportsButNeverRewrites_RelatedUnboundCustomParameterEventAndHiddenData()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: doc =>
        {
            // Rewritten: a plain mis-cased header binding.
            doc.Root!.Add(Area("header", "{cdcdcdcd-cdcd-cdcd-cdcd-cdcdcdcdcdcd}", "{abababab-abab-abab-abab-abababababab}",
                "header_mayo_priorityscore", "Mayo_PriorityScore"));
            // Not rewritten: relationship and isunbound controls; a related control that matches nothing is not reported.
            Control(doc, "telephone1").SetAttributeValue("datafieldname", "Telephone1");
            Control(doc, "Telephone1").SetAttributeValue("relationship", "account_parent_account");
            AddGeneralControl(doc, "{ffffffff-ffff-ffff-ffff-fffffffffff3}", "unbound_name", "NAME", new XAttribute("isunbound", "true"));
            AddGeneralControl(doc, "{ffffffff-ffff-ffff-ffff-fffffffffff4}", "related_other", "parent_only_column",
                new XAttribute("relationship", "account_parent_account"));
            // Not rewritten: a custom-control parameter, an event attribute and a hidden data entry.
            doc.Descendants("controlDescription").Elements("customControl").Elements("parameters").Elements("datafieldname")
                .First(p => p.Value == "name").Value = "Name";
            doc.Root!.Add(new XElement("events", new XElement("event", new XAttribute("name", "onchange"),
                new XAttribute("application", "false"), new XAttribute("active", "false"), new XAttribute("attribute", "TELEPHONE1"))));
            doc.Root!.Add(new XElement("hiddencontrols", new XElement("data", new XAttribute("id", "hidden_priority"),
                new XAttribute("datafieldname", "MAYO_PRIORITYSCORE"), new XAttribute("classid", ControlTypeFixture.TypeId))));
        });

        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);

        Assert.Equal("Mayo_PriorityScore", Assert.Single(result.Repaired).OldBinding);
        Assert.Equal(new[]
        {
            (BindingRepairReasons.RelatedOrUnboundControl, "NAME"),
            (BindingRepairReasons.RelatedOrUnboundControl, "Telephone1"),
            (BindingRepairReasons.CustomControlParameterCaseDiffers, "Name"),
            (BindingRepairReasons.EventAttributeCaseDiffers, "TELEPHONE1"),
            (BindingRepairReasons.HiddenDataCaseDiffers, "MAYO_PRIORITYSCORE"),
        }, result.Unchanged.Select(u => (u.Reason, u.Binding)));
        Assert.Equal("authored_name_control", result.Unchanged[2].ControlId); // the parameter's control, found by uniqueid
        Assert.Equal("My Tab / General", result.Unchanged[2].Location);
        Assert.All(result.Unchanged, u => Assert.True(BindingRepairReasons.IsRemainingCaseVariant(u.Reason)));

        var expected = h.Original.Replace("datafieldname=\"Mayo_PriorityScore\"", $"datafieldname=\"{ContributorFormFixture.Priority}\"");
        Assert.Equal(expected, Assert.Single(h.Writes));
        Assert.True(result.ReadBackVerified);
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public void Editor_DescribesTabHeaderAndFooterLocations()
    {
        var doc = XDocument.Parse(ContributorFormFixture.Build("base"));
        var tab = doc.Descendants("tab").First();
        tab.Element("labels")!.AddAfterSelf(
            Area("tabheader", "{12121212-1212-1212-1212-121212121212}", "{13131313-1313-1313-1313-131313131313}", "th", "Name"),
            Area("tabfooter", "{14141414-1414-1414-1414-141414141414}", "{15151515-1515-1515-1515-151515151515}", "tf", "TELEPHONE1"));
        var plan = FormXmlEditor.PlanBindingRepair(doc.ToString(SaveOptions.DisableFormatting), ["name", "telephone1", ContributorFormFixture.Priority]);
        Assert.Equal(new[] { ("My Tab / Tab header", (string?)null), ("My Tab / Tab footer", null) },
            plan.Repaired.Select(r => (r.Location, r.SectionId)));
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

    // ── AC-75: nothing to repair ────────────────────────────────────────────

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
        Assert.False(result.ReadBackVerified);
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
    [Trait("AC", "AC-75")]
    public async Task Repair_EmptyColumnList_FailsWithoutWriting()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere, noColumns: true);
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.ColumnNotFound, error.ErrorCode);
        Assert.Empty(h.Writes);
    }

    // ── AC-76: dry run ──────────────────────────────────────────────────────

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
        Assert.False(plan.ReadBackVerified);
        Assert.False(plan.SolutionAssociationRequested);
        Assert.False(plan.PublicationRequested);
        Assert.Equal(4, plan.Repaired.Count);
        Assert.Empty(h.Writes);
        Assert.DoesNotContain("confirmation", h.Journal);
        Assert.Equal(1, h.ReadRequests);
        Assert.Equal(h.Original, h.StateXml);

        var real = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere);
        var applied = await real.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Equal(plan.Repaired, applied.Repaired);
        Assert.Equal(plan.Unchanged, applied.Unchanged);
    }

    [Theory]
    [Trait("AC", "AC-76")]
    [InlineData(true, null)]
    [InlineData(false, "fixture_solution")]
    public async Task DryRun_WithPublishOrSolution_IsRejectedByTheService(bool publish, string? solution)
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere);
        var error = await Assert.ThrowsAsync<PpdsValidationException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", DryRun: true, SolutionUniqueName: solution, Publish: publish), ct: cts.Token));
        Assert.Contains("dry run", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, h.Acquisitions);
        Assert.Empty(h.Journal);
    }

    [Fact]
    [Trait("AC", "AC-76")]
    public async Task DryRun_SuppliedTargetMustMatch_ButNeedsNoAcknowledgement()
    {
        using var cts = new CancellationTokenSource();
        var mismatch = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere);
        var error = await Assert.ThrowsAsync<PpdsException>(() => mismatch.Service.RepairBindingsAsync(
            new("account", "Fixture", DryRun: true, ConfirmEnvironment: "https://other.example.test"), ct: cts.Token));
        Assert.Equal(EnvironmentConfirmationErrorCodes.TargetMismatch, error.ErrorCode);
        Assert.Equal(0, mismatch.Acquisitions);

        var match = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { DenyConfirmation = true };
        var plan = await match.Service.RepairBindingsAsync(
            new("account", "Fixture", DryRun: true, ConfirmEnvironment: "https://FORMS.example.test/"), ct: cts.Token);
        Assert.Equal(4, plan.Repaired.Count);
        Assert.DoesNotContain("confirmation", match.Journal);
        Assert.Empty(match.Writes);
    }

    // ── AC-77: guards ───────────────────────────────────────────────────────

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task DeniedConfirmation_NonCustomizable_NonMainForm_AndCancellation_DoNotWrite()
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

        foreach (var dryRun in new[] { false, true })
        {
            var quickCreate = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { FormType = 7 };
            var typeError = await Assert.ThrowsAsync<PpdsException>(() =>
                quickCreate.Service.RepairBindingsAsync(new("account", "Fixture", DryRun: dryRun), ct: cts.Token));
            Assert.Equal(FormErrorCodes.FormNotFound, typeError.ErrorCode);
            Assert.Contains("not a Main form", typeError.Message);
            Assert.Empty(quickCreate.Writes);
        }

        var cancelled = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { BeforeMetadata = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Empty(cancelled.Writes);
        Assert.Equal(0, cancelled.SolutionRequests + cancelled.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task CancellationBetweenPlanAndWrite_DoesNotWrite()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { AllowExplicitSideEffects = true };
        var reporter = new Mock<IProgressReporter>();
        reporter.Setup(r => r.ReportPhase("Writing", It.IsAny<string?>())).Callback(cts.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), reporter.Object, cts.Token));
        reporter.Verify(r => r.ReportPhase("Writing", It.IsAny<string?>()), Times.Once);
        Assert.Empty(h.Writes);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ValidationFailure_PreventsTheWrite_OnDryRunAndRealRun()
    {
        foreach (var dryRun in new[] { true, false })
        {
            using var cts = new CancellationTokenSource();
            // A duplicate instance GUID (two cells share an id) fails the bundled validator.
            var h = new ContributorFormHarness(cts.Token, transform: doc =>
            {
                MisCasedEverywhere(doc);
                doc.Descendants("cell").Last().SetAttributeValue("id", (string?)doc.Descendants("cell").First().Attribute("id"));
            });
            var error = await Assert.ThrowsAnyAsync<PpdsException>(() =>
                h.Service.RepairBindingsAsync(new("account", "Fixture", DryRun: dryRun), ct: cts.Token));
            Assert.Equal(FormErrorCodes.DuplicateGuid, error.ErrorCode);
            Assert.Empty(h.Writes);
        }
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ReadBackStillMisCased_FailsAsNotVerified_BeforeAssociationOrPublication()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(cts.Token, transform: MisCasedEverywhere) { DiscardWrites = true, AllowExplicitSideEffects = true };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("was submitted", error.Message);
        Assert.Contains("No solution association or publication was attempted", error.Message);
        Assert.Single(h.Writes);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
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
