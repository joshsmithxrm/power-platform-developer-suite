using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Environment;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using PPDS.Dataverse.Pooling;
using Xunit;
using Xunit.Abstractions;

namespace PPDS.Cli.Tests.Services.Forms;

/// <summary>
/// Observation harness for #1528/#1529, not acceptance tests for unresolved policies.
/// A passing row means the synthetic fixture/mocks were valid and observations were
/// captured. It never means duplicate/copy/drop behavior is an approved fix.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Purpose", "ContributorCharacterizationOnly")]
public sealed class FormContributorCharacterizationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("repeat-selected-section")]
    [InlineData("existing-casing-batch")]
    [InlineData("new-duplicate-batch")]
    [InlineData("mixed-existing-and-new")]
    [InlineData("intentional-elsewhere-add")]
    [InlineData("applied-update-lost-ack-retry")]
    [InlineData("new-reorder-metadata-label")]
    [InlineData("empty-display-name-reorder")]
    [InlineData("cross-section-reorder")]
    [InlineData("ambiguous-cross-section-reorder")]
    [InlineData("existing-cell-reorder")]
    [InlineData("drop-unlisted-reorder")]
    [InlineData("duplicate-existing-binding-reorder")]
    [InlineData("repeated-existing-reorder-name")]
    [InlineData("rowless-reorder")]
    public async Task Observe_CurrentService_WithoutSelectingDesiredPolicy(string scenario)
    {
        using var cancellation = new CancellationTokenSource();
        var plan = ObservationPlan.For(scenario);
        var harness = new ContributorFormHarness(cancellation.Token, plan.Fixture,
            emptyDisplayName: plan.EmptyDisplayName) { LoseFirstUpdateAcknowledgement = plan.LostAcknowledgement };
        harness.AssertPreconditions(plan.Fixture);

        for (var index = 0; index < plan.Steps.Length; index++)
        {
            var step = plan.Steps[index];
            var before = harness.Snapshot();
            var writesBefore = harness.Writes.Count;
            var error = await Record.ExceptionAsync(() => step.Reorder
                ? harness.Service.ReorderFieldsAsync(new("account", "Fixture", "General", step.Fields), ct: cancellation.Token)
                : harness.Service.AddFieldAsync(new("account", "Fixture", "General", step.Fields), ct: cancellation.Token));
            Assert.True(error is null or PpdsException or OperationCanceledException,
                "Unexpected harness/setup failure must not count as characterization.");
            for (var cause = error; cause is not null; cause = cause.InnerException)
                Assert.False(cause is MockException or Xunit.Sdk.XunitException,
                    "An unconfigured SDK/provider path or failed fixture assertion invalidates the observation.");
            harness.AssertIsolation();
            output.WriteLine("PPDS_FORMS_CHARACTERIZATION=" + JsonSerializer.Serialize(new
            {
                Scenario = scenario, Step = index + 1, Operation = step.Reorder ? "reorder" : "add",
                RequestedFields = step.Fields, Fixture = plan.Fixture,
                EvidenceKind = "mock-service-observation-not-fix-proof", CharacterizationValid = true,
                FaultModel = plan.LostAcknowledgement ? "synthetic SDK applied first update then returned TimeoutException" : "none",
                ErrorType = error?.GetType().FullName, ErrorCode = (error as PpdsException)?.ErrorCode,
                CauseType = error?.InnerException?.GetType().FullName,
                UpdateAttemptsThisStep = harness.Writes.Count - writesBefore,
                UpdateAttemptsTotal = harness.Writes.Count, AcknowledgedUpdates = harness.AcknowledgedUpdates,
                ReadRequests = harness.ReadRequests, MetadataCalls = harness.MetadataCalls,
                ActiveLeases = harness.ActiveLeases, Journal = harness.Journal.ToArray(),
                SubmittedFormXml = harness.Writes.Skip(writesBefore).ToArray(), Before = before, After = harness.Snapshot()
            }));
        }
    }
}

