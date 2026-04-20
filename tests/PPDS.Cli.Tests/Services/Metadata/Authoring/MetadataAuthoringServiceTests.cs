using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Moq;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Metadata.Authoring;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
using PPDS.Dataverse.Pooling;
using Xunit;
using AuthoringUpdateRelationshipRequest = PPDS.Dataverse.Metadata.Authoring.UpdateRelationshipRequest;
using AuthoringUpdateStateValueRequest = PPDS.Dataverse.Metadata.Authoring.UpdateStateValueRequest;

namespace PPDS.Cli.Tests.Services.Metadata.Authoring;

[Trait("Category", "Unit")]
public class MetadataAuthoringServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _pool = new();
    private readonly Mock<IPooledClient> _client = new(MockBehavior.Loose);
    private readonly SchemaValidator _validator = new();
    private readonly DataverseMetadataAuthoringService _service;

    public MetadataAuthoringServiceTests()
    {
        _pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_client.Object);

        // Default ExecuteAsync to return a valid response (prevents NRE from await null)
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OrganizationResponse());

        SetupPublisherPrefixQuery("new");

        _service = new DataverseMetadataAuthoringService(_pool.Object, _validator, new InactiveFakeShakedownGuard());
    }

    private void SetupPublisherPrefixQuery(string prefix)
    {
        var publisherId = Guid.NewGuid();

        // Solution query returns a solution with publisherid
        var solutionEntity = new Entity("solution")
        {
            ["publisherid"] = new EntityReference("publisher", publisherId)
        };
        var solutionCollection = new EntityCollection(new System.Collections.Generic.List<Entity> { solutionEntity });

        // Publisher query returns the customization prefix
        var publisherEntity = new Entity("publisher")
        {
            ["customizationprefix"] = prefix
        };
        var publisherCollection = new EntityCollection(new System.Collections.Generic.List<Entity> { publisherEntity });

        // First call returns solutions, second call returns publisher
        _client.SetupSequence(c => c.RetrieveMultipleAsync(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(solutionCollection)
            .ReturnsAsync(publisherCollection);
    }

    #region CreateTableAsync

    [Fact]
    public async Task CreateTableAsync_ValidRequest_CallsSdkAndReturnsResult()
    {
        var entityId = Guid.NewGuid();
        var response = new CreateEntityResponse();
        response.Results["EntityId"] = entityId;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables",
            OwnershipType = "UserOwned"
        };

        var result = await _service.CreateTableAsync(request);

        result.Should().NotBeNull();
        result.MetadataId.Should().Be(entityId);
        result.WasDryRun.Should().BeFalse();
        result.LogicalName.Should().Be("new_testtable");
    }

    [Fact]
    public async Task CreateTableAsync_DryRun_DoesNotCallSdk()
    {
        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables",
            DryRun = true
        };

        var result = await _service.CreateTableAsync(request);

        result.Should().NotBeNull();
        result.WasDryRun.Should().BeTrue();
        _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateTableAsync_MapsSchemaNameToSdkRequest()
    {
        CreateEntityRequest? capturedRequest = null;
        var response = new CreateEntityResponse();
        response.Results["EntityId"] = Guid.NewGuid();

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is CreateEntityRequest ce) capturedRequest = ce;
            })
            .ReturnsAsync(response);

        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables",
            OwnershipType = "UserOwned",
            HasNotes = true,
            ChangeTrackingEnabled = true
        };

        await _service.CreateTableAsync(request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Entity.SchemaName.Should().Be("new_TestTable");
        capturedRequest.SolutionUniqueName.Should().Be("TestSolution");
        capturedRequest.Entity.OwnershipType.Should().Be(OwnershipTypes.UserOwned);
    }

    #endregion

    #region UpdateTableAsync

    [Fact]
    public async Task UpdateTableAsync_ChangesDisplayName()
    {
        UpdateEntityRequest? capturedRequest = null;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is UpdateEntityRequest update) capturedRequest = update;
            })
            .ReturnsAsync(new OrganizationResponse());

        var request = new UpdateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            DisplayName = "Updated Account"
        };

        await _service.UpdateTableAsync(request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Entity.DisplayName.Should().NotBeNull();
        capturedRequest.Entity.DisplayName.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("Updated Account");
    }

    [Fact]
    public async Task UpdateTableAsync_DryRun_DoesNotCallSdk()
    {
        var request = new UpdateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            DisplayName = "Updated",
            DryRun = true
        };

        await _service.UpdateTableAsync(request);

        _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    #endregion

    #region DeleteTableAsync

    [Fact]
    public async Task DeleteTableAsync_DryRun_DoesNotCallSdk()
    {
        var request = new DeleteTableRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            DryRun = true
        };

        await _service.DeleteTableAsync(request);

        _client.Verify(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteTableAsync_CallsSdkDeleteEntityRequest()
    {
        DeleteEntityRequest? capturedRequest = null;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is DeleteEntityRequest del) capturedRequest = del;
            })
            .ReturnsAsync(new OrganizationResponse());

        var request = new DeleteTableRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "new_mytable"
        };

        await _service.DeleteTableAsync(request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.LogicalName.Should().Be("new_mytable");
    }

    #endregion

    #region UpdateColumnAsync

    [Fact]
    public async Task UpdateColumnAsync_ChangesRequiredLevel()
    {
        // Setup retrieve existing attribute
        var existingAttr = new StringAttributeMetadata { LogicalName = "new_myfield", MaxLength = 100 };
        var retrieveResponse = new RetrieveAttributeResponse();
        retrieveResponse.Results["AttributeMetadata"] = existingAttr;

        UpdateAttributeRequest? capturedRequest = null;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is RetrieveAttributeRequest)
                    return Task.FromResult<OrganizationResponse>(retrieveResponse);
                if (req is UpdateAttributeRequest ua)
                    capturedRequest = ua;
                return Task.FromResult(new OrganizationResponse());
            });

        var request = new UpdateColumnRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            ColumnLogicalName = "new_myfield",
            RequiredLevel = "Required"
        };

        await _service.UpdateColumnAsync(request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Attribute.RequiredLevel!.Value.Should().Be(AttributeRequiredLevel.ApplicationRequired);
    }

    #endregion

    #region UpdateRelationshipAsync

    [Fact]
    public async Task UpdateRelationshipAsync_ChangesCascadeConfig()
    {
        // Setup retrieve existing relationship
        var existingRel = new OneToManyRelationshipMetadata
        {
            SchemaName = "new_account_contact",
            CascadeConfiguration = new CascadeConfiguration()
        };
        var retrieveResponse = new RetrieveRelationshipResponse();
        retrieveResponse.Results["RelationshipMetadata"] = existingRel;

        Microsoft.Xrm.Sdk.Messages.UpdateRelationshipRequest? capturedRequest = null;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Returns<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is RetrieveRelationshipRequest)
                    return Task.FromResult<OrganizationResponse>(retrieveResponse);
                if (req is Microsoft.Xrm.Sdk.Messages.UpdateRelationshipRequest ur)
                    capturedRequest = ur;
                return Task.FromResult(new OrganizationResponse());
            });

        var request = new AuthoringUpdateRelationshipRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_account_contact",
            CascadeConfiguration = new CascadeConfigurationDto
            {
                Delete = CascadeBehavior.Restrict,
                Assign = CascadeBehavior.Cascade
            }
        };

        await _service.UpdateRelationshipAsync(request);

        capturedRequest.Should().NotBeNull();
        var updatedRel = capturedRequest!.Relationship as OneToManyRelationshipMetadata;
        updatedRel.Should().NotBeNull("because UpdateRelationship should send a OneToManyRelationshipMetadata");
        updatedRel!.CascadeConfiguration.Should().NotBeNull();
        updatedRel.CascadeConfiguration.Delete.Should().Be(CascadeType.Restrict);
        updatedRel.CascadeConfiguration.Assign.Should().Be(CascadeType.Cascade);
    }

    #endregion

    #region ReorderOptionsAsync

    [Fact]
    public async Task ReorderOptionsAsync_SendsOrderOptionRequest()
    {
        OrderOptionRequest? capturedRequest = null;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is OrderOptionRequest oor) capturedRequest = oor;
            })
            .ReturnsAsync(new OrganizationResponse());

        var request = new ReorderOptionsRequest
        {
            SolutionUniqueName = "TestSolution",
            OptionSetName = "new_mystatus",
            Order = [3, 1, 2]
        };

        await _service.ReorderOptionsAsync(request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.OptionSetName.Should().Be("new_mystatus");
        capturedRequest.Values.Should().BeEquivalentTo(new[] { 3, 1, 2 });
    }

    #endregion

    #region UpdateStateValueAsync

    [Fact]
    public async Task UpdateStateValueAsync_RenamesLabel()
    {
        Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest? capturedRequest = null;

        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((req, _) =>
            {
                if (req is Microsoft.Xrm.Sdk.Messages.UpdateStateValueRequest usv) capturedRequest = usv;
            })
            .ReturnsAsync(new OrganizationResponse());

        var request = new AuthoringUpdateStateValueRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "incident",
            AttributeLogicalName = "statecode",
            Value = 0,
            Label = "Open"
        };

        await _service.UpdateStateValueAsync(request);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.EntityLogicalName.Should().Be("incident");
        capturedRequest.AttributeLogicalName.Should().Be("statecode");
        capturedRequest.Value.Should().Be(0);
        capturedRequest.Label.LocalizedLabels.Should().ContainSingle()
            .Which.Label.Should().Be("Open");
    }

    #endregion

    #region Cancellation Token Propagation

    [Fact]
    public async Task CreateTableAsync_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables"
        };

        // The cancelled token should be passed to the pool's GetClientAsync
        _pool.Setup(p => p.GetClientAsync(null, null, It.Is<CancellationToken>(t => t.IsCancellationRequested)))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _service.CreateTableAsync(request, ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    #endregion

    #region Progress Reporter

    [Fact]
    public async Task CreateTableAsync_ReportsPhases()
    {
        var reporter = new Mock<IMetadataAuthoringProgressReporter>();

        var response = new CreateEntityResponse();
        response.Results["EntityId"] = Guid.NewGuid();
        _client.Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var request = new CreateTableRequest
        {
            SolutionUniqueName = "TestSolution",
            SchemaName = "new_TestTable",
            DisplayName = "Test Table",
            PluralDisplayName = "Test Tables"
        };

        await _service.CreateTableAsync(request, reporter.Object);

        reporter.Verify(r => r.ReportPhase("Resolving publisher prefix", null), Times.Once);
        reporter.Verify(r => r.ReportPhase("Validating", null), Times.Once);
        reporter.Verify(r => r.ReportPhase("Creating table", "new_TestTable"), Times.Once);
        reporter.Verify(r => r.ReportInfo(It.Is<string>(s => s.Contains("created successfully"))), Times.Once);
    }

    #endregion

    #region Lookup Column Type Rejection

    [Fact]
    public async Task CreateColumnAsync_LookupType_ThrowsValidationException()
    {
        var request = new CreateColumnRequest
        {
            SolutionUniqueName = "TestSolution",
            EntityLogicalName = "account",
            SchemaName = "new_MyLookup",
            DisplayName = "My Lookup",
            ColumnType = SchemaColumnType.Lookup
        };

        var act = () => _service.CreateColumnAsync(request);

        await act.Should().ThrowAsync<MetadataValidationException>()
            .Where(e => e.ValidationMessages.Any(m => m.Rule == MetadataErrorCodes.UseRelationshipForLookup));
    }

    #endregion

    #region Constructor

    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        var act = () => new DataverseMetadataAuthoringService(null!, _validator, new InactiveFakeShakedownGuard());

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("connectionPool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullValidator()
    {
        var act = () => new DataverseMetadataAuthoringService(_pool.Object, null!, new InactiveFakeShakedownGuard());

        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("validator");
    }

    #endregion

    #region DI Registration

    [Fact]
    public void AddCliApplicationServices_RegistersAuthoringService()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();

        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Primary")
            {
                Url = "https://test.crm.dynamics.com",
                ClientId = "test",
                ClientSecret = "test",
                AuthType = DataverseAuthType.ClientSecret
            });
        });
        // The authoring service moved to PPDS.Cli.Services.Metadata.Authoring; register via
        // AddCliApplicationServices (the new single-source-of-truth entrypoint per SL2).
        services.AddCliApplicationServices();

        var provider = services.BuildServiceProvider();
        var authoringService = provider.GetService(typeof(IMetadataAuthoringService));

        authoringService.Should().NotBeNull();
        authoringService.Should().BeOfType<DataverseMetadataAuthoringService>();
    }

    [Fact]
    public void RegisterDataverseServices_RegistersSchemaValidator()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging();

        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Primary")
            {
                Url = "https://test.crm.dynamics.com",
                ClientId = "test",
                ClientSecret = "test",
                AuthType = DataverseAuthType.ClientSecret
            });
        });

        var provider = services.BuildServiceProvider();
        var validator = provider.GetService(typeof(SchemaValidator));

        validator.Should().NotBeNull();
        validator.Should().BeOfType<SchemaValidator>();
    }

    #endregion
}
