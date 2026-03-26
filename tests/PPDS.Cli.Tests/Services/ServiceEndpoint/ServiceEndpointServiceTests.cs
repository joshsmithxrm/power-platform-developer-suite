using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.ServiceEndpoint;

public class ServiceEndpointServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _mockPool;
    private readonly Mock<IPooledClient> _mockClient;
    private readonly Mock<ILogger<ServiceEndpointService>> _mockLogger;
    private readonly ServiceEndpointService _sut;

    private EntityCollection _retrieveMultipleResult = new();
    private Guid _createResult = Guid.Empty;
    private Entity? _updatedEntity;
    private readonly List<Entity> _deletedEntities = [];
    private readonly List<OrganizationRequest> _executedRequests = [];
    private readonly OrganizationResponse _executeResult = new();

    public ServiceEndpointServiceTests()
    {
        _mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        _mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        _mockLogger = new Mock<ILogger<ServiceEndpointService>>();

        // Async retrieve
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _retrieveMultipleResult);
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync(() => _retrieveMultipleResult);

        // Sync fallback
        _mockClient
            .Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(() => _retrieveMultipleResult);

        // Create
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _createResult);
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>()))
            .ReturnsAsync(() => _createResult);
        _mockClient
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(() => _createResult);

        // Update
        _mockClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => _updatedEntity = e)
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>()))
            .Callback<Entity>(e => _updatedEntity = e)
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.Update(It.IsAny<Entity>()))
            .Callback<Entity>(e => _updatedEntity = e);

        // Delete
        _mockClient
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((name, id, _) =>
            {
                var e = new Entity(name) { Id = id };
                _deletedEntities.Add(e);
            })
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>()))
            .Callback<string, Guid>((name, id) =>
            {
                var e = new Entity(name) { Id = id };
                _deletedEntities.Add(e);
            })
            .Returns(Task.CompletedTask);
        _mockClient
            .Setup(s => s.Delete(It.IsAny<string>(), It.IsAny<Guid>()))
            .Callback<string, Guid>((name, id) =>
            {
                var e = new Entity(name) { Id = id };
                _deletedEntities.Add(e);
            });

        // Execute
        _mockClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((r, _) => _executedRequests.Add(r))
            .ReturnsAsync(() => _executeResult);
        _mockClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => _executedRequests.Add(r))
            .ReturnsAsync(() => _executeResult);
        _mockClient
            .Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => _executedRequests.Add(r))
            .Returns(() => _executeResult);

        // Pool
        _mockPool
            .Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockClient.Object);

        _sut = new ServiceEndpointService(_mockPool.Object, _mockLogger.Object);
    }

    #region ListAsync

    [Fact]
    public async Task ListAsync_ReturnsEmptyList_WhenNoEndpointsExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.ListAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsEndpoints_WhenTheyExist()
    {
        var id = Guid.NewGuid();
        var entity = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = id,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "MyWebhook",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                new OptionSetValue((int)serviceendpoint_contract.Webhook),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Url] = "https://example.com/hook",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.ListAsync();

        Assert.Single(result);
        Assert.Equal(id, result[0].Id);
        Assert.Equal("MyWebhook", result[0].Name);
        Assert.Equal("Webhook", result[0].ContractType);
        Assert.True(result[0].IsWebhook);
        Assert.Equal("https://example.com/hook", result[0].Url);
        Assert.Equal("WebhookKey", result[0].AuthType);
        Assert.False(result[0].IsManaged);
    }

    #endregion

    #region GetByIdAsync

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.GetByIdAsync(Guid.NewGuid());
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsInfo_WhenFound()
    {
        var id = Guid.NewGuid();
        var entity = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = id,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "MyQueue",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                new OptionSetValue((int)serviceendpoint_contract.Queue),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                new OptionSetValue((int)serviceendpoint_authtype.SASKey),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.NamespaceAddress] = "sb://test.servicebus.windows.net",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Path] = "myqueue",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetByIdAsync(id);

        Assert.NotNull(result);
        Assert.Equal(id, result.Id);
        Assert.Equal("MyQueue", result.Name);
        Assert.Equal("Queue", result.ContractType);
        Assert.False(result.IsWebhook);
        Assert.Equal("SASKey", result.AuthType);
    }

    #endregion

    #region GetByNameAsync

    [Fact]
    public async Task GetByNameAsync_ReturnsNull_WhenNotFound()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.GetByNameAsync("missing");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByNameAsync_ReturnsInfo_WhenFound()
    {
        var id = Guid.NewGuid();
        var entity = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = id,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "FindMe",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                new OptionSetValue((int)serviceendpoint_contract.Webhook),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                new OptionSetValue((int)serviceendpoint_authtype.HttpHeader),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetByNameAsync("FindMe");

        Assert.NotNull(result);
        Assert.Equal("FindMe", result!.Name);
    }

    #endregion

    #region RegisterWebhookAsync — validation

    [Fact]
    public async Task RegisterWebhookAsync_ThrowsValidation_WhenUrlIsNotAbsolute()
    {
        var reg = new WebhookRegistration("Test", "not-a-url", "WebhookKey");
        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterWebhookAsync(reg));
        Assert.Equal(ErrorCodes.ServiceEndpoint.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterWebhookAsync_ThrowsValidation_WhenUrlIsRelative()
    {
        var reg = new WebhookRegistration("Test", "/relative/path", "WebhookKey");
        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterWebhookAsync(reg));
        Assert.Equal(ErrorCodes.ServiceEndpoint.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterWebhookAsync_ThrowsNameInUse_WhenNameAlreadyExists()
    {
        // Simulate name-check returning a match
        var existing = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = Guid.NewGuid(),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "DupeName"
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { existing } };

        var reg = new WebhookRegistration("DupeName", "https://example.com/hook", "WebhookKey");
        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterWebhookAsync(reg));
        Assert.Equal(ErrorCodes.ServiceEndpoint.NameInUse, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterWebhookAsync_CreatesEndpoint_WhenValid()
    {
        var expectedId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection(); // no existing
        _createResult = expectedId;

        var reg = new WebhookRegistration("NewHook", "https://example.com/hook", "WebhookKey", "mySecret");
        var result = await _sut.RegisterWebhookAsync(reg);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region RegisterServiceBusAsync — validation

    [Fact]
    public async Task RegisterServiceBusAsync_ThrowsValidation_WhenNamespaceAddressDoesNotStartWithSb()
    {
        var reg = new ServiceBusRegistration(
            "SBQ", "https://test.servicebus.windows.net", "myqueue", "Queue", "SASKey",
            SasKeyName: "RootManageSharedAccessKey", SasKey: new string('A', 44));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterServiceBusAsync(reg));
        Assert.Equal(ErrorCodes.ServiceEndpoint.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterServiceBusAsync_ThrowsValidation_WhenSasKeyIsWrongLength()
    {
        var reg = new ServiceBusRegistration(
            "SBQ", "sb://test.servicebus.windows.net", "myqueue", "Queue", "SASKey",
            SasKeyName: "RootManageSharedAccessKey", SasKey: "tooshort");

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterServiceBusAsync(reg));
        Assert.Equal(ErrorCodes.ServiceEndpoint.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterServiceBusAsync_ThrowsNameInUse_WhenNameAlreadyExists()
    {
        var existing = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = Guid.NewGuid(),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "DupeSB"
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { existing } };

        var reg = new ServiceBusRegistration(
            "DupeSB", "sb://test.servicebus.windows.net", "myqueue", "Queue", "SASKey",
            SasKeyName: "RootManageSharedAccessKey", SasKey: new string('A', 44));

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterServiceBusAsync(reg));
        Assert.Equal(ErrorCodes.ServiceEndpoint.NameInUse, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterServiceBusAsync_CreatesEndpoint_WhenValid()
    {
        var expectedId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedId;

        var reg = new ServiceBusRegistration(
            "NewSBQ", "sb://test.servicebus.windows.net", "myqueue", "Queue", "SASKey",
            SasKeyName: "RootManageSharedAccessKey", SasKey: new string('A', 44));

        var result = await _sut.RegisterServiceBusAsync(reg);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region UpdateAsync

    [Fact]
    public async Task UpdateAsync_ThrowsNotFound_WhenEndpointDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateAsync(Guid.NewGuid(), new ServiceEndpointUpdateRequest(Name: "New")));
        Assert.Equal(ErrorCodes.ServiceEndpoint.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task Update_ManagedComponent_NotBlocked()
    {
        // Arrange - managed endpoint should NOT be blocked from updates
        var id = Guid.NewGuid();
        var entity = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = id,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "Managed",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = true,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                new OptionSetValue((int)serviceendpoint_contract.Webhook),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        // Act - should complete without throwing
        await _sut.UpdateAsync(id, new ServiceEndpointUpdateRequest(Name: "Changed"));

        // Assert - update was applied
        Assert.NotNull(_updatedEntity);
        Assert.Equal("Changed", _updatedEntity!.GetAttributeValue<string>(global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name));
    }

    [Fact]
    public async Task UpdateAsync_DoesNothing_WhenNoFieldsProvided()
    {
        var id = Guid.NewGuid();
        var entity = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = id,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "MyHook",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                new OptionSetValue((int)serviceendpoint_contract.Webhook),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateAsync(id, new ServiceEndpointUpdateRequest());

        // UpdateAsync should NOT have been called since no fields changed
        _mockClient.Verify(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockClient.Verify(s => s.UpdateAsync(It.IsAny<Entity>()), Times.Never);
        _mockClient.Verify(s => s.Update(It.IsAny<Entity>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesNameAndDescription_WhenProvided()
    {
        var id = Guid.NewGuid();
        var entity = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
        {
            Id = id,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "OldName",
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false,
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                new OptionSetValue((int)serviceendpoint_contract.Webhook),
            [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
        };
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateAsync(id, new ServiceEndpointUpdateRequest(Name: "NewName", Description: "Desc"));

        Assert.NotNull(_updatedEntity);
        Assert.Equal("NewName", _updatedEntity!.GetAttributeValue<string>(global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name));
        Assert.Equal("Desc", _updatedEntity.GetAttributeValue<string>(global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Description));
    }

    #endregion

    #region UnregisterAsync

    [Fact]
    public async Task UnregisterAsync_ThrowsNotFound_WhenEndpointDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UnregisterAsync(Guid.NewGuid()));
        Assert.Equal(ErrorCodes.ServiceEndpoint.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterAsync_DeletesEndpoint_WhenNoDependents()
    {
        var id = Guid.NewGuid();

        // First call (GetByIdAsync): returns the endpoint
        // Second call (list dependents): returns empty
        var callCount = 0;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    // GetByIdAsync returns the endpoint
                    var endpoint = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
                    {
                        Id = id,
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "Hook",
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                            new OptionSetValue((int)serviceendpoint_contract.Webhook),
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                            new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false
                    };
                    return new EntityCollection { Entities = { endpoint } };
                }
                // List dependents: no steps
                return new EntityCollection();
            });

        await _sut.UnregisterAsync(id);

        Assert.Single(_deletedEntities);
        Assert.Equal(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName, _deletedEntities[0].LogicalName);
        Assert.Equal(id, _deletedEntities[0].Id);
    }

    [Fact]
    public async Task UnregisterAsync_ThrowsHasDependents_WhenStepsExistAndForceIsFalse()
    {
        var id = Guid.NewGuid();
        var callCount = 0;

        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var endpoint = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
                    {
                        Id = id,
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "Hook",
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                            new OptionSetValue((int)serviceendpoint_contract.Webhook),
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                            new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false
                    };
                    return new EntityCollection { Entities = { endpoint } };
                }
                // Has a dependent step
                var step = new Entity("sdkmessageprocessingstep") { Id = Guid.NewGuid() };
                step["name"] = "DependentStep";
                return new EntityCollection { Entities = { step } };
            });

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.UnregisterAsync(id, force: false));
        Assert.Equal(ErrorCodes.ServiceEndpoint.HasDependents, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterAsync_CascadeDeletesStepsAndEndpoint_WhenForceIsTrue()
    {
        var endpointId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var callCount = 0;

        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    var endpoint = new Entity(global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName)
                    {
                        Id = endpointId,
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Name] = "Hook",
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.Contract] =
                            new OptionSetValue((int)serviceendpoint_contract.Webhook),
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.AuthType] =
                            new OptionSetValue((int)serviceendpoint_authtype.WebhookKey),
                        [global::PPDS.Dataverse.Generated.ServiceEndpoint.Fields.IsManaged] = false
                    };
                    return new EntityCollection { Entities = { endpoint } };
                }
                if (callCount == 2)
                {
                    // Dependent steps
                    var step = new Entity("sdkmessageprocessingstep") { Id = stepId };
                    step["name"] = "DependentStep";
                    return new EntityCollection { Entities = { step } };
                }
                return new EntityCollection();
            });

        var mockReporter = new Mock<IProgressReporter>();
        await _sut.UnregisterAsync(endpointId, force: true, progressReporter: mockReporter.Object);

        // Both the step and the endpoint should have been deleted
        Assert.Equal(2, _deletedEntities.Count);
        Assert.Contains(_deletedEntities, e => e.LogicalName == "sdkmessageprocessingstep" && e.Id == stepId);
        Assert.Contains(_deletedEntities, e => e.LogicalName == global::PPDS.Dataverse.Generated.ServiceEndpoint.EntityLogicalName && e.Id == endpointId);
    }

    #endregion
}