/// <summary>Existing, unambiguous contracts; these do not choose duplicate/move/drop policy.</summary>
[Trait("Category", "Unit")]
public sealed class FormContributorGuardTests
{
    [Fact]
    public async Task UnknownReorderColumn_FailsBeforeSdkWriteOrSideEffects()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token);
        h.AssertPreconditions("base");
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.ReorderFieldsAsync(
            new("account", "Fixture", "General", ["does_not_exist"], "fixture_solution", true), ct: cancellation.Token));
        Assert.Equal(FormErrorCodes.ColumnNotFound, error.ErrorCode);
        Assert.Empty(h.Writes);
        Assert.Equal(h.Original, h.StateXml);
        h.AssertIsolation();
    }

    [Fact]
    public async Task ExistingCellPermutation_PreservesAuthoredLabelsIdsAndReusableControlTypes()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token);
        h.AssertPreconditions("base");
        var original = XDocument.Parse(h.Original);
        await h.Service.ReorderFieldsAsync(new("account", "Fixture", "General", ["NAME"]), ct: cancellation.Token);
        var written = XDocument.Parse(Assert.Single(h.Writes));
        Assert.True(XNode.DeepEquals(ContributorFormFixture.NameCell(original), ContributorFormFixture.NameCell(written)));
        Assert.True(XNode.DeepEquals(original.Descendants("control").Single(c => (string?)c.Attribute("id") == "notescontrol").Parent,
            written.Descendants("control").Single(c => (string?)c.Attribute("id") == "notescontrol").Parent));
        Assert.True(XNode.DeepEquals(original.Root!.Element("controlDescriptions"), written.Root!.Element("controlDescriptions")));
        Assert.True(XNode.DeepEquals(ContributorFormFixture.Section(original, "Other"), ContributorFormFixture.Section(written, "Other")));
        h.AssertIsolation();
    }

    [Fact]
    public async Task DeniedConfirmation_DoesNotAcquirePoolOrMetadata()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token) { DenyConfirmation = true };
        h.AssertPreconditions("base");
        var error = await Assert.ThrowsAsync<PpdsException>(() => h.Service.AddFieldAsync(
            new("account", "Fixture", "General", [ContributorFormFixture.Priority]), ct: cancellation.Token));
        Assert.Equal("Environment.ReadOnly", error.ErrorCode);
        Assert.Equal(0, h.Acquisitions);
        Assert.Equal(0, h.MetadataCalls);
        Assert.Empty(h.Writes);
        h.AssertIsolation();
    }

    [Fact]
    public async Task CancellationAtMetadataBoundary_PropagatesWithFetchLeaseReleasedAndNoWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var h = new ContributorFormHarness(cancellation.Token) { BeforeMetadata = cancellation.Cancel };
        h.AssertPreconditions("base");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Service.AddFieldAsync(
            new("account", "Fixture", "General", [ContributorFormFixture.Priority]), ct: cancellation.Token));
        Assert.Equal(1, h.ReadRequests);
        Assert.Equal(1, h.MetadataCalls);
        Assert.Empty(h.Writes);
        Assert.Equal(h.Original, h.StateXml);
        h.AssertIsolation();
    }
}

