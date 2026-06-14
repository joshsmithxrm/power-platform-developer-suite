using System;
using System.Collections.Generic;
using System.ServiceModel;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Forms;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Forms;

public class FormServiceTests
{
    // ── Guids used across tests ───────────────────────────────────────────

    private static readonly Guid TestFormId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TestTabId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid TestTabLabelId = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TestSectionId = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid TestSectionLabelId = new("55555555-5555-5555-5555-555555555555");

    // A minimal, schema-valid form XML with one tab ("My Tab") and one section
    // ("General"). All GUIDs use brace format as required by the validator and
    // the structure conforms to the bundled FormXml.xsd.
    private static readonly string SimpleTabFormXml = $@"<form>
  <tabs>
    <tab name=""{{{TestTabId}}}"" id=""{{{TestTabId}}}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" expanded=""1"" visible=""1"" availableforphone=""1"" labelid=""{{{TestTabLabelId}}}"">
      <labels><label description=""My Tab"" languagecode=""1033"" /></labels>
      <columns>
        <column width=""100%"">
          <sections>
            <section name=""{{{TestSectionId}}}"" id=""{{{TestSectionId}}}"" IsUserDefined=""0"" locklevel=""0"" showlabel=""1"" visible=""1"" availableforphone=""1"" columns=""1"" labelid=""{{{TestSectionLabelId}}}"">
              <labels><label description=""General"" languagecode=""1033"" /></labels>
              <rows />
            </section>
          </sections>
        </column>
      </columns>
    </tab>
  </tabs>
</form>";

    // A minimal empty form XML for AddTab (no tabs yet).
    private static readonly string EmptyFormXml = "<form />";

    // ── Mock factory ──────────────────────────────────────────────────────

    private static (Mock<IDataverseConnectionPool> poolMock, Mock<IPooledClient> clientMock) CreateMocks()
    {
        var clientMock = new Mock<IPooledClient>();
        clientMock.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        clientMock.Setup(c => c.Dispose());

        var poolMock = new Mock<IDataverseConnectionPool>();
        poolMock
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(clientMock.Object);

        return (poolMock, clientMock);
    }

