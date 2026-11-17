using System.ServiceModel;
using System.Xml.Linq;
using Microsoft.Xrm.Sdk;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>
/// Read-back identity, error classification by phase and owner-rewritten references for forms repair-bindings (#1527).
/// Offline only: the synthetic server's behaviour is not Dataverse behaviour.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FormBindingRepairConvergenceTests
{
    private static ContributorFormHarness Harness(CancellationToken ct, Action<XDocument>? transform = null)
        => new(readBackById: true, token: ct, transform: transform ?? FormBindingRepairTests.MisCasedEverywhere);

    private static XElement Control(XDocument doc, string id) => doc.Descendants("control").Single(c => (string?)c.Attribute("id") == id);

    private static XElement BoundCell(string cellId, string? controlId, string binding)
        => new("row", new XElement("cell", new XAttribute("id", cellId),
            new XElement("control", controlId is null ? null : new XAttribute("id", controlId), new XAttribute("datafieldname", binding),
                new XAttribute("classid", ControlTypeFixture.TypeId))));

    // ── S1: read-back that is empty or unparsable ──

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ReadBack_EmptyFormXml_IsNotVerified_WithoutClaimingNothingWasWritten()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { ReadBackTransform = _ => "" };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("was submitted, but the form XML read back is empty or could not be parsed", error.Message);
        Assert.DoesNotContain("Nothing was written", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(h.Writes);
    }

    // ── S2: identity-based verification ──

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ReadBack_WithAnUnrelatedControlAddedBefore_IsVerifiedById()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
        {
            ReadBackTransform = xml =>
            {
                var doc = XDocument.Parse(xml);
                // Another maker's change: a new bound control at the top of the first section shifts every later position.
                ContributorFormFixture.Section(doc, "General").Element("rows")!.AddFirst(
                    BoundCell("{abcdef00-0000-4000-8000-000000000001}", "added_elsewhere", "telephone1"));
                return doc.ToString(SaveOptions.DisableFormatting);
            }
        };
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.True(result.ReadBackVerified);
        Assert.All(result.Repaired, r => Assert.Equal(BindingVerification.ById, r.VerifiedBy));
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task ReadBack_ControlWithUniqueIdMovedToAnotherContainer_IsNotVerified()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
        {
            ReadBackTransform = xml =>
            {
                var doc = XDocument.Parse(xml);
                var row = Control(doc, "authored_name_control").Parent!.Parent!;
                row.Remove();
                ContributorFormFixture.Section(doc, "Other").Element("rows")!.Add(row);
                return doc.ToString(SaveOptions.DisableFormatting);
            }
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("control 'authored_name_control' (My Tab / General) was found in a different place", error.Message);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task IdlessAndDuplicatedIds_AreVerifiedByPosition_AndAShiftIsReportedAsNotAtItsPosition()
    {
        static void Transform(XDocument doc)
        {
            var rows = ContributorFormFixture.Section(doc, "Other").Element("rows")!;
            rows.Add(BoundCell("{abcdef00-0000-4000-8000-000000000011}", null, "Name"));
            rows.Add(BoundCell("{abcdef00-0000-4000-8000-000000000012}", "dup", "NAME"));
            rows.Add(BoundCell("{abcdef00-0000-4000-8000-000000000013}", "dup", "Name"));
        }

        using var cts = new CancellationTokenSource();
        var ok = Harness(cts.Token, Transform);
        var result = await ok.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Equal(3, result.Repaired.Count);
        Assert.All(result.Repaired, r => Assert.Equal(BindingVerification.ByPosition, r.VerifiedBy));

        // The read-back drops the id-less control: the next id-less/duplicate position no longer matches.
        var shifted = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: Transform)
        {
            ReadBackTransform = xml =>
            {
                var doc = XDocument.Parse(xml);
                doc.Descendants("control").Single(c => c.Attribute("id") is null && (string?)c.Attribute("datafieldname") == "name").Parent!.Parent!.Remove();
                return doc.ToString(SaveOptions.DisableFormatting);
            }
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => shifted.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(FormErrorCodes.RepairNotVerified, error.ErrorCode);
        Assert.Contains("control '(no id)' (My Tab / Other) was not found at its expected position", error.Message);
    }

    // ── S3: side effects report what completed, and unknown outcomes ──

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task PublicationFailure_SaysTheAssociationCompleted()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailPublishWith = () => new InvalidOperationException("synthetic publish fault") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.Equal(FormErrorCodes.PublicationFailed, error.ErrorCode);
        Assert.Contains("were written and verified by reading the form back; the solution association to 'fixture_solution' completed; " +
            "the publication then failed: synthetic publish fault.", error.Message);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task PublicationTimeout_IsAnUnknownOutcome()
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailPublishWith = () => new TimeoutException("synthetic publish timeout") };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture", Publish: true), ct: cts.Token));
        Assert.Equal(ErrorCodes.Connection.Timeout, error.ErrorCode);
        Assert.Contains("the publication then did not complete and its outcome is unknown (synthetic publish timeout)", error.Message);
    }

    [Fact]
    [Trait("AC", "AC-77")]
    public async Task CancellationDuringAssociation_IsUnknown_AndBeforePublication_IsNotSent()
    {
        using var during = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: during.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
        {
            AllowExplicitSideEffects = true,
            FailSolutionWith = () => { during.Cancel(); return new OperationCanceledException(during.Token); }
        };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: during.Token));
        Assert.Equal(ErrorCodes.Operation.Cancelled, error.ErrorCode);
        Assert.Contains("the solution association then did not complete and its outcome is unknown", error.Message);
        Assert.Contains("Publication was not attempted", error.Message);
        Assert.Equal(0, h.PublishRequests);

        using var before = new CancellationTokenSource();
        var b = new ContributorFormHarness(readBackById: true, token: before.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, AfterSolutionRequest = before.Cancel };
        var notSent = await Assert.ThrowsAsync<PpdsException>(() => b.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: before.Token));
        Assert.Equal(ErrorCodes.Operation.Cancelled, notSent.ErrorCode);
        Assert.Contains("the solution association to 'fixture_solution' completed; the publication was cancelled before it was sent.", notSent.Message);
        Assert.Equal(0, b.PublishRequests);
    }

    // ── S4 / S7: update and planning failures keep their classification ──

    public static TheoryData<string, Func<CancellationTokenSource, Exception>, string, string> UpdateFailures() => new()
    {
        { "server fault", _ => new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147220891, Message = "synthetic privilege fault" }, "fault"),
            FormErrorCodes.RepairWriteRejected, "Dataverse rejected the update of form 'Fixture' (fault 0x80040265: synthetic privilege fault); this request did not change the form." },
        { "throttle fault", _ => new FaultException<OrganizationServiceFault>(new OrganizationServiceFault { ErrorCode = -2147015902, Message = "synthetic throttle" }, "fault"),
            ErrorCodes.Connection.Throttled, "Dataverse rejected the update" },
        { "auth", _ => new DataverseAuthenticationException("synthetic auth failure", requiresReauthentication: false),
            ErrorCodes.Auth.InsufficientPermissions, "Dataverse rejected the update" },
        { "transport", _ => new InvalidOperationException("synthetic transport failure"),
            FormErrorCodes.RepairWriteOutcomeUnknown, "did not complete and its outcome is unknown" },
        { "unrequested cancellation", _ => new OperationCanceledException("synthetic"),
            ErrorCodes.Connection.Timeout, "cancelled without a cancellation request from the caller, so it is treated as a timeout" },
        { "user cancellation", cts => { cts.Cancel(); return new OperationCanceledException(cts.Token); },
            ErrorCodes.Operation.Cancelled, "did not complete and its outcome is unknown" },
    };

    [Theory]
    [Trait("AC", "AC-77")]
    [MemberData(nameof(UpdateFailures))]
    public async Task UpdateFailure_IsClassified_AndRejectedOrUnknown(string shape, Func<CancellationTokenSource, Exception> failure, string code, string text)
    {
        using var cts = new CancellationTokenSource();
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { AllowExplicitSideEffects = true, FailUpdateWith = () => failure(cts) };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(
            new("account", "Fixture", SolutionUniqueName: "fixture_solution", Publish: true), ct: cts.Token));
        Assert.True(code == error.ErrorCode, $"{shape}: {error.ErrorCode}");
        Assert.Contains(text, error.Message);
        Assert.Contains("No solution association or publication was attempted", error.Message);
        Assert.Empty(h.Writes);
        Assert.Equal(0, h.SolutionRequests + h.PublishRequests);
    }

    [Theory]
    [Trait("AC", "AC-77")]
    [InlineData("generic", FormErrorCodes.RepairFailed)]
    [InlineData("throttle", ErrorCodes.Connection.Throttled)]
    [InlineData("auth", ErrorCodes.Auth.Expired)]
    public async Task PlanningFailure_KeepsItsClassification_AndSaysNothingWasWritten(string shape, string code)
    {
        using var cts = new CancellationTokenSource();
        Exception failure = shape switch
        {
            "throttle" => new ServiceProtectionException("synthetic-connection", TimeSpan.FromSeconds(5), -2147015902),
            "auth" => new DataverseAuthenticationException("synthetic expired token", requiresReauthentication: true),
            _ => new InvalidOperationException("synthetic metadata failure")
        };
        var h = new ContributorFormHarness(readBackById: true, token: cts.Token, transform: FormBindingRepairTests.MisCasedEverywhere)
            { BeforeMetadata = () => throw failure };
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token));
        Assert.Equal(code, error.ErrorCode);
        Assert.Contains("Nothing was written", error.Message);
        Assert.Empty(h.Writes);
    }

    // ── S5: references that belong to a rewritten control ──

    [Fact]
    [Trait("AC", "AC-74")]
    public async Task ParameterAndEventOfARewrittenControl_AreMarkedOwnerRewritten()
    {
        using var cts = new CancellationTokenSource();
        var h = Harness(cts.Token, doc =>
        {
            doc.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "name").SetAttributeValue("datafieldname", "Name");
            doc.Descendants("controlDescription").Elements("customControl").Elements("parameters").Elements("datafieldname")
                .First(p => p.Value == "name").Value = "Name";
            doc.Root!.Add(new XElement("events", new XElement("event", new XAttribute("name", "onchange"), new XAttribute("application", "false"),
                new XAttribute("active", "false"), new XAttribute("attribute", "NAME"), new XAttribute("control", "authored_name_control"))));
        });
        var result = await h.Service.RepairBindingsAsync(new("account", "Fixture"), ct: cts.Token);
        Assert.Equal("authored_name_control", Assert.Single(result.Repaired).ControlId);
        Assert.Equal(new (string, string?, bool)[]
        {
            (BindingRepairReasons.CustomControlParameterCaseDiffers, "authored_name_control", true),
            (BindingRepairReasons.EventAttributeCaseDiffers, "authored_name_control", true),
        }, result.Unchanged.Select(u => (u.Reason, u.ControlId, u.OwnerControlRewritten)));
        // The rewritten control's own binding is not listed in Unchanged.
        Assert.DoesNotContain(result.Unchanged, u => u.Binding == "Name" && u.Reason == BindingRepairReasons.ControlIdCaseDiffers);
    }
}