internal sealed record ObservationStep(bool Reorder, params string[] Fields);
internal sealed record ObservationPlan(string Fixture, ObservationStep[] Steps, bool EmptyDisplayName = false, bool LostAcknowledgement = false)
{
    internal static ObservationPlan For(string name) => name switch
    {
        "repeat-selected-section" => new("base", [new(false, ContributorFormFixture.Priority), new(false, "MAYO_PRIORITYSCORE")]),
        "existing-casing-batch" => new("base", [new(false, "NAME", "name")]),
        "new-duplicate-batch" => new("base", [new(false, ContributorFormFixture.Priority, "MAYO_PRIORITYSCORE")]),
        "mixed-existing-and-new" => new("base", [new(false, "NAME", ContributorFormFixture.Priority)]),
        "intentional-elsewhere-add" => new("base", [new(false, "TELEPHONE1")]),
        "applied-update-lost-ack-retry" => new("base", [new(false, ContributorFormFixture.Priority, "telephone1"),
            new(false, ContributorFormFixture.Priority, "telephone1")], LostAcknowledgement: true),
        "new-reorder-metadata-label" => new("base", [new(true, "MAYO_PRIORITYSCORE", "name")]),
        "empty-display-name-reorder" => new("base", [new(true, ContributorFormFixture.Priority, "name")], EmptyDisplayName: true),
        "cross-section-reorder" => new("base", [new(true, "TELEPHONE1", "name")]),
        "ambiguous-cross-section-reorder" => new("phone-in-two-sections", [new(true, "telephone1", "name")]),
        "existing-cell-reorder" => new("name-elsewhere", [new(true, "NAME")]),
        "drop-unlisted-reorder" => new("base", [new(true, ContributorFormFixture.Priority)]),
        "duplicate-existing-binding-reorder" => new("name-twice-selected", [new(true, "name")]),
        "repeated-existing-reorder-name" => new("base", [new(true, "name", "NAME")]),
        "rowless-reorder" => new("rowless", [new(true, ContributorFormFixture.Priority)]),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
}

internal static class ContributorFormFixture
{
    internal const string Priority = "mayo_priorityscore";
    internal static XElement Section(XDocument doc, string label) => doc.Descendants("section").Single(section =>
        (string?)section.Element("labels")?.Element("label")?.Attribute("description") == label);
    internal static XElement NameCell(XDocument doc) => Section(doc, "General").Descendants("cell").First(cell =>
        (string?)cell.Element("control")?.Attribute("datafieldname") == "name");