    // systemform is a publishable entity; FetchFormRecordAsync now uses RetrieveUnpublishedMultiple
    // so that pending draft changes are visible and not silently overwritten.
    private static void SetupFormFetch(Mock<IPooledClient> clientMock, Entity formEntity)
    {
        clientMock
            .Setup(c => c.ExecuteAsync(
                It.IsAny<RetrieveUnpublishedMultipleRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((OrganizationRequest _, CancellationToken __) =>
            {
                var response = new RetrieveUnpublishedMultipleResponse();
                response.Results["EntityCollection"] = new EntityCollection(new List<Entity> { formEntity });
                return response;
            });
    }

    private static Entity BuildFormEntity(string formXml, int formType = 2, bool isCustomizable = true)
        => new Entity("systemform")
        {
            ["formid"] = TestFormId,
            ["name"] = "Test Form",
            ["type"] = new OptionSetValue(formType),
            ["ismanaged"] = false,
            ["iscustomizable"] = new BooleanManagedProperty(isCustomizable),
            ["formxml"] = formXml,
            ["description"] = null
        };

    private static FormService CreateService(IDataverseConnectionPool pool, IMetadataQueryService? metadata = null)
    {
        var metaMock = new Mock<IMetadataQueryService>();
        return new FormService(
            pool,
            metadata ?? metaMock.Object,
            new NullLogger<FormService>());
    }

    // ── GetAsync read-default: published unless --unpublished (parity with views/web-resource) ──

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_DefaultsToPublishedRead()
    {
        var (poolMock, clientMock) = CreateMocks();
        clientMock
            .Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(q => q.EntityName == "systemform"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { BuildFormEntity(SimpleTabFormXml) }));
        var service = CreateService(poolMock.Object);

        var detail = await service.GetAsync("contact", "Test Form");

        detail.Should().NotBeNull();
        clientMock.Verify(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Once);
        clientMock.Verify(c => c.ExecuteAsync(It.IsAny<RetrieveUnpublishedMultipleRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_Unpublished_ReadsDraft()
    {
        var (poolMock, clientMock) = CreateMocks();
        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));
        var service = CreateService(poolMock.Object);

        var detail = await service.GetAsync("contact", "Test Form", unpublished: true);

        detail.Should().NotBeNull();
        clientMock.Verify(c => c.ExecuteAsync(It.IsAny<RetrieveUnpublishedMultipleRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        clientMock.Verify(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Constructor tests ─────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullPool()
    {
        // Arrange
        var meta = new Mock<IMetadataQueryService>().Object;
        var logger = new NullLogger<FormService>();

        // Act
        var act = () => new FormService(null!, meta, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullMetadataService()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<FormService>();

        // Act
        var act = () => new FormService(pool, null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("metadataService");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var meta = new Mock<IMetadataQueryService>().Object;

        // Act
        var act = () => new FormService(pool, meta, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var meta = new Mock<IMetadataQueryService>().Object;
        var logger = new NullLogger<FormService>();

        // Act
        var service = new FormService(pool, meta, logger);

        // Assert
        service.Should().NotBeNull();
    }

    // ── AC-31: Exception wrapping ─────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WhenDataverseFails_ThrowsPpdsException()
    {
        // Arrange — pool.GetClientAsync throws a raw SDK exception
        var poolMock = new Mock<IDataverseConnectionPool>();
        poolMock
            .Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dataverse is unavailable"));

        var service = CreateService(poolMock.Object);

        // Act
        var act = async () => await service.ListAsync("account");

        // Assert — raw exception is wrapped in PpdsException (AC-31)
        await act.Should().ThrowAsync<PpdsException>();
    }

    // ── AC-32: CancellationToken ──────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_CancelledToken_ThrowsOperationCanceled()
    {
        // Arrange — pre-cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (poolMock, clientMock) = CreateMocks();
        clientMock
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var service = CreateService(poolMock.Object);

        // Act
        Func<Task> act = () => service.ListAsync("account", cts.Token);

        // Assert — either OperationCanceledException propagates directly or is wrapped in PpdsException
        (await act.Should().ThrowAsync<Exception>())
            .Which.Should().Match<Exception>(e => e is OperationCanceledException || e is PpdsException);
    }

    // ── AC-33: Progress reporter ──────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetFormXmlAsync_ReportsPhases()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reporterMock = new Mock<IProgressReporter>();
        var service = CreateService(poolMock.Object);

        var request = new SetFormXmlRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            FormXml: SimpleTabFormXml);

        // Act
        await service.SetFormXmlAsync(request, reporter: reporterMock.Object);

        // Assert — at least 2 phases reported (Retrieving + Validating + Writing = 3)
        reporterMock.Verify(
            r => r.ReportPhase(It.IsAny<string>(), It.IsAny<string?>()),
            Times.AtLeast(2));
    }

    // ── AC-08: AddTab defaults ────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddTab_DefaultProperties_GeneratesCorrectXml()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(EmptyFormXml));

        Entity? updatedEntity = null;
        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntity = e)
            .Returns(Task.CompletedTask);

        var service = CreateService(poolMock.Object);

        var request = new AddTabRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            Label: "New Tab");

        // Act
        await service.AddTabAsync(request);

        // Assert — default values are written to the XML (AC-08)
        updatedEntity.Should().NotBeNull();
        var xml = (string)updatedEntity!["formxml"];
        xml.Should().Contain("expanded=\"1\"");
        xml.Should().Contain("showlabel=\"1\"");
        xml.Should().Contain("visible=\"1\"");
    }

    // ── AC-09: UpdateTab partial update ───────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateTab_PartialUpdate_ChangesOnlyProvidedProperties()
    {
        // Arrange — form has a tab with expanded=1; only Visible=false is provided
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        Entity? updatedEntity = null;
        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntity = e)
            .Returns(Task.CompletedTask);

        var service = CreateService(poolMock.Object);

        var request = new UpdateTabRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            TabLabel: "My Tab",
            Visible: false);

        // Act
        await service.UpdateTabAsync(request);

        // Assert — visible is set to 0, expanded is still 1 (AC-09: partial update)
        updatedEntity.Should().NotBeNull();
        var xml = (string)updatedEntity!["formxml"];
        xml.Should().Contain("visible=\"0\"");
        xml.Should().Contain("expanded=\"1\"");
    }

    // ── AC-11: FindTab ────────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindTab_ExistingLabel_ReturnsIdAndPosition()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        var service = CreateService(poolMock.Object);

        var request = new FindTabRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            TabLabel: "My Tab");

        // Act
        var result = await service.FindTabAsync(request);

        // Assert — tab found at position 0 (AC-11)
        result.Should().NotBeNull();
        result!.Position.Should().Be(0);
        result.TabLabel.Should().Be("My Tab");
    }

