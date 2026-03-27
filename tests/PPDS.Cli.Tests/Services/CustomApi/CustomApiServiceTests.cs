using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.CustomApi;

[Trait("Category", "Unit")]
public class CustomApiServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _mockPool;
    private readonly Mock<IPooledClient> _mockClient;
    private readonly Mock<IPluginRegistrationService> _mockPluginRegistrationService;
    private readonly Mock<ILogger<CustomApiService>> _mockLogger;
    private readonly CustomApiService _sut;

    private EntityCollection _retrieveMultipleResult = new();
    private Guid _createResult = Guid.Empty;
    private Entity? _updatedEntity;
    private readonly List<Entity> _deletedEntities = [];
    private readonly List<OrganizationRequest> _executedRequests = [];
    private readonly OrganizationResponse _executeResult = new();

    public CustomApiServiceTests()
    {
        _mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        _mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        _mockLogger = new Mock<ILogger<CustomApiService>>();

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

        _mockPluginRegistrationService = new Mock<IPluginRegistrationService>();
        _sut = new CustomApiService(_mockPool.Object, _mockPluginRegistrationService.Object, _mockLogger.Object);
    }

    #region ListAsync

    [Fact]
    public async Task ListAsync_ReturnsEmptyList_WhenNoApisExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.ListAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAsync_ReturnsApis_WhenTheyExist()
    {
        var id = Guid.NewGuid();
        var entity = BuildApiEntity(id, "MyApi", "My API", bindingType: 0, isFunction: false, isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.ListAsync();

        Assert.Single(result);
        Assert.Equal(id, result[0].Id);
        Assert.Equal("MyApi", result[0].UniqueName);
        Assert.Equal("My API", result[0].DisplayName);
        Assert.Equal("Global", result[0].BindingType);
        Assert.False(result[0].IsManaged);
    }

    [Fact]
    public async Task ListAsync_MapsAllBindingTypes()
    {
        var entities = new EntityCollection();
        entities.Entities.Add(BuildApiEntity(Guid.NewGuid(), "Api0", "Global API", bindingType: 0));
        entities.Entities.Add(BuildApiEntity(Guid.NewGuid(), "Api1", "Entity API", bindingType: 1));
        entities.Entities.Add(BuildApiEntity(Guid.NewGuid(), "Api2", "Collection API", bindingType: 2));
        _retrieveMultipleResult = entities;

        var result = await _sut.ListAsync();

        Assert.Equal("Global", result[0].BindingType);
        Assert.Equal("Entity", result[1].BindingType);
        Assert.Equal("EntityCollection", result[2].BindingType);
    }

    [Fact]
    public async Task ListAsync_MapsAllProcessingStepTypes()
    {
        var entities = new EntityCollection();
        entities.Entities.Add(BuildApiEntity(Guid.NewGuid(), "Api0", "None", allowedStepType: 0));
        entities.Entities.Add(BuildApiEntity(Guid.NewGuid(), "Api1", "Async", allowedStepType: 1));
        entities.Entities.Add(BuildApiEntity(Guid.NewGuid(), "Api2", "Sync", allowedStepType: 2));
        _retrieveMultipleResult = entities;

        var result = await _sut.ListAsync();

        Assert.Equal("None", result[0].AllowedProcessingStepType);
        Assert.Equal("AsyncOnly", result[1].AllowedProcessingStepType);
        Assert.Equal("SyncAndAsync", result[2].AllowedProcessingStepType);
    }

    #endregion

    #region GetAsync

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenNotFound()
    {
        _retrieveMultipleResult = new EntityCollection();
        var result = await _sut.GetAsync("MissingApi");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_FindsByUniqueName_WhenStringIsNotGuid()
    {
        var id = Guid.NewGuid();
        var entity = BuildApiEntity(id, "MyApi", "My API");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetAsync("MyApi");

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
        Assert.Equal("MyApi", result.UniqueName);
    }

    [Fact]
    public async Task GetAsync_FindsById_WhenStringIsValidGuid()
    {
        var id = Guid.NewGuid();
        var entity = BuildApiEntity(id, "MyApi", "My API");
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        var result = await _sut.GetAsync(id.ToString());

        Assert.NotNull(result);
        Assert.Equal(id, result!.Id);
    }

    [Fact]
    public async Task GetAsync_IncludesRequestParameters()
    {
        var apiId = Guid.NewGuid();
        var paramId = Guid.NewGuid();

        var callCount = 0;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new EntityCollection { Entities = { BuildApiEntity(apiId, "MyApi", "My API") } };
                }
                if (callCount == 2)
                {
                    // Request parameters
                    var param = BuildRequestParamEntity(paramId, apiId, "Param1", "Parameter 1", 10 /* String */);
                    return new EntityCollection { Entities = { param } };
                }
                // Response properties (empty)
                return new EntityCollection();
            });

        var result = await _sut.GetAsync("MyApi");

        Assert.NotNull(result);
        Assert.Single(result!.RequestParameters);
        Assert.Equal(paramId, result.RequestParameters[0].Id);
        Assert.Equal("Param1", result.RequestParameters[0].UniqueName);
        Assert.Equal("String", result.RequestParameters[0].Type);
    }

    [Fact]
    public async Task GetAsync_IncludesResponseProperties()
    {
        var apiId = Guid.NewGuid();
        var propId = Guid.NewGuid();

        var callCount = 0;
        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                {
                    return new EntityCollection { Entities = { BuildApiEntity(apiId, "MyApi", "My API") } };
                }
                if (callCount == 2)
                {
                    // Request parameters (empty)
                    return new EntityCollection();
                }
                // Response properties
                var prop = BuildResponsePropEntity(propId, apiId, "Result1", "Result 1", 10 /* String */);
                return new EntityCollection { Entities = { prop } };
            });

        var result = await _sut.GetAsync("MyApi");

        Assert.NotNull(result);
        Assert.Single(result!.ResponseProperties);
        Assert.Equal(propId, result.ResponseProperties[0].Id);
        Assert.Equal("Result1", result.ResponseProperties[0].UniqueName);
        Assert.Equal("String", result.ResponseProperties[0].Type);
    }

    #endregion

    #region RegisterAsync

    [Fact]
    public async Task RegisterAsync_ThrowsValidation_WhenUniqueNameIsEmpty()
    {
        var reg = new CustomApiRegistration(
            UniqueName: "",
            DisplayName: "My API",
            Name: null,
            Description: null,
            PluginTypeId: Guid.NewGuid(),
            BindingType: "Global",
            BoundEntity: null,
            IsFunction: false,
            IsPrivate: false,
            ExecutePrivilegeName: null,
            AllowedProcessingStepType: "None");

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterAsync(reg));
        Assert.Equal(ErrorCodes.CustomApi.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterAsync_ThrowsValidation_WhenEntityBindingHasNoBoundEntity()
    {
        var reg = new CustomApiRegistration(
            UniqueName: "ppds_TestApi",
            DisplayName: "Test API",
            Name: null,
            Description: null,
            PluginTypeId: Guid.NewGuid(),
            BindingType: "Entity",
            BoundEntity: null, // missing
            IsFunction: false,
            IsPrivate: false,
            ExecutePrivilegeName: null,
            AllowedProcessingStepType: "None");

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterAsync(reg));
        Assert.Equal(ErrorCodes.CustomApi.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterAsync_ThrowsNameInUse_WhenUniqueNameAlreadyExists()
    {
        var existing = BuildApiEntity(Guid.NewGuid(), "ppds_Existing", "Existing API");
        _retrieveMultipleResult = new EntityCollection { Entities = { existing } };

        var reg = new CustomApiRegistration(
            UniqueName: "ppds_Existing",
            DisplayName: "My API",
            Name: null,
            Description: null,
            PluginTypeId: Guid.NewGuid(),
            BindingType: "Global",
            BoundEntity: null,
            IsFunction: false,
            IsPrivate: false,
            ExecutePrivilegeName: null,
            AllowedProcessingStepType: "None");

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.RegisterAsync(reg));
        Assert.Equal(ErrorCodes.CustomApi.NameInUse, ex.ErrorCode);
    }

    [Fact]
    public async Task RegisterAsync_CreatesApi_WhenValid()
    {
        var expectedId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection(); // no existing
        _createResult = expectedId;

        var reg = new CustomApiRegistration(
            UniqueName: "ppds_NewApi",
            DisplayName: "New API",
            Name: null,
            Description: null,
            PluginTypeId: Guid.NewGuid(),
            BindingType: "Global",
            BoundEntity: null,
            IsFunction: false,
            IsPrivate: false,
            ExecutePrivilegeName: null,
            AllowedProcessingStepType: "None");

        var result = await _sut.RegisterAsync(reg);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == CustomAPI.EntityLogicalName),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RegisterAsync_CreatesApiWithParameters_WhenParametersProvided()
    {
        var apiId = Guid.NewGuid();
        var paramId = Guid.NewGuid();

        var createCallCount = 0;
        _retrieveMultipleResult = new EntityCollection();
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                createCallCount++;
                return createCallCount == 1 ? apiId : paramId;
            });

        var reg = new CustomApiRegistration(
            UniqueName: "ppds_NewApi",
            DisplayName: "New API",
            Name: null,
            Description: null,
            PluginTypeId: Guid.NewGuid(),
            BindingType: "Global",
            BoundEntity: null,
            IsFunction: false,
            IsPrivate: false,
            ExecutePrivilegeName: null,
            AllowedProcessingStepType: "None",
            Parameters: [
                new CustomApiParameterRegistration(
                    UniqueName: "Param1",
                    DisplayName: "Parameter 1",
                    Name: null,
                    Description: null,
                    Type: "String",
                    LogicalEntityName: null,
                    IsOptional: false,
                    Direction: "Request")
            ]);

        var result = await _sut.RegisterAsync(reg);

        Assert.Equal(apiId, result);
        // Called twice: once for API, once for the parameter
        _mockClient.Verify(
            s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task RegisterAsync_CreatesResponseProperty_WhenDirectionIsResponse()
    {
        var apiId = Guid.NewGuid();
        var propId = Guid.NewGuid();

        var createCallCount = 0;
        _retrieveMultipleResult = new EntityCollection();
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                createCallCount++;
                return createCallCount == 1 ? apiId : propId;
            });

        Entity? capturedEntity = null;
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(() =>
            {
                createCallCount++;
                return createCallCount == 1 ? apiId : propId;
            });

        var reg = new CustomApiRegistration(
            UniqueName: "ppds_FuncApi",
            DisplayName: "Function API",
            Name: null,
            Description: null,
            PluginTypeId: Guid.NewGuid(),
            BindingType: "Global",
            BoundEntity: null,
            IsFunction: true,
            IsPrivate: false,
            ExecutePrivilegeName: null,
            AllowedProcessingStepType: "None",
            Parameters: [
                new CustomApiParameterRegistration(
                    UniqueName: "Result",
                    DisplayName: "Result",
                    Name: null,
                    Description: null,
                    Type: "String",
                    LogicalEntityName: null,
                    IsOptional: false,
                    Direction: "Response")
            ]);

        await _sut.RegisterAsync(reg);

        // The second Create should have been for a customapiresponseproperty entity
        Assert.NotNull(capturedEntity);
        Assert.Equal(CustomAPIResponseProperty.EntityLogicalName, capturedEntity!.LogicalName);
    }

    #endregion

    #region UpdateAsync

    [Fact]
    public async Task UpdateAsync_ThrowsNotFound_WhenApiDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateAsync(Guid.NewGuid(), new CustomApiUpdateRequest(DisplayName: "New")));
        Assert.Equal(ErrorCodes.CustomApi.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task Update_ManagedComponent_NotBlocked()
    {
        // Arrange - managed Custom API should NOT be blocked from updates
        var id = Guid.NewGuid();
        var entity = BuildApiEntity(id, "ManagedApi", "Managed API", isManaged: true);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        // Act - should complete without throwing
        await _sut.UpdateAsync(id, new CustomApiUpdateRequest(DisplayName: "Changed"));

        // Assert - update was applied
        Assert.NotNull(_updatedEntity);
        Assert.Equal("Changed", _updatedEntity!.GetAttributeValue<string>(CustomAPI.Fields.DisplayName));
    }

    [Fact]
    public async Task UpdateAsync_DoesNothing_WhenNoFieldsProvided()
    {
        var id = Guid.NewGuid();
        var entity = BuildApiEntity(id, "MyApi", "My API", isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateAsync(id, new CustomApiUpdateRequest());

        _mockClient.Verify(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockClient.Verify(s => s.UpdateAsync(It.IsAny<Entity>()), Times.Never);
        _mockClient.Verify(s => s.Update(It.IsAny<Entity>()), Times.Never);
    }

    [Fact]
    public async Task UpdateAsync_UpdatesDisplayNameAndDescription_WhenProvided()
    {
        var id = Guid.NewGuid();
        var entity = BuildApiEntity(id, "MyApi", "Old Name", isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateAsync(id, new CustomApiUpdateRequest(DisplayName: "New Name", Description: "New Desc"));

        Assert.NotNull(_updatedEntity);
        Assert.Equal("New Name", _updatedEntity!.GetAttributeValue<string>(CustomAPI.Fields.DisplayName));
        Assert.Equal("New Desc", _updatedEntity.GetAttributeValue<string>(CustomAPI.Fields.Description));
    }

    [Fact]
    public async Task UpdateAsync_UpdatesPluginTypeId_WhenProvided()
    {
        var id = Guid.NewGuid();
        var newPluginTypeId = Guid.NewGuid();
        var entity = BuildApiEntity(id, "MyApi", "My API", isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { entity } };

        await _sut.UpdateAsync(id, new CustomApiUpdateRequest(PluginTypeId: newPluginTypeId));

        Assert.NotNull(_updatedEntity);
        var pluginTypeRef = _updatedEntity!.GetAttributeValue<EntityReference>(CustomAPI.Fields.PluginTypeId);
        Assert.NotNull(pluginTypeRef);
        Assert.Equal(newPluginTypeId, pluginTypeRef.Id);
    }

    #endregion

    #region UnregisterAsync

    [Fact]
    public async Task UnregisterAsync_ThrowsNotFound_WhenApiDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UnregisterAsync(Guid.NewGuid()));
        Assert.Equal(ErrorCodes.CustomApi.NotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterAsync_DeletesApi_WhenNoParameters()
    {
        var id = Guid.NewGuid();
        var callCount = 0;

        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    ? new EntityCollection { Entities = { BuildApiEntity(id, "MyApi", "My API") } }
                    : new EntityCollection(); // no parameters
            });

        await _sut.UnregisterAsync(id);

        Assert.Single(_deletedEntities);
        Assert.Equal(CustomAPI.EntityLogicalName, _deletedEntities[0].LogicalName);
        Assert.Equal(id, _deletedEntities[0].Id);
    }

    [Fact]
    public async Task UnregisterAsync_ThrowsHasDependents_WhenParametersExistAndForceIsFalse()
    {
        var id = Guid.NewGuid();
        var callCount = 0;

        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new EntityCollection { Entities = { BuildApiEntity(id, "MyApi", "My API") } };
                // Has parameters
                var param = BuildRequestParamEntity(Guid.NewGuid(), id, "Param1", "Param 1", 10);
                return new EntityCollection { Entities = { param } };
            });

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.UnregisterAsync(id, force: false));
        Assert.Equal(ErrorCodes.CustomApi.HasDependents, ex.ErrorCode);
    }

    [Fact]
    public async Task UnregisterAsync_CascadeDeletesParametersAndApi_WhenForceIsTrue()
    {
        var apiId = Guid.NewGuid();
        var paramId = Guid.NewGuid();
        var propId = Guid.NewGuid();
        var callCount = 0;

        _mockClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    return new EntityCollection { Entities = { BuildApiEntity(apiId, "MyApi", "My API") } };
                if (callCount == 2)
                {
                    // Request parameters
                    var param = BuildRequestParamEntity(paramId, apiId, "Param1", "Param 1", 10);
                    return new EntityCollection { Entities = { param } };
                }
                if (callCount == 3)
                {
                    // Response properties
                    var prop = BuildResponsePropEntity(propId, apiId, "Prop1", "Prop 1", 10);
                    return new EntityCollection { Entities = { prop } };
                }
                return new EntityCollection();
            });

        var mockReporter = new Mock<IProgressReporter>();
        await _sut.UnregisterAsync(apiId, force: true, progressReporter: mockReporter.Object);

        // param + prop + api = 3 deletes
        Assert.Equal(3, _deletedEntities.Count);
        Assert.Contains(_deletedEntities, e => e.LogicalName == CustomAPIRequestParameter.EntityLogicalName && e.Id == paramId);
        Assert.Contains(_deletedEntities, e => e.LogicalName == CustomAPIResponseProperty.EntityLogicalName && e.Id == propId);
        Assert.Contains(_deletedEntities, e => e.LogicalName == CustomAPI.EntityLogicalName && e.Id == apiId);
    }

    #endregion

    #region AddParameterAsync

    [Fact]
    public async Task AddParameterAsync_ThrowsValidation_WhenTypeIsEntityAndNoLogicalEntityName()
    {
        var apiId = Guid.NewGuid();
        var param = new CustomApiParameterRegistration(
            UniqueName: "Param1",
            DisplayName: "Parameter 1",
            Name: null,
            Description: null,
            Type: "Entity",
            LogicalEntityName: null, // missing
            IsOptional: false,
            Direction: "Request");

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.AddParameterAsync(apiId, param));
        Assert.Equal(ErrorCodes.CustomApi.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task AddParameterAsync_ThrowsValidation_WhenDirectionIsInvalid()
    {
        var apiId = Guid.NewGuid();
        var param = new CustomApiParameterRegistration(
            UniqueName: "Param1",
            DisplayName: "Parameter 1",
            Name: null,
            Description: null,
            Type: "String",
            LogicalEntityName: null,
            IsOptional: false,
            Direction: "InvalidDirection");

        var ex = await Assert.ThrowsAsync<PpdsException>(() => _sut.AddParameterAsync(apiId, param));
        Assert.Equal(ErrorCodes.CustomApi.ValidationFailed, ex.ErrorCode);
    }

    [Fact]
    public async Task AddParameterAsync_CreatesRequestParameter_WhenDirectionIsRequest()
    {
        var apiId = Guid.NewGuid();
        var expectedId = Guid.NewGuid();
        _createResult = expectedId;

        var param = new CustomApiParameterRegistration(
            UniqueName: "InputParam",
            DisplayName: "Input Parameter",
            Name: null,
            Description: null,
            Type: "String",
            LogicalEntityName: null,
            IsOptional: true,
            Direction: "Request");

        var result = await _sut.AddParameterAsync(apiId, param);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == CustomAPIRequestParameter.EntityLogicalName),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddParameterAsync_CreatesResponseProperty_WhenDirectionIsResponse()
    {
        var apiId = Guid.NewGuid();
        var expectedId = Guid.NewGuid();
        _createResult = expectedId;

        var param = new CustomApiParameterRegistration(
            UniqueName: "OutputProp",
            DisplayName: "Output Property",
            Name: null,
            Description: null,
            Type: "Integer",
            LogicalEntityName: null,
            IsOptional: false,
            Direction: "Response");

        var result = await _sut.AddParameterAsync(apiId, param);

        Assert.Equal(expectedId, result);
        _mockClient.Verify(
            s => s.CreateAsync(
                It.Is<Entity>(e => e.LogicalName == CustomAPIResponseProperty.EntityLogicalName),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AddParameterAsync_MapsTypeCorrectly()
    {
        var apiId = Guid.NewGuid();
        _createResult = Guid.NewGuid();

        Entity? capturedEntity = null;
        _mockClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(() => _createResult);

        var param = new CustomApiParameterRegistration(
            UniqueName: "BoolParam",
            DisplayName: "Bool Parameter",
            Name: null,
            Description: null,
            Type: "Boolean",
            LogicalEntityName: null,
            IsOptional: false,
            Direction: "Request");

        await _sut.AddParameterAsync(apiId, param);

        Assert.NotNull(capturedEntity);
        var typeValue = capturedEntity!.GetAttributeValue<OptionSetValue>(CustomAPIRequestParameter.Fields.Type);
        Assert.NotNull(typeValue);
        Assert.Equal(0, typeValue.Value); // Boolean = 0
    }

    #endregion

    #region UpdateParameterAsync

    [Fact]
    public async Task UpdateParameterAsync_ThrowsParameterNotFound_WhenParameterDoesNotExist()
    {
        _retrieveMultipleResult = new EntityCollection();
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateParameterAsync(Guid.NewGuid(), new CustomApiParameterUpdateRequest(DisplayName: "New")));
        Assert.Equal(ErrorCodes.CustomApi.ParameterNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task UpdateParameterAsync_UpdatesDisplayName_WhenFound()
    {
        var paramId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var param = BuildRequestParamEntity(paramId, apiId, "Param1", "Old Name", 10, isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { param } };

        await _sut.UpdateParameterAsync(paramId, new CustomApiParameterUpdateRequest(DisplayName: "New Name"));

        Assert.NotNull(_updatedEntity);
        Assert.Equal("New Name", _updatedEntity!.GetAttributeValue<string>(CustomAPIRequestParameter.Fields.DisplayName));
    }

    [Fact]
    public async Task UpdateParameterAsync_DoesNothing_WhenNoFieldsProvided()
    {
        var paramId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var param = BuildRequestParamEntity(paramId, apiId, "Param1", "Param Name", 10, isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { param } };

        await _sut.UpdateParameterAsync(paramId, new CustomApiParameterUpdateRequest());

        _mockClient.Verify(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
        _mockClient.Verify(s => s.UpdateAsync(It.IsAny<Entity>()), Times.Never);
        _mockClient.Verify(s => s.Update(It.IsAny<Entity>()), Times.Never);
    }

    #endregion

    #region RemoveParameterAsync

    [Fact]
    public async Task RemoveParameterAsync_ThrowsParameterNotFound_WhenNotFound()
    {
        _retrieveMultipleResult = new EntityCollection();
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.RemoveParameterAsync(Guid.NewGuid()));
        Assert.Equal(ErrorCodes.CustomApi.ParameterNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task RemoveParameterAsync_DeletesRequestParameter_WhenExists()
    {
        var paramId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var param = BuildRequestParamEntity(paramId, apiId, "Param1", "Param Name", 10, isManaged: false);
        _retrieveMultipleResult = new EntityCollection { Entities = { param } };

        await _sut.RemoveParameterAsync(paramId);

        Assert.Single(_deletedEntities);
        Assert.Equal(CustomAPIRequestParameter.EntityLogicalName, _deletedEntities[0].LogicalName);
        Assert.Equal(paramId, _deletedEntities[0].Id);
    }

    [Fact]
    public async Task RemoveParameter_ManagedComponent_NotBlocked()
    {
        // Arrange - managed parameter should NOT be blocked from removal
        var paramId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var param = BuildRequestParamEntity(paramId, apiId, "Param1", "Param Name", 10, isManaged: true);
        _retrieveMultipleResult = new EntityCollection { Entities = { param } };

        // Act - should complete without throwing
        await _sut.RemoveParameterAsync(paramId);

        // Assert - delete was called
        Assert.Single(_deletedEntities);
        Assert.Equal(paramId, _deletedEntities[0].Id);
    }

    #endregion

    #region SetPluginTypeAsync

    [Fact]
    public async Task SetPlugin_SetsPluginTypeId()
    {
        // Arrange
        var apiId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        var pluginTypeInfo = new PluginTypeInfo
        {
            Id = pluginTypeId,
            TypeName = "TestNamespace.TestPlugin"
        };

        _mockPluginRegistrationService
            .Setup(s => s.GetPluginTypeByNameAsync("TestNamespace.TestPlugin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(pluginTypeInfo);

        // Act
        await _sut.SetPluginTypeAsync(apiId, "TestNamespace.TestPlugin", null);

        // Assert - update entity should set PluginTypeId
        Assert.NotNull(_updatedEntity);
        var pluginTypeRef = _updatedEntity!.GetAttributeValue<EntityReference>(CustomAPI.Fields.PluginTypeId);
        Assert.NotNull(pluginTypeRef);
        Assert.Equal(pluginTypeId, pluginTypeRef.Id);
    }

    [Fact]
    public async Task SetPlugin_None_ClearsPluginTypeId()
    {
        // Arrange
        var apiId = Guid.NewGuid();

        // Act - pass null to clear the plugin type
        await _sut.SetPluginTypeAsync(apiId, null, null);

        // Assert - update entity should set PluginTypeId to null
        Assert.NotNull(_updatedEntity);
        Assert.True(_updatedEntity!.Attributes.ContainsKey(CustomAPI.Fields.PluginTypeId));
        Assert.Null(_updatedEntity.GetAttributeValue<EntityReference>(CustomAPI.Fields.PluginTypeId));
    }

    [Fact]
    public async Task SetPlugin_InvalidType_ThrowsNotFound()
    {
        // Arrange
        var apiId = Guid.NewGuid();

        _mockPluginRegistrationService
            .Setup(s => s.GetPluginTypeByNameAsync("NonExistent.Plugin", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PluginTypeInfo?)null);

        // Act & Assert
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.SetPluginTypeAsync(apiId, "NonExistent.Plugin", null));
        Assert.Equal(ErrorCodes.CustomApi.PluginTypeNotFound, ex.ErrorCode);
    }

    [Fact]
    public async Task SetPlugin_SameType_IdempotentSuccess()
    {
        // Arrange
        var apiId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        var pluginTypeInfo = new PluginTypeInfo
        {
            Id = pluginTypeId,
            TypeName = "TestNamespace.TestPlugin"
        };

        _mockPluginRegistrationService
            .Setup(s => s.GetPluginTypeByNameAsync("TestNamespace.TestPlugin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(pluginTypeInfo);

        // Act - call twice with same type
        await _sut.SetPluginTypeAsync(apiId, "TestNamespace.TestPlugin", null);
        _updatedEntity = null; // Reset to capture second call
        await _sut.SetPluginTypeAsync(apiId, "TestNamespace.TestPlugin", null);

        // Assert - second call also succeeds
        Assert.NotNull(_updatedEntity);
        var pluginTypeRef = _updatedEntity!.GetAttributeValue<EntityReference>(CustomAPI.Fields.PluginTypeId);
        Assert.NotNull(pluginTypeRef);
        Assert.Equal(pluginTypeId, pluginTypeRef.Id);
    }

    [Fact]
    public async Task SetPlugin_AssemblyName_Verified_WhenMatch()
    {
        // Arrange
        var apiId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        var pluginTypeInfo = new PluginTypeInfo
        {
            Id = pluginTypeId,
            TypeName = "TestNamespace.TestPlugin",
            AssemblyName = "MyAssembly"
        };

        _mockPluginRegistrationService
            .Setup(s => s.GetPluginTypeByNameAsync("TestNamespace.TestPlugin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(pluginTypeInfo);

        // Act — should succeed because assemblyName matches
        await _sut.SetPluginTypeAsync(apiId, "TestNamespace.TestPlugin", "MyAssembly");

        // Assert
        Assert.NotNull(_updatedEntity);
        var pluginTypeRef = _updatedEntity!.GetAttributeValue<EntityReference>(CustomAPI.Fields.PluginTypeId);
        Assert.NotNull(pluginTypeRef);
        Assert.Equal(pluginTypeId, pluginTypeRef.Id);
    }

    [Fact]
    public async Task SetPlugin_AssemblyName_ThrowsWhenMismatch()
    {
        // Arrange
        var apiId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        var pluginTypeInfo = new PluginTypeInfo
        {
            Id = pluginTypeId,
            TypeName = "TestNamespace.TestPlugin",
            AssemblyName = "DifferentAssembly"
        };

        _mockPluginRegistrationService
            .Setup(s => s.GetPluginTypeByNameAsync("TestNamespace.TestPlugin", It.IsAny<CancellationToken>()))
            .ReturnsAsync(pluginTypeInfo);

        // Act & Assert — should throw because assemblyName doesn't match
        var ex = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.SetPluginTypeAsync(apiId, "TestNamespace.TestPlugin", "MyAssembly"));
        Assert.Equal(ErrorCodes.CustomApi.PluginTypeNotFound, ex.ErrorCode);
        Assert.Contains("DifferentAssembly", ex.Message);
    }

    #endregion

    #region Builder Helpers

    private static Entity BuildApiEntity(
        Guid id,
        string uniqueName,
        string displayName,
        int bindingType = 0,
        int allowedStepType = 0,
        bool isFunction = false,
        bool isManaged = false)
    {
        var entity = new Entity(CustomAPI.EntityLogicalName)
        {
            Id = id,
            [CustomAPI.Fields.UniqueName] = uniqueName,
            [CustomAPI.Fields.DisplayName] = displayName,
            [CustomAPI.Fields.BindingType] = new OptionSetValue(bindingType),
            [CustomAPI.Fields.AllowedCustomProcessingStepType] = new OptionSetValue(allowedStepType),
            [CustomAPI.Fields.IsFunction] = isFunction,
            [CustomAPI.Fields.IsPrivate] = false,
            [CustomAPI.Fields.IsManaged] = isManaged
        };
        return entity;
    }

    private static Entity BuildRequestParamEntity(
        Guid id,
        Guid apiId,
        string uniqueName,
        string displayName,
        int type,
        bool isManaged = false)
    {
        return new Entity(CustomAPIRequestParameter.EntityLogicalName)
        {
            Id = id,
            [CustomAPIRequestParameter.Fields.UniqueName] = uniqueName,
            [CustomAPIRequestParameter.Fields.DisplayName] = displayName,
            [CustomAPIRequestParameter.Fields.CustomAPIId] = new EntityReference(CustomAPI.EntityLogicalName, apiId),
            [CustomAPIRequestParameter.Fields.Type] = new OptionSetValue(type),
            [CustomAPIRequestParameter.Fields.IsOptional] = false,
            [CustomAPIRequestParameter.Fields.IsManaged] = isManaged
        };
    }

    private static Entity BuildResponsePropEntity(
        Guid id,
        Guid apiId,
        string uniqueName,
        string displayName,
        int type,
        bool isManaged = false)
    {
        return new Entity(CustomAPIResponseProperty.EntityLogicalName)
        {
            Id = id,
            [CustomAPIResponseProperty.Fields.UniqueName] = uniqueName,
            [CustomAPIResponseProperty.Fields.DisplayName] = displayName,
            [CustomAPIResponseProperty.Fields.CustomAPIId] = new EntityReference(CustomAPI.EntityLogicalName, apiId),
            [CustomAPIResponseProperty.Fields.Type] = new OptionSetValue(type),
            [CustomAPIResponseProperty.Fields.IsManaged] = isManaged
        };
    }

    #endregion
}