    internal static string Build(string variant)
    {
        // Both reused fixtures are synthetic and already in the exact B3 test project.
        var doc = XDocument.Parse(ControlTypeFixture.Xml());
        var general = Section(doc, "General");
        var name = NameCell(doc);
        name.AddFirst(new XElement("labels",
            new XElement("label", new XAttribute("description", "Authored account label"), new XAttribute("languagecode", "1033")),
            new XElement("label", new XAttribute("description", "Nom du compte"), new XAttribute("languagecode", "1036"))));
        name.Element("control")!.SetAttributeValue("id", "authored_name_control");
        name.Element("control")!.SetAttributeValue("disabled", "true");
        var phone = doc.Descendants("control").Single(c => (string?)c.Attribute("datafieldname") == "telephone1").Parent!;
        phone.AddFirst(new XElement("labels", new XElement("label", new XAttribute("description", "Authored phone label"),
            new XAttribute("languagecode", "1033"))));
        var phoneRow = phone.Parent!;
        phoneRow.Remove();
        var other = NewSection(general, "Other", "55555555-5555-5555-5555-555555555555");
        other.Element("rows")!.Add(phoneRow);
        general.Parent!.Add(other);
        if (variant == "name-elsewhere")
            other.Element("rows")!.Add(new XElement("row", DuplicateCell(name, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "authored_name_elsewhere")));
        if (variant == "name-twice-selected")
            general.Element("rows")!.Add(new XElement("row", DuplicateCell(name, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "authored_name_second_instance")));
        if (variant == "phone-in-two-sections")
        {
            var third = NewSection(general, "Third", "cccccccc-cccc-cccc-cccc-cccccccccccc");
            third.Element("rows")!.Add(new XElement("row", DuplicateCell(phone, "dddddddd-dddd-dddd-dddd-dddddddddddd",
                "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", "authored_phone_elsewhere")));
            general.Parent!.Add(third);
        }
        if (variant == "rowless")
        {
            // Keep controls and their root references valid while making only the target rowless.
            var rows = general.Element("rows")!;
            foreach (var row in rows.Elements("row").ToArray())
            {
                row.Remove();
                other.Element("rows")!.Add(row);
            }
            rows.Remove();
        }
        if (variant == "unreferenced-unlisted")
            general.Element("rows")!.Add(new XElement("row", new XElement("cell",
                new XAttribute("id", "{ffffffff-ffff-ffff-ffff-fffffffffff1}"),
                new XElement("control", new XAttribute("id", "unlisted_extra"), new XAttribute("datafieldname", "unlisted_extra"),
                    new XAttribute("classid", ControlTypeFixture.TypeId), new XAttribute("disabled", "false")))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    private static XElement NewSection(XElement template, string label, string id) => new("section",
        template.Attributes().Where(a => a.Name != "id" && a.Name != "name").Select(a => new XAttribute(a)),
        new XAttribute("id", "{" + id + "}"), new XAttribute("name", "section_" + label.ToLowerInvariant()),
        new XElement("labels", new XElement("label", new XAttribute("description", label), new XAttribute("languagecode", "1033"))),
        new XElement("rows"));

    private static XElement DuplicateCell(XElement original, string cellId, string uniqueId, string controlId)
    {
        var copy = new XElement(original);
        copy.SetAttributeValue("id", "{" + cellId + "}");
        copy.Element("control")!.SetAttributeValue("uniqueid", "{" + uniqueId + "}");
        copy.Element("control")!.SetAttributeValue("id", controlId);
        return copy;
    }
}

internal sealed class ContributorFormHarness
{
    private static readonly Guid FormId = new("12345678-1234-1234-1234-123456789abc");
    private readonly CancellationToken _token;
    private readonly Mock<IDataverseConnectionPool> _pool = new(MockBehavior.Strict);
    private readonly Mock<IMetadataQueryService> _metadata = new(MockBehavior.Strict);
    private readonly Mock<IEnvironmentConfirmationService> _confirmation = new(MockBehavior.Strict);
    private readonly List<LeaseObservation> _leases = [];
    private bool _lostAcknowledgement;
    internal FormService Service { get; }
    internal string Original { get; }
    internal string StateXml { get; private set; }
    internal List<string> Writes { get; } = [];
    internal List<string> Journal { get; } = [];
    internal int AcknowledgedUpdates { get; private set; }
    internal int ReadRequests { get; private set; }
    internal int MetadataCalls { get; private set; }
    internal int SolutionRequests { get; private set; }
    internal int PublishRequests { get; private set; }
    internal int Acquisitions => _leases.Count;
    internal int ActiveLeases { get; private set; }
    internal CancellationToken[] ObservedTokens => _confirmation.Invocations.Concat(_pool.Invocations).Concat(_metadata.Invocations)
        .Concat(_leases.SelectMany(lease => lease.Client.Invocations)).SelectMany(call => call.Arguments).OfType<CancellationToken>().ToArray();
    internal bool LoseFirstUpdateAcknowledgement { get; init; }
    internal bool DenyConfirmation { get; init; }
    internal bool AllowExplicitSideEffects { get; init; }
    internal bool IsManaged { get; init; }
    internal bool IsCustomizable { get; init; } = true;
    internal Action? BeforeMetadata { get; init; }

    internal ContributorFormHarness(CancellationToken token, string fixture = "base", bool emptyDisplayName = false, Action<XDocument>? transform = null)
    {
        _token = token;
        var initial = XDocument.Parse(ContributorFormFixture.Build(fixture));
        transform?.Invoke(initial);
        StateXml = Original = initial.ToString(SaveOptions.DisableFormatting);
        IReadOnlyList<AttributeMetadataDto> attributes = [
            new() { LogicalName = "name", SchemaName = "Name", DisplayName = "Metadata account name", AttributeType = "String" },
            new() { LogicalName = "telephone1", SchemaName = "Telephone1", DisplayName = "Business phone", AttributeType = "String" },
            new() { LogicalName = ContributorFormFixture.Priority, SchemaName = "mayo_PriorityScore",
                DisplayName = emptyDisplayName ? "" : "Priority score", AttributeType = "Integer" }];
        _confirmation.Setup(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.Is<CancellationToken>(t => t == _token)))
            .Returns(() => { Journal.Add("confirmation"); return DenyConfirmation
                ? Task.FromException(new PpdsException("Environment.ReadOnly", "Synthetic confirmation denied.")) : Task.CompletedTask; });
        _pool.Setup(p => p.GetClientAsync(null, null, It.Is<CancellationToken>(t => t == _token))).ReturnsAsync(NewLease);
        _metadata.Setup(m => m.GetAttributesAsync("account", null, It.Is<CancellationToken>(t => t == _token)))
            .Returns(() => { Assert.Equal(0, ActiveLeases); MetadataCalls++; Journal.Add("metadata-with-no-held-lease");
                BeforeMetadata?.Invoke(); _token.ThrowIfCancellationRequested(); return Task.FromResult(attributes); });
        Service = new FormService(_pool.Object, _metadata.Object, NullLogger<FormService>.Instance, _confirmation.Object);
    }

    internal void AssertPreconditions(string fixture)
    {
        var doc = XDocument.Parse(Original);
        FormXmlValidator.Validate(doc); // Genuine embedded schema/GUID/type-reference validation, not a skipped fixture gate.
        Assert.Single(doc.Descendants("control").Where(c => (string?)c.Attribute("id") == "notescontrol"));
        if (fixture == "rowless") Assert.Null(ContributorFormFixture.Section(doc, "General").Element("rows"));
        Assert.DoesNotContain(doc.Descendants("control"), c => (string?)c.Attribute("datafieldname") == ContributorFormFixture.Priority);
        Assert.Single(ContributorFormFixture.Section(doc, "Other").Descendants("control").Where(c => (string?)c.Attribute("datafieldname") == "telephone1"));
        if (fixture == "name-elsewhere") Assert.Equal(2, doc.Descendants("control").Count(c => (string?)c.Attribute("datafieldname") == "name"));
        if (fixture == "phone-in-two-sections") Assert.Equal(2, doc.Descendants("control").Count(c => (string?)c.Attribute("datafieldname") == "telephone1"));
        if (fixture == "name-twice-selected") Assert.Equal(2, ContributorFormFixture.Section(doc, "General").Descendants("control").Count(c => (string?)c.Attribute("datafieldname") == "name"));
    }

    private IPooledClient NewLease()
    {
        _token.ThrowIfCancellationRequested();
        Assert.Equal(0, ActiveLeases);
        ActiveLeases++;
        var lease = new LeaseObservation();
        _leases.Add(lease);
        Journal.Add("acquire-" + _leases.Count);
        lease.Client.Setup(c => c.ExecuteAsync(It.Is<OrganizationRequest>(r => r is RetrieveUnpublishedMultipleRequest ||
                (AllowExplicitSideEffects && (r is AddSolutionComponentRequest || r is PublishXmlRequest))),
                It.Is<CancellationToken>(t => t == _token)))
            .Returns<OrganizationRequest, CancellationToken>((request, _) =>
            {
                lease.Operations++; Assert.Equal(1, ActiveLeases);
                if (request is AddSolutionComponentRequest association)
                {
                    Assert.Equal(FormId, association.ComponentId); Assert.Equal(60, association.ComponentType);
                    Assert.Equal("fixture_solution", association.SolutionUniqueName); Assert.False(association.AddRequiredComponents);
                    SolutionRequests++; Journal.Add("explicit-solution-request"); return Task.FromResult(new OrganizationResponse());
                }
                if (request is PublishXmlRequest publication)
                {
                    Assert.Equal("<importexportxml><entities><entity>account</entity></entities></importexportxml>", publication.ParameterXml);
                    PublishRequests++; Journal.Add("explicit-publication-request"); return Task.FromResult(new OrganizationResponse());
                }
                ReadRequests++; Journal.Add("read-unpublished");
                var query = Assert.IsType<QueryExpression>(((RetrieveUnpublishedMultipleRequest)request).Query);
                Assert.Equal("systemform", query.EntityName);
                Assert.Equal(2, query.TopCount);
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "objecttypecode" && (string)c.Values.Single() == "account");
                Assert.Contains(query.Criteria.Conditions, c => c.AttributeName == "name" && (string)c.Values.Single() == "Fixture");
                var state = new Entity("systemform", FormId) { ["formid"] = FormId, ["name"] = "Fixture", ["type"] = new OptionSetValue(2),
                    ["ismanaged"] = IsManaged, ["iscustomizable"] = new BooleanManagedProperty(IsCustomizable), ["formxml"] = StateXml };
                return Task.FromResult<OrganizationResponse>(new RetrieveUnpublishedMultipleResponse
                    { Results = new ParameterCollection { ["EntityCollection"] = new EntityCollection([state]) } });
            });
        if (AllowExplicitSideEffects)
            lease.Client.SetupGet(c => c.ConnectedOrgUniqueName).Returns("synthetic-forms-" + Guid.NewGuid().ToString("N"));
        lease.Client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.Is<CancellationToken>(t => t == _token)))
            .Returns<Entity, CancellationToken>((entity, _) =>
            {
                lease.Operations++; Assert.Equal(1, ActiveLeases); Journal.Add("update-attempt");
                Assert.Equal("systemform", entity.LogicalName); Assert.Equal(FormId, entity.Id);
                Assert.Equal(new[] { "formxml" }, entity.Attributes.Keys);
                var xml = entity.GetAttributeValue<string>("formxml"); Assert.NotNull(xml);
                Writes.Add(xml); StateXml = xml; // Synthetic server applies before the optional acknowledgement fault.
                if (LoseFirstUpdateAcknowledgement && !_lostAcknowledgement)
                {
                    _lostAcknowledgement = true; Journal.Add("synthetic-applied-before-lost-ack");
                    return Task.FromException(new TimeoutException("Synthetic acknowledgement loss; no real service was contacted."));
                }
                AcknowledgedUpdates++; return Task.CompletedTask;
            });
        lease.Client.Setup(c => c.DisposeAsync()).Returns(() =>
        {
            lease.Disposals++; Assert.Equal(1, ActiveLeases); ActiveLeases--; Journal.Add("dispose"); return ValueTask.CompletedTask;
        });
        return lease.Client.Object;
    }