    // ── AC-12: AddSection ─────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddSection_AllProperties_GeneratesCorrectXml()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        Entity? updatedEntity = null;
        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntity = e)
            .Returns(Task.CompletedTask);

        var service = CreateService(poolMock.Object);

        var request = new AddSectionRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            TabLabel: "My Tab",
            Label: "New Section",
            Columns: 2);

        // Act
        var result = await service.AddSectionAsync(request);

        // Assert — new section element created with correct label (AC-12)
        result.Should().NotBeNull();
        result.SectionLabel.Should().Be("New Section");
        updatedEntity.Should().NotBeNull();
        var xml = (string)updatedEntity!["formxml"];
        xml.Should().Contain("New Section");
        xml.Should().Contain("columns=\"2\"");
    }

    // ── AC-13: UpdateSection partial ──────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateSection_PartialUpdate_ChangesOnlyProvidedProperties()
    {
        // Arrange — form has a section with visible=1; only Visible=false is provided
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        Entity? updatedEntity = null;
        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntity = e)
            .Returns(Task.CompletedTask);

        var service = CreateService(poolMock.Object);

        var request = new UpdateSectionRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            SectionLabel: "General",
            Visible: false);

        // Act
        await service.UpdateSectionAsync(request);

        // Assert — visible toggled to 0, showlabel unchanged at 1 (AC-13: partial update)
        updatedEntity.Should().NotBeNull();
        var xml = (string)updatedEntity!["formxml"];
        xml.Should().Contain("visible=\"0\"");
        xml.Should().Contain("showlabel=\"1\"");
    }

    // ── AC-15: FindSection ────────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FindSection_ExistingLabel_ReturnsIdAndParentTab()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        var service = CreateService(poolMock.Object);

        var request = new FindSectionRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            SectionLabel: "General");

        // Act
        var result = await service.FindSectionAsync(request);

        // Assert — section found with correct parent tab label (AC-15)
        result.Should().NotBeNull();
        result!.SectionLabel.Should().Be("General");
        result.TabLabel.Should().Be("My Tab");
    }

    // ── AC-23: AddSubgrid — view not found ────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddSubgrid_InvalidViewId_ThrowsViewNotFound()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        // FetchFormRecordAsync returns a valid form
        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        // savedquery lookup returns empty (view not found)
        clientMock
            .Setup(c => c.RetrieveMultipleAsync(
                It.Is<QueryExpression>(q => q.EntityName == "savedquery"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var service = CreateService(poolMock.Object);

        var request = new AddSubgridRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            SectionLabel: "General",
            Label: "Contacts",
            TargetEntity: "contact",
            DefaultViewId: Guid.NewGuid(),
            MaxRows: 5);

        // Act
        var act = async () => await service.AddSubgridAsync(request);

        // Assert — PpdsException with ViewNotFound error code (AC-23)
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(FormErrorCodes.ViewNotFound);
    }

    // ── AC-24: AddSubgrid — MaxRows validation ────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddSubgrid_MaxRowsTooLow_ThrowsInvalidMaxRows()
    {
        // Arrange — MaxRows=1 is below minimum of 2
        var (poolMock, _) = CreateMocks();
        var service = CreateService(poolMock.Object);

        var request = new AddSubgridRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            SectionLabel: "General",
            Label: "Contacts",
            TargetEntity: "contact",
            DefaultViewId: Guid.NewGuid(),
            MaxRows: 1);

        // Act
        var act = async () => await service.AddSubgridAsync(request);

        // Assert — PpdsException with InvalidMaxRows error code (AC-24)
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(FormErrorCodes.InvalidMaxRows);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddSubgrid_MaxRowsTooHigh_ThrowsInvalidMaxRows()
    {
        // Arrange — MaxRows=251 exceeds maximum of 250
        var (poolMock, _) = CreateMocks();
        var service = CreateService(poolMock.Object);

        var request = new AddSubgridRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            SectionLabel: "General",
            Label: "Contacts",
            TargetEntity: "contact",
            DefaultViewId: Guid.NewGuid(),
            MaxRows: 251);

        // Act
        var act = async () => await service.AddSubgridAsync(request);

        // Assert — PpdsException with InvalidMaxRows error code (AC-24)
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(FormErrorCodes.InvalidMaxRows);
    }

    // ── AC-55: FormNotCustomizable guard ─────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task AddSubgrid_NonCustomizableForm_ThrowsFormNotCustomizable()
    {
        // Arrange — iscustomizable=false; Dataverse silently ignores UpdateAsync on such forms
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml, isCustomizable: false));

        var service = CreateService(poolMock.Object);

        var request = new AddSubgridRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            SectionLabel: "General",
            Label: "Contacts",
            TargetEntity: "contact",
            DefaultViewId: Guid.NewGuid(),
            MaxRows: 5);

        // Act
        var act = async () => await service.AddSubgridAsync(request);

        // Assert — early failure with FormNotCustomizable rather than silent no-op (AC-55)
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(FormErrorCodes.FormNotCustomizable);
    }

    // ── AC-26: Publish calls ExecuteAsync with PublishXmlRequest ─────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetFormXml_WithPublish_CallsPublishXmlRequest()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        clientMock
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        // SetupFormFetch registered after the broad ExecuteAsync so it wins for RetrieveUnpublishedMultipleRequest.
        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        clientMock.SetupGet(c => c.ConnectedOrgUniqueName).Returns("testorg");

        var service = CreateService(poolMock.Object);

        var request = new SetFormXmlRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            FormXml: SimpleTabFormXml,
            Publish: true);

        // Act
        await service.SetFormXmlAsync(request);

        // Assert — ExecuteAsync called with PublishXmlRequest (AC-26)
        clientMock.Verify(
            c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r.GetType().Name == "PublishXmlRequest"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── AC-27: Solution add ───────────────────────────────────────────────

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetFormXml_WithSolution_AddsSolutionComponent()
    {
        // Arrange
        var (poolMock, clientMock) = CreateMocks();

        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        clientMock
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        // SetupFormFetch registered after the broad ExecuteAsync so it wins for RetrieveUnpublishedMultipleRequest.
        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        var service = CreateService(poolMock.Object);

        var request = new SetFormXmlRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            FormXml: SimpleTabFormXml,
            SolutionUniqueName: "MySolution");

        // Act
        await service.SetFormXmlAsync(request);

        // Assert — AddSolutionComponentRequest was executed (AC-27)
        clientMock.Verify(
            c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r is AddSolutionComponentRequest),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SetFormXml_FormAlreadyInSolution_SwallowsFault()
    {
        // Arrange — AddSolutionComponentRequest throws fault with error code -2147159998 (already in solution)
        var (poolMock, clientMock) = CreateMocks();

        SetupFormFetch(clientMock, BuildFormEntity(SimpleTabFormXml));

        clientMock
            .Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var fault = new OrganizationServiceFault { ErrorCode = -2147159998 };
        clientMock
            .Setup(c => c.ExecuteAsync(
                It.Is<OrganizationRequest>(r => r is AddSolutionComponentRequest),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException<OrganizationServiceFault>(fault, new FaultReason("Component already exists in solution")));

        var service = CreateService(poolMock.Object);

        var request = new SetFormXmlRequest(
            EntityLogicalName: "account",
            FormName: "Test Form",
            FormXml: SimpleTabFormXml,
            SolutionUniqueName: "MySolution");

        // Act — should NOT throw; the fault is swallowed as a no-op (AC-27: idempotent)
        var act = async () => await service.SetFormXmlAsync(request);

        await act.Should().NotThrowAsync();
    }
}
