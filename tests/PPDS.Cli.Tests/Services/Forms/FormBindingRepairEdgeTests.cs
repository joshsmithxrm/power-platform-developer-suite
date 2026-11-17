using System.Xml.Linq;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Forms;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>
/// Edge cases of forms repair-bindings (#1527): text splicing shapes, what is never rewritten, read-back verification and
/// error reporting after the write. Offline only; the synthetic server's storage is not Dataverse behaviour.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FormBindingRepairEdgeTests
{
    private const string V = "{{V}}";

    private static string Form(string nl, string rows, string rootExtra = "")
        => "<form>" + nl + "<tabs><tab name=\"t\"><labels><label description=\"T\" languagecode=\"1033\" /></labels><columns><column><sections>" + nl +
           "<section id=\"{44444444-4444-4444-4444-444444444444}\"><labels><label description=\"S\" languagecode=\"1033\" /></labels><rows>" + nl +
           rows + nl + "</rows></section></sections></column></columns></tab></tabs>" + rootExtra + nl + "</form>";

    private static string Row(string control) => "<row><cell>" + control + "</cell></row>";

    public static TheoryData<string, string> SpliceShapes()
    {
        var padding = new string('p', 10_000);
        return new()
        {
            { "lone CR", Form("\r", Row($"<control id=\"c1\" datafieldname=\"{V}\" />") + "\r" + Row($"<control id=\"c2\" datafieldname=\"{V}\" />")) },
            { "LF only", Form("\n", Row($"<control id=\"c1\" datafieldname=\"{V}\" />") + "\n" + Row($"<control id=\"c2\" datafieldname=\"{V}\" />")) },
            { "mixed endings", Form("\r\n", Row($"<control id=\"c1\" datafieldname=\"{V}\" />") + "\n" +
                Row($"<control id=\"c2\" datafieldname=\"{V}\" />") + "\r" + Row($"<control id=\"c3\" datafieldname=\"{V}\" />")) },
            { "start tag across lines", Form("\n", Row($"<control id=\"c1\"\r\n  title=\"first\nsecond\rthird\"\r\n\tdatafieldname\r\n=\r\n'{V}' />")) },
            { "surrogate pairs earlier on the line", Form("\n", "<row><cell id=\"\U0001F600\U0001F600\"><control id=\"x\U0001F600\" datafieldname=\"" + V + "\" /></cell></row>") },
            { "beyond 4096 characters on one line", Form("", $"<row padding=\"{padding}\"><cell><control id=\"c1\" datafieldname=\"{V}\" /></cell></row>") },
            { "single-line document with several repairs", Form("", Row($"<control id=\"c1\" datafieldname=\"{V}\" />") + $"<row padding=\"{padding}\" />" +
                Row($"<control id=\"c2\" datafieldname=\"{V}\" />")) },
            { "look-alikes in comment, CDATA and other attributes", Form("\n",
                "<!-- datafieldname=\"Name\" -->" + Row($"<control id=\"c1\" datafieldnameX=\"Name\" xdatafieldname=\"Name\" datafieldname=\"{V}\" />") +
                Row("<control id=\"c2\" datafieldname=\"other\"><parameters><p><![CDATA[<control datafieldname=\"Name\" />]]></p></parameters></control>")) },
            { "isunbound false is repaired", Form("\n", Row($"<control id=\"c1\" isunbound=\"false\" datafieldname=\"{V}\" />")) },
        };
    }

    [Theory]
    [Trait("AC", "AC-73")]
    [MemberData(nameof(SpliceShapes))]
    public void Splice_ReplacesExactlyTheBindingValues(string shape, string template)
    {
        var raw = template.Replace(V, "Name");
        var plan = FormXmlEditor.PlanBindingRepair(raw, ["name", "other_column"]);
        Assert.Equal(CountOf(template, V), plan.Repaired.Count);
        Assert.True(template.Replace(V, "name") == plan.RepairedXml, shape);
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    [Trait("AC", "AC-74")]
    public void ControlsOutsideTheFormLayout_AreReportedAndNeverRewritten()
    {
        // A cell/control look-alike inside custom-control parameters (the XSD allows any content there).
        var raw = Form("\n", Row("<control id=\"c1\" uniqueid=\"{11111111-1111-1111-1111-111111111111}\" datafieldname=\"Name\" />"),
            "<controlDescriptions><controlDescription forControl=\"{11111111-1111-1111-1111-111111111111}\"><customControl id=\"{4273EDBD-AC1D-40d3-9FB2-095C621B552D}\">" +
            "<parameters><layout><cell><control datafieldname=\"Name\" /></cell></layout></parameters></customControl></controlDescription></controlDescriptions>");
        var plan = FormXmlEditor.PlanBindingRepair(raw, ["name"]);
        var repaired = Assert.Single(plan.Repaired);
        Assert.Equal(("c1", "T / S"), (repaired.ControlId, repaired.Location));
        var outside = Assert.Single(plan.Unchanged);
        Assert.Equal((BindingRepairReasons.OutsideFormLayout, BindingLocations.OutsideFormLayout), (outside.Reason, outside.Location));
        Assert.Equal(raw.Replace("id=\"c1\" uniqueid=\"{11111111-1111-1111-1111-111111111111}\" datafieldname=\"Name\"",
            "id=\"c1\" uniqueid=\"{11111111-1111-1111-1111-111111111111}\" datafieldname=\"name\""), plan.RepairedXml);
        Assert.Contains("<layout><cell><control datafieldname=\"Name\" />", plan.RepairedXml);
        Assert.DoesNotContain(plan.Repaired, r => r.Location == "(unknown)");
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public void UnboundRelatedEventsAndHiddenData_FollowTheirRules()
    {
        var raw = Form("\n",
            Row("<control id=\"u1\" isunbound=\"1\" datafieldname=\"Name\" />") + Row("<control id=\"u2\" isunbound=\"false\" datafieldname=\"Name\" />"),
            "<events><event name=\"onchange\" attribute=\"Name\" relationship=\"rel\" /><event name=\"onload\" attribute=\"NAME\" /></events>" +
            "<hiddencontrols><data id=\"h1\" datafieldname=\"Name\" relationship=\"rel\" /><data id=\"h2\" datafieldname=\"NAME\" /></hiddencontrols>");
        var plan = FormXmlEditor.PlanBindingRepair(raw, ["name"]);
        Assert.Equal("u2", Assert.Single(plan.Repaired).ControlId);
        Assert.Equal(new (string, string?)[]
        {
            (BindingRepairReasons.RelatedOrUnboundControl, "u1"),
            (BindingRepairReasons.EventAttributeCaseDiffers, null),
            (BindingRepairReasons.HiddenDataCaseDiffers, "h2"),
        }, plan.Unchanged.Select(u => (u.Reason, u.ControlId)));
    }

    [Fact]
    [Trait("AC", "AC-74")]
    public void ColumnsDifferingOnlyByCase_ExactBindingIsKept_AndOtherSpellingIsAmbiguous()
    {
        var raw = Form("\n", Row("<control id=\"c1\" datafieldname=\"name\" />") + Row("<control id=\"c2\" datafieldname=\"Name\" />"));
        var plan = FormXmlEditor.PlanBindingRepair(raw, ["NAME", "name"]);
        Assert.Empty(plan.Repaired);
        var ambiguous = Assert.Single(plan.Unchanged);
        Assert.Equal<(string, string?, string?)>((BindingRepairReasons.AmbiguousColumn, "c2", null), (ambiguous.Reason, ambiguous.ControlId, ambiguous.CanonicalName));
        Assert.Equal(raw, plan.RepairedXml);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public void DoctypeAndEmptyXml_AreRefused()
    {
        var doctype = "<!DOCTYPE form [<!ENTITY c \"<control id='c1' datafieldname='Name' />\">]>" + Form("\n", "<row><cell>&c;</cell></row>");
        var doctypeError = Assert.Throws<PpdsException>(() => FormXmlEditor.PlanBindingRepair(doctype, ["name"]));
        Assert.Equal(FormErrorCodes.InvalidFormXml, doctypeError.ErrorCode);
        Assert.Contains("a DOCTYPE is not supported", doctypeError.Message); // pins DtdProcessing.Prohibit
        var truncated = Assert.Throws<PpdsException>(() => FormXmlEditor.PlanBindingRepair("<form><tabs>", ["name"]));
        Assert.DoesNotContain("DOCTYPE", truncated.Message);
        Assert.Equal(FormErrorCodes.InvalidFormXml, Assert.Throws<PpdsException>(() => FormXmlEditor.PlanBindingRepair("", ["name"])).ErrorCode);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public void LocateAttributeValue_FailsClosed_WithoutLineInfoOrWhenTheTextDisagrees()
    {
        var raw = Form("\n", Row("<control id=\"c1\" datafieldname=\"Name\" />"));
        var detached = new XAttribute("datafieldname", "Name");
        Assert.Throws<PpdsException>(() => FormXmlEditor.LocateAttributeValue(raw, FormXmlEditor.LineStartsFor(raw), detached));

        var parsed = XDocument.Parse(raw, LoadOptions.SetLineInfo).Descendants("control").Single().Attribute("datafieldname")!;
        var shifted = raw.Replace("<control", "  <control"); // the line information no longer points at the attribute
        Assert.Throws<PpdsException>(() => FormXmlEditor.LocateAttributeValue(shifted, FormXmlEditor.LineStartsFor(shifted), parsed));
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task EmptyStoredFormXml_FailsWithoutWriting()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, rawXml: "");
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.InvalidFormXml, error.ErrorCode);
        Assert.Contains("no form XML", error.Message);
        Assert.Empty(h.Writes);
    }

    // ── Read-back verification ──────────────────────────────────────────────

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ReadBack_VerifiesRewrittenControls_AndReportsNewlyRepairableOnesWithoutFailing()
    {
        // TELEPHONE1 is stored with a character reference, so it is reported, not rewritten. The synthetic server then
        // returns it as plain text: it becomes repairable on read-back, which is reported, not a verification failure.
        var raw = ContributorFormFixture.Build("base")
            .Replace("datafieldname=\"name\"", "datafieldname=\"Name\"")
            .Replace("datafieldname=\"telephone1\"", "datafieldname=\"&#84;ELEPHONE1\"");
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, rawXml: raw) { ReadBackTransform = xml => xml.Replace("&#84;ELEPHONE1", "TELEPHONE1") };
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.True(result.ReadBackVerified);
        Assert.Equal("Name", Assert.Single(result.Repaired).OldBinding);
        Assert.Equal(BindingRepairReasons.BindingNotPlainText, Assert.Single(result.Unchanged).Reason);
        Assert.Equal(("TELEPHONE1", "telephone1"), (Assert.Single(result.ReadBackRepairable).OldBinding, result.ReadBackRepairable[0].NewBinding));
    }

    [Theory]
    [Trait("AC", "AC-77")]
    [InlineData("reverted", "is bound to 'Name', not 'name'")]
    [InlineData("removed", "is missing")]
    public async Task ReadBack_ThatDoesNotShowARewrittenControl_IsNotVerified(string change, string expected)
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
        {
            AllowExplicitSideEffects = true,
            ReadBackTransform = xml =>
            {
                var doc = XDocument.Parse(xml);
                var control = doc.Descendants("control").Single(c => (string?)c.Attribute("id") == "authored_name_control");
                if (change == "reverted") control.SetAttributeValue("datafieldname", "Name"); else control.Parent!.Remove();
                return doc.ToString(SaveOptions.DisableFormatting);
            }
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("was submitted", error.Message);
        Assert.Contains(expected, error.Message);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ReadBack_ThatThrows_IsNotVerified_AndSaysTheUpdateWasSubmitted()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailReadBackWith = () => new TimeoutException("synthetic read-back timeout") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("was submitted, but the form could not be read back (synthetic read-back timeout)", error.Message);
        Assert.Single(h.Writes);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task CancellationDuringReadBack_IsNotVerified_AndSaysTheUpdateWasSubmitted()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere);
        var reporter = new Mock<IProgressReporter>();
        reporter.Setup(r => r.ReportPhase("Verifying", It.IsAny<string?>())).Callback(cts.Cancel);
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), reporter.Object, cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("was submitted, but reading it back was cancelled", error.Message);
        Assert.Single(h.Writes);
    }

    // ── Errors after or around the write keep their classification and say what state was reached ──

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task Success_WithSolutionAndPublish_RunsUpdateReadBackAssociationPublicationInOrder()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere) { AllowExplicitSideEffects = true };
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token);
        Assert.True(result.ReadBackVerified && result.SolutionAssociationRequested && result.PublicationRequested);
        var update = h.Journal.IndexOf("update-attempt");
        var readBack = h.Journal.LastIndexOf("read-unpublished");
        var association = h.Journal.IndexOf("explicit-solution-request");
        var publication = h.Journal.IndexOf("explicit-publication-request");
        Assert.True(update >= 0 && update < readBack && readBack < association && association < publication, string.Join(",", h.Journal));
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task AssociationFailureAfterAVerifiedWrite_KeepsItsClassification_AndSaysTheBindingsWereWritten()
    {
        using var cts = new CancellationTokenSource();
        var timeout = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailSolutionWith = () => new TimeoutException("synthetic association timeout") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => timeout.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(ErrorCodes.Connection.Timeout, error.ErrorCode);
        Assert.Equal(ExitCodes.ConnectionError, ExceptionMapper.ToExitCode(error));
        Assert.Contains("4 binding(s) on form 'Fixture' were written and verified", error.Message);
        Assert.Contains("the solution association then did not complete and its outcome is unknown (synthetic association timeout)", error.Message);
        Assert.Contains("Publication was not attempted", error.Message);
        Assert.Equal(0, timeout.PublishRequests);

        var fault = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailSolutionWith = () => new InvalidOperationException("synthetic fault") };
        var faultError = await Assert.ThrowsAsync<PpdsException>(() => fault.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.SolutionAssociationFailed, faultError.ErrorCode);
        Assert.Contains("the solution association then failed: synthetic fault", faultError.Message);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task PublicationFailure_IsReportedWithItsOwnCode_AfterWriteOrNoOp()
    {
        using var cts = new CancellationTokenSource();
        var written = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailPublishWith = () => new InvalidOperationException("synthetic publish fault") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => written.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(FormErrorCodes.PublicationFailed, error.ErrorCode);
        Assert.Contains("were written and verified", error.Message);
        Assert.Equal(1, written.SolutionRequests);

        var noop = new ContributorFormHarness(readBackById: true, token: cts.Token)
            { AllowExplicitSideEffects = true, FailPublishWith = () => new InvalidOperationException("synthetic publish fault") };
        var noopError = await Assert.ThrowsAsync<PpdsException>(() => noop.Service.RepairBindingsAsync(new("account", "Fixture", Publish: true), ct: cts.Token));
        Assert.Equal(FormErrorCodes.PublicationFailed, noopError.ErrorCode);
        Assert.Contains("needed repair and nothing was written", noopError.Message);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task UpdateTimeout_IsAnUnknownOutcome_ClassifiedAsATimeout_WithNoSideEffects()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { LoseFirstUpdateAcknowledgement = true, AllowExplicitSideEffects = true };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(ErrorCodes.Connection.Timeout, error.ErrorCode);
        Assert.Equal(ExitCodes.ConnectionError, ExceptionMapper.ToExitCode(error));
        Assert.Contains("outcome is unknown", error.Message);
        Assert.Contains("No solution association or publication was attempted", error.Message);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task MetadataReadTimeout_KeepsItsClassification_AndSaysNothingWasWritten()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { BeforeMetadata = () => throw new TimeoutException("synthetic metadata timeout") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(ErrorCodes.Connection.Timeout, error.ErrorCode);
        Assert.Contains("Nothing was written", error.Message);
        Assert.Empty(h.Writes);
    }
}