    internal void AssertIsolation()
    {
        Assert.Equal(0, ActiveLeases);
        foreach (var lease in _leases)
        {
            Assert.Equal(1, lease.Operations); Assert.Equal(1, lease.Disposals);
            lease.Client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.AtMostOnce);
            lease.Client.Verify(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.AtMostOnce);
            lease.Client.Verify(c => c.DisposeAsync(), Times.Once);
            if (AllowExplicitSideEffects) lease.Client.VerifyGet(c => c.ConnectedOrgUniqueName, Times.AtMostOnce);
            lease.Client.VerifyNoOtherCalls();
        }
        _pool.Verify(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()), Times.Exactly(Acquisitions));
        _metadata.Verify(m => m.GetAttributesAsync("account", null, It.IsAny<CancellationToken>()), Times.Exactly(MetadataCalls));
        _confirmation.Verify(c => c.EnsureConfirmedAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        _pool.VerifyNoOtherCalls(); _metadata.VerifyNoOtherCalls(); _confirmation.VerifyNoOtherCalls();
    }

    internal ServiceProvider CreateProvider() => new ServiceCollection().AddSingleton<IFormService>(Service)
        .AddSingleton<IEnvironmentConfirmationService>(_confirmation.Object).BuildServiceProvider();

    internal object Snapshot()
    {
        var doc = XDocument.Parse(StateXml);
        return new { FormXml = StateXml, Sha256 = FormService.ComputeDraftHash(StateXml),
            Controls = doc.Descendants("control").Select(control => new
            {
                Section = (string?)control.Ancestors("section").First().Element("labels")?.Element("label")?.Attribute("description"),
                SectionId = (string?)control.Ancestors("section").First().Attribute("id"),
                CellId = (string?)control.Parent?.Attribute("id"), Id = (string?)control.Attribute("id"),
                UniqueId = (string?)control.Attribute("uniqueid"), Binding = (string?)control.Attribute("datafieldname"),
                ClassId = (string?)control.Attribute("classid"), Disabled = (string?)control.Attribute("disabled"),
                Labels = control.Parent?.Element("labels")?.Elements("label").Select(label => new
                    { Text = (string?)label.Attribute("description"), Language = (string?)label.Attribute("languagecode") }).ToArray()
            }).ToArray(), ControlDescriptions = doc.Root!.Element("controlDescriptions")?.ToString(SaveOptions.DisableFormatting) };
    }

    private sealed class LeaseObservation
    {
        internal Mock<IPooledClient> Client { get; } = new(MockBehavior.Strict);
        internal int Operations { get; set; }
        internal int Disposals { get; set; }
    }
}
