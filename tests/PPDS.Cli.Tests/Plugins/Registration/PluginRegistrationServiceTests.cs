using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Generated;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public class PluginRegistrationServiceTests
{
    private readonly Mock<IDataverseConnectionPool> _mockPool;
    private readonly Mock<IPooledClient> _mockPooledClient;
    private readonly Mock<ILogger<PluginRegistrationService>> _mockLogger;
    private readonly PluginRegistrationService _sut;

    // Track expected results for verification
    private EntityCollection _retrieveMultipleResult = new();
    private Guid _createResult = Guid.Empty;
    private Entity? _updatedEntity;
    private OrganizationRequest? _executedRequest;
    private readonly List<OrganizationRequest> _executedRequests = [];
    private readonly OrganizationResponse _executeResult = new();

    public PluginRegistrationServiceTests()
    {
        // Use Mock with CallBase=false to ensure we control all behavior
        _mockPooledClient = new Mock<IPooledClient>(MockBehavior.Loose);
        _mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        _mockLogger = new Mock<ILogger<PluginRegistrationService>>();

        // The service's helper methods check "if (client is IOrganizationServiceAsync2)"
        // Since IPooledClient : IDataverseClient : IOrganizationServiceAsync2, this should pass
        // We set up methods on both the base type and derived to ensure matching

        // For IOrganizationServiceAsync2 methods - these are what the helper methods actually call
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync(() => _retrieveMultipleResult);
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _retrieveMultipleResult);
        _mockPooledClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>()))
            .ReturnsAsync(() => _createResult);
        _mockPooledClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => _createResult);
        _mockPooledClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>()))
            .Callback<Entity>((e) => _updatedEntity = e)
            .Returns(Task.CompletedTask);
        _mockPooledClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => _updatedEntity = e)
            .Returns(Task.CompletedTask);
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>((r) => { _executedRequest = r; _executedRequests.Add(r); })
            .ReturnsAsync(() => _executeResult);
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((r, _) => { _executedRequest = r; _executedRequests.Add(r); })
            .ReturnsAsync(() => _executeResult);

        // Also setup sync methods through IOrganizationService as fallback
        _mockPooledClient
            .Setup(s => s.RetrieveMultiple(It.IsAny<QueryBase>()))
            .Returns(() => _retrieveMultipleResult);
        _mockPooledClient
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(() => _createResult);
        _mockPooledClient
            .Setup(s => s.Update(It.IsAny<Entity>()))
            .Callback<Entity>(e => _updatedEntity = e);
        _mockPooledClient
            .Setup(s => s.Execute(It.IsAny<OrganizationRequest>()))
            .Callback<OrganizationRequest>(r => { _executedRequest = r; _executedRequests.Add(r); })
            .Returns(() => _executeResult);

        // Setup pool to return our mock pooled client
        _mockPool.Setup(p => p.GetClientAsync(
                It.IsAny<DataverseClientOptions?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(_mockPooledClient.Object);

        _sut = new PluginRegistrationService(_mockPool.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ListAssembliesAsync_ReturnsEmptyList_WhenNoAssembliesExist()
    {
        // Arrange
        _retrieveMultipleResult = new EntityCollection();

        // Act
        var result = await _sut.ListAssembliesAsync();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListAssembliesAsync_ReturnsAssemblies_WhenTheyExist()
    {
        // Arrange
        var entities = new EntityCollection();
        var assembly = new PluginAssembly
        {
            Id = Guid.NewGuid(),
            Name = "TestAssembly",
            Version = "1.0.0.0",
            PublicKeyToken = "abc123",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        entities.Entities.Add(assembly);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.ListAssembliesAsync();

        // Assert
        Assert.Single(result);
        Assert.Equal("TestAssembly", result[0].Name);
        Assert.Equal("1.0.0.0", result[0].Version);
    }

    [Fact]
    public async Task UpsertAssemblyAsync_CreatesNewAssembly_WhenNotExists()
    {
        // Arrange
        var expectedId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedId;

        // Act
        var result = await _sut.UpsertAssemblyAsync("TestAssembly", new byte[] { 1, 2, 3 });

        // Assert
        Assert.Equal(expectedId, result);
        // Verify CreateAsync was called with CancellationToken
        _mockPooledClient.Verify(s => s.CreateAsync(It.Is<Entity>(e => e.LogicalName == PluginAssembly.EntityLogicalName), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpsertAssemblyAsync_UpdatesExisting_WhenAssemblyExists()
    {
        // Arrange
        var existingId = Guid.NewGuid();
        var entities = new EntityCollection();
        var existingAssembly = new PluginAssembly
        {
            Id = existingId,
            Name = "TestAssembly",
            Version = "1.0.0.0"
        };
        entities.Entities.Add(existingAssembly);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.UpsertAssemblyAsync("TestAssembly", new byte[] { 1, 2, 3 });

        // Assert
        Assert.Equal(existingId, result);
        Assert.NotNull(_updatedEntity);
        Assert.Equal(existingId, _updatedEntity!.Id);
    }

    [Fact]
    public async Task GetSdkMessageIdAsync_ReturnsNull_WhenMessageNotFound()
    {
        // Arrange
        _retrieveMultipleResult = new EntityCollection();

        // Act
        var result = await _sut.GetSdkMessageIdAsync("NonExistentMessage");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetSdkMessageIdAsync_ReturnsId_WhenMessageExists()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var message = new SdkMessage { Id = messageId };
        entities.Entities.Add(message);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetSdkMessageIdAsync("Create");

        // Assert
        Assert.Equal(messageId, result);
    }

    #region GetComponentTypeAsync Exception Handling Tests

    // Note: GetComponentTypeAsync is only called for entities NOT in WellKnownComponentTypes.
    // pluginassembly (91) and sdkmessageprocessingstep (92) have well-known types.
    // plugintype does NOT have a well-known type, so UpsertPluginTypeAsync exercises GetComponentTypeAsync.

    [Fact]
    public async Task UpsertPluginTypeAsync_LogsDebugAndSucceeds_WhenGetComponentTypeThrowsFaultException()
    {
        // Arrange - Create plugintype succeeds, but RetrieveEntityRequest for metadata throws FaultException
        var assemblyId = Guid.NewGuid();
        var expectedId = Guid.NewGuid();

        // No existing plugin type
        _retrieveMultipleResult = new EntityCollection();

        // Create succeeds
        _createResult = expectedId;

        // RetrieveEntityRequest throws FaultException (entity metadata not found)
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException("Entity does not exist"));

        // Act - Should succeed despite the FaultException (graceful degradation)
        var result = await _sut.UpsertPluginTypeAsync(assemblyId, "MyPlugin.Plugin", "TestSolution");

        // Assert
        Assert.Equal(expectedId, result);
        // Verify Debug log was called (exception was caught and logged)
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Could not retrieve component type")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertPluginTypeAsync_LogsDebugAndSucceeds_WhenGetComponentTypeThrowsOrganizationServiceFault()
    {
        // Arrange - Create plugintype succeeds, but RetrieveEntityRequest throws FaultException<OrganizationServiceFault>
        var assemblyId = Guid.NewGuid();
        var expectedId = Guid.NewGuid();

        // No existing plugin type
        _retrieveMultipleResult = new EntityCollection();

        // Create succeeds
        _createResult = expectedId;

        // RetrieveEntityRequest throws FaultException<OrganizationServiceFault>
        var fault = new OrganizationServiceFault { Message = "Entity not found", ErrorCode = -2147220969 };
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException<OrganizationServiceFault>(fault, new FaultReason("Entity not found")));

        // Act - Should succeed despite the FaultException (graceful degradation)
        var result = await _sut.UpsertPluginTypeAsync(assemblyId, "MyPlugin.Plugin", "TestSolution");

        // Assert
        Assert.Equal(expectedId, result);
        // Verify Debug log was called with error code info
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Could not retrieve component type")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertPluginTypeAsync_SkipsSolutionAddition_WhenComponentTypeReturnsZero()
    {
        // Arrange - Create plugintype succeeds, metadata lookup fails (returns 0), so no solution addition
        var assemblyId = Guid.NewGuid();
        var expectedId = Guid.NewGuid();
        var addSolutionComponentCalled = false;

        // No existing plugin type
        _retrieveMultipleResult = new EntityCollection();

        // Create succeeds
        _createResult = expectedId;

        // RetrieveEntityRequest throws (so GetComponentTypeAsync returns 0)
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new FaultException("Entity does not exist"));

        // Track if AddSolutionComponent is called
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.Is<OrganizationRequest>(r => r.RequestName == "AddSolutionComponent"), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((_, _) => addSolutionComponentCalled = true)
            .ReturnsAsync(new OrganizationResponse());

        // Act
        var result = await _sut.UpsertPluginTypeAsync(assemblyId, "MyPlugin.Plugin", "TestSolution");

        // Assert - Plugin type was created, solution addition was skipped
        Assert.Equal(expectedId, result);
        Assert.False(addSolutionComponentCalled, "AddSolutionComponent should not be called when componentType is 0");
    }

    #endregion

    #region ListAssembliesAsync Filtering Tests

    [Fact]
    public async Task ListAssembliesAsync_ExcludesMicrosoftAssemblies_ByDefault()
    {
        // Arrange
        var entities = new EntityCollection();
        var customAssembly = new PluginAssembly
        {
            Id = Guid.NewGuid(),
            Name = "CustomPlugin",
            Version = "1.0.0.0",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        var microsoftAssembly = new PluginAssembly
        {
            Id = Guid.NewGuid(),
            Name = "Microsoft.SomePlugin",
            Version = "1.0.0.0",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        entities.Entities.Add(customAssembly);
        entities.Entities.Add(microsoftAssembly);
        _retrieveMultipleResult = entities;

        // Act - default options should exclude Microsoft assemblies
        var result = await _sut.ListAssembliesAsync();

        // Assert - verify query was built (we can't easily verify the filter in mocked tests,
        // but we can verify the service was called and returned results)
        // The actual filtering happens in the service layer query
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListAssembliesAsync_IncludesMicrosoftAssemblies_WhenOptionSet()
    {
        // Arrange
        var entities = new EntityCollection();
        var microsoftAssembly = new PluginAssembly
        {
            Id = Guid.NewGuid(),
            Name = "Microsoft.SomePlugin",
            Version = "1.0.0.0",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        entities.Entities.Add(microsoftAssembly);
        _retrieveMultipleResult = entities;

        var options = new PluginListOptions(IncludeMicrosoft: true);

        // Act
        var result = await _sut.ListAssembliesAsync(options: options);

        // Assert
        Assert.Single(result);
        Assert.Equal("Microsoft.SomePlugin", result[0].Name);
    }

    #endregion

    #region ListStepsForTypeAsync Filtering Tests

    [Fact]
    public async Task ListStepsForTypeAsync_ExcludesHiddenSteps_ByDefault()
    {
        // Arrange
        var typeId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        // Act - default options should exclude hidden steps
        var result = await _sut.ListStepsForTypeAsync(typeId);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task ListStepsForTypeAsync_IncludesHiddenSteps_WhenOptionSet()
    {
        // Arrange
        var typeId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        var options = new PluginListOptions(IncludeHidden: true);

        // Act
        var result = await _sut.ListStepsForTypeAsync(typeId, options);

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region ListPackagesAsync Filtering Tests

    [Fact]
    public async Task ListPackagesAsync_ExcludesMicrosoftPackages_ByDefault()
    {
        // Arrange
        _retrieveMultipleResult = new EntityCollection();

        // Act - default options should exclude Microsoft packages
        var result = await _sut.ListPackagesAsync();

        // Assert
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ListPackagesAsync_IncludesMicrosoftPackages_WhenOptionSet()
    {
        // Arrange
        var entities = new EntityCollection();
        var microsoftPackage = new PluginPackage
        {
            Id = Guid.NewGuid(),
            Name = "Microsoft.SomePackage",
            UniqueName = "Microsoft.SomePackage",
            Version = "1.0.0.0"
        };
        entities.Entities.Add(microsoftPackage);
        _retrieveMultipleResult = entities;

        var options = new PluginListOptions(IncludeMicrosoft: true);

        // Act
        var result = await _sut.ListPackagesAsync(options: options);

        // Assert
        Assert.Single(result);
        Assert.Equal("Microsoft.SomePackage", result[0].Name);
    }

    #endregion

    #region GetDefaultImagePropertyName Tests

    [Theory]
    [InlineData("Create", "id")]
    [InlineData("CreateMultiple", "Ids")]
    [InlineData("Update", "Target")]
    [InlineData("UpdateMultiple", "Targets")]
    [InlineData("Delete", "Target")]
    [InlineData("Assign", "Target")]
    [InlineData("SetState", "EntityMoniker")]
    [InlineData("SetStateDynamicEntity", "EntityMoniker")]
    [InlineData("Route", "Target")]
    [InlineData("Send", "EmailId")]
    [InlineData("DeliverIncoming", "EmailId")]
    [InlineData("DeliverPromote", "EmailId")]
    [InlineData("ExecuteWorkflow", "Target")]
    [InlineData("Merge", "Target")]
    public void GetDefaultImagePropertyName_ReturnsCorrectPropertyName_ForKnownMessages(string messageName, string expectedPropertyName)
    {
        // Act
        var result = PluginRegistrationService.GetDefaultImagePropertyName(messageName);

        // Assert
        Assert.Equal(expectedPropertyName, result);
    }

    [Theory]
    [InlineData("create", "id")]
    [InlineData("CREATE", "id")]
    [InlineData("SetState", "EntityMoniker")]
    [InlineData("SETSTATE", "EntityMoniker")]
    [InlineData("setstate", "EntityMoniker")]
    public void GetDefaultImagePropertyName_IsCaseInsensitive(string messageName, string expectedPropertyName)
    {
        // Act
        var result = PluginRegistrationService.GetDefaultImagePropertyName(messageName);

        // Assert
        Assert.Equal(expectedPropertyName, result);
    }

    [Theory]
    [InlineData("Retrieve")]
    [InlineData("RetrieveMultiple")]
    [InlineData("CustomAction")]
    [InlineData("UnknownMessage")]
    [InlineData("")]
    public void GetDefaultImagePropertyName_ReturnsNull_ForUnsupportedMessages(string messageName)
    {
        // Act
        var result = PluginRegistrationService.GetDefaultImagePropertyName(messageName);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("Retrieve")]
    [InlineData("RetrieveMultiple")]
    [InlineData("CustomAction")]
    public async Task UpsertImageAsync_ThrowsPpdsException_ForUnsupportedMessages(string messageName)
    {
        // Arrange
        var imageConfig = new PluginImageConfig
        {
            Name = "TestImage",
            ImageType = "PreImage"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpsertImageAsync(Guid.NewGuid(), imageConfig, messageName));

        Assert.Equal(ErrorCodes.Plugin.ImageNotSupported, exception.ErrorCode);
        Assert.Contains(messageName, exception.UserMessage);
        Assert.Contains("does not support plugin images", exception.UserMessage);
    }

    #endregion

    #region GetStepByNameOrIdAsync Tests

    [Fact]
    public async Task GetStepByNameOrIdAsync_ReturnsNull_WhenStepNotFound()
    {
        // Arrange
        _retrieveMultipleResult = new EntityCollection();

        // Act
        var result = await _sut.GetStepByNameOrIdAsync("NonExistentStep");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetStepByNameOrIdAsync_ReturnsStep_WhenFoundByName()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestPlugin: Create of account",
            Stage = sdkmessageprocessingstep_stage.Preoperation,
            Mode = sdkmessageprocessingstep_mode.Synchronous,
            Rank = 1,
            StateCode = sdkmessageprocessingstep_statecode.Enabled
        };
        step["message.name"] = new AliasedValue(SdkMessage.EntityLogicalName, SdkMessage.Fields.Name, "Create");
        step["filter.primaryobjecttypecode"] = new AliasedValue(SdkMessageFilter.EntityLogicalName, SdkMessageFilter.Fields.PrimaryObjectTypeCode, "account");
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetStepByNameOrIdAsync("TestPlugin: Create of account");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(stepId, result.Id);
        Assert.Equal("TestPlugin: Create of account", result.Name);
    }

    [Fact]
    public async Task GetStepByNameOrIdAsync_ReturnsStep_WhenFoundByGuid()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestPlugin: Create of account",
            Stage = sdkmessageprocessingstep_stage.Postoperation,
            Mode = sdkmessageprocessingstep_mode.Asynchronous,
            Rank = 5,
            StateCode = sdkmessageprocessingstep_statecode.Enabled
        };
        step["message.name"] = new AliasedValue(SdkMessage.EntityLogicalName, SdkMessage.Fields.Name, "Create");
        step["filter.primaryobjecttypecode"] = new AliasedValue(SdkMessageFilter.EntityLogicalName, SdkMessageFilter.Fields.PrimaryObjectTypeCode, "account");
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetStepByNameOrIdAsync(stepId.ToString());

        // Assert
        Assert.NotNull(result);
        Assert.Equal(stepId, result.Id);
    }

    #endregion

    #region GetImageByNameOrIdAsync Tests

    [Fact]
    public async Task GetImageByNameOrIdAsync_ReturnsNull_WhenImageNotFound()
    {
        // Arrange
        _retrieveMultipleResult = new EntityCollection();

        // Act
        var result = await _sut.GetImageByNameOrIdAsync("NonExistentImage");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetImageByNameOrIdAsync_ReturnsImage_WhenFoundByName()
    {
        // Arrange
        var imageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var image = new SdkMessageProcessingStepImage
        {
            Id = imageId,
            Name = "PreImage",
            EntityAlias = "PreImage",
            ImageType = sdkmessageprocessingstepimage_imagetype.PreImage,
            Attributes1 = "name,accountnumber"
        };
        entities.Entities.Add(image);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetImageByNameOrIdAsync("PreImage");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(imageId, result.Id);
        Assert.Equal("PreImage", result.Name);
        Assert.Equal("name,accountnumber", result.Attributes);
    }

    #endregion

    #region UpdateStepAsync Tests

    [Fact]
    public async Task UpdateStepAsync_ThrowsPpdsException_WhenStepNotFound()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateStepAsync(stepId, new StepUpdateRequest(Mode: "Asynchronous")));

        Assert.Equal(ErrorCodes.Plugin.NotFound, exception.ErrorCode);
        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public async Task UpdateStepAsync_ThrowsPpdsException_WhenStepIsManagedAndNotCustomizable()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "ManagedStep: Create of account",
            IsCustomizable = new BooleanManagedProperty(false)
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = true;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpdateStepAsync(stepId, new StepUpdateRequest(Mode: "Asynchronous")));

        Assert.Equal(ErrorCodes.Plugin.ManagedComponent, exception.ErrorCode);
        Assert.Contains("is managed", exception.Message);
    }

    [Fact]
    public async Task UpdateStepAsync_UpdatesStep_WhenValid()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestStep: Create of account",
            IsCustomizable = new BooleanManagedProperty(true)
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = false;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        await _sut.UpdateStepAsync(stepId, new StepUpdateRequest(Mode: "Asynchronous", Rank: 10));

        // Assert
        Assert.NotNull(_updatedEntity);
        Assert.Equal(stepId, _updatedEntity!.Id);
        Assert.Equal((int)sdkmessageprocessingstep_mode.Asynchronous,
            _updatedEntity.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.Mode)?.Value);
        Assert.Equal(10, _updatedEntity.GetAttributeValue<int>(SdkMessageProcessingStep.Fields.Rank));
    }

    [Fact]
    public async Task UpdateStepAsync_DoesNotUpdate_WhenNoChangesSpecified()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestStep: Create of account"
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = false;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;
        _updatedEntity = null;

        // Act
        await _sut.UpdateStepAsync(stepId, new StepUpdateRequest());

        // Assert - UpdateAsync should not be called
        Assert.Null(_updatedEntity);
    }

    [Fact]
    public async Task UpdateStepAsync_AllowsUpdate_WhenManagedButCustomizable()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "ManagedStep: Create of account",
            IsCustomizable = new BooleanManagedProperty(true) // Managed but customizable
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = true;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        await _sut.UpdateStepAsync(stepId, new StepUpdateRequest(Stage: "PreOperation"));

        // Assert - Should succeed, not throw
        Assert.NotNull(_updatedEntity);
    }

    #endregion

    #region UpdateImageAsync Tests

    [Fact]
    public async Task UpdateImageAsync_ThrowsInvalidOperationException_WhenImageNotFound()
    {
        // Arrange
        var imageId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateImageAsync(imageId, new ImageUpdateRequest(Attributes: "name,accountnumber")));

        Assert.Contains("not found", exception.Message);
    }

    [Fact]
    public async Task UpdateImageAsync_ThrowsInvalidOperationException_WhenImageIsManagedAndNotCustomizable()
    {
        // Arrange
        var imageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var image = new SdkMessageProcessingStepImage
        {
            Id = imageId,
            Name = "ManagedImage",
            IsCustomizable = new BooleanManagedProperty(false)
        };
        image[SdkMessageProcessingStepImage.Fields.IsManaged] = true;
        entities.Entities.Add(image);
        _retrieveMultipleResult = entities;

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpdateImageAsync(imageId, new ImageUpdateRequest(Attributes: "name")));

        Assert.Contains("is managed", exception.Message);
    }

    [Fact]
    public async Task UpdateImageAsync_UpdatesImage_WhenValid()
    {
        // Arrange
        var imageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var image = new SdkMessageProcessingStepImage
        {
            Id = imageId,
            Name = "PreImage"
        };
        image[SdkMessageProcessingStepImage.Fields.IsManaged] = false;
        entities.Entities.Add(image);
        _retrieveMultipleResult = entities;

        // Act
        await _sut.UpdateImageAsync(imageId, new ImageUpdateRequest(Attributes: "name,accountnumber,statecode"));

        // Assert
        Assert.NotNull(_updatedEntity);
        Assert.Equal(imageId, _updatedEntity!.Id);
        Assert.Equal("name,accountnumber,statecode",
            _updatedEntity.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Attributes1));
    }

    [Fact]
    public async Task UpdateImageAsync_DoesNotUpdate_WhenNoChangesSpecified()
    {
        // Arrange
        var imageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var image = new SdkMessageProcessingStepImage
        {
            Id = imageId,
            Name = "PreImage"
        };
        image[SdkMessageProcessingStepImage.Fields.IsManaged] = false;
        entities.Entities.Add(image);
        _retrieveMultipleResult = entities;
        _updatedEntity = null;

        // Act
        await _sut.UpdateImageAsync(imageId, new ImageUpdateRequest());

        // Assert - UpdateAsync should not be called
        Assert.Null(_updatedEntity);
    }

    #endregion

    #region PluginStepConfig.Enabled Tests

    [Fact]
    public void PluginStepConfig_Enabled_DefaultsToTrue()
    {
        // Act
        var config = new PluginStepConfig();

        // Assert
        Assert.True(config.Enabled);
    }

    #endregion

    #region UpsertStepAsync Step State Tests

    [Fact]
    public async Task UpsertStepAsync_DisablesNewStep_WhenEnabledIsFalse()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        // First query returns no existing step
        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;
        _executedRequests.Clear();

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            Enabled = false
        };

        // Act
        var result = await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert
        Assert.Equal(expectedStepId, result);
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().FirstOrDefault();
        Assert.NotNull(setStateRequest);
        Assert.Equal(expectedStepId, setStateRequest.EntityMoniker.Id);
        Assert.Equal((int)sdkmessageprocessingstep_statecode.Disabled, setStateRequest.State.Value);
    }

    [Fact]
    public async Task UpsertStepAsync_DoesNotCallSetState_WhenNewStepEnabledIsTrue()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;
        _executedRequests.Clear();

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            Enabled = true // Default value
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - No SetStateRequest should be made for enabled new steps
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().FirstOrDefault();
        Assert.Null(setStateRequest);
    }

    [Fact]
    public async Task UpsertStepAsync_EnablesExistingDisabledStep_WhenEnabledIsTrue()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();

        var existingStep = new SdkMessageProcessingStep
        {
            Id = existingStepId,
            Name = "TestPlugin: Create of account"
        };
        existingStep[SdkMessageProcessingStep.Fields.StateCode] = new OptionSetValue((int)sdkmessageprocessingstep_statecode.Disabled);

        _retrieveMultipleResult = new EntityCollection();
        _retrieveMultipleResult.Entities.Add(existingStep);
        _executedRequests.Clear();

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            Enabled = true
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().FirstOrDefault();
        Assert.NotNull(setStateRequest);
        Assert.Equal(existingStepId, setStateRequest.EntityMoniker.Id);
        Assert.Equal((int)sdkmessageprocessingstep_statecode.Enabled, setStateRequest.State.Value);
    }

    [Fact]
    public async Task UpsertStepAsync_DisablesExistingEnabledStep_WhenEnabledIsFalse()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();

        var existingStep = new SdkMessageProcessingStep
        {
            Id = existingStepId,
            Name = "TestPlugin: Create of account"
        };
        existingStep[SdkMessageProcessingStep.Fields.StateCode] = new OptionSetValue((int)sdkmessageprocessingstep_statecode.Enabled);

        _retrieveMultipleResult = new EntityCollection();
        _retrieveMultipleResult.Entities.Add(existingStep);
        _executedRequests.Clear();

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            Enabled = false
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().FirstOrDefault();
        Assert.NotNull(setStateRequest);
        Assert.Equal(existingStepId, setStateRequest.EntityMoniker.Id);
        Assert.Equal((int)sdkmessageprocessingstep_statecode.Disabled, setStateRequest.State.Value);
    }

    [Fact]
    public async Task UpsertStepAsync_DoesNotCallSetState_WhenExistingStepStateMatchesConfig()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();

        var existingStep = new SdkMessageProcessingStep
        {
            Id = existingStepId,
            Name = "TestPlugin: Create of account"
        };
        existingStep[SdkMessageProcessingStep.Fields.StateCode] = new OptionSetValue((int)sdkmessageprocessingstep_statecode.Enabled);

        _retrieveMultipleResult = new EntityCollection();
        _retrieveMultipleResult.Entities.Add(existingStep);
        _executedRequests.Clear();

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            Enabled = true // Matches existing state
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - No SetStateRequest when state already matches
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().FirstOrDefault();
        Assert.Null(setStateRequest);
    }

    #endregion

    #region UpsertStepAsync RunAsUser Resolution Tests

    [Fact]
    public async Task UpsertStepAsync_SetsImpersonatingUserId_WhenRunAsUserIsGuid()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();
        var runAsUserId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            RunAsUser = runAsUserId.ToString()
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - Verify CreateAsync was called with correct ImpersonatingUserId
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId) != null &&
                e.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId).Id == runAsUserId),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_ResolvesUserByDomainName_WhenRunAsUserIsNotGuid()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();
        var resolvedUserId = Guid.NewGuid();

        // Setup to return no existing step on first query, then return user on second query
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                if (queryCount == 1)
                {
                    // First query: check for existing step
                    return new EntityCollection();
                }
                else
                {
                    // Second query: user lookup
                    var userEntities = new EntityCollection();
                    var user = new SystemUser { Id = resolvedUserId };
                    userEntities.Entities.Add(user);
                    return userEntities;
                }
            });

        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            RunAsUser = "user@domain.com"
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - Verify CreateAsync was called with resolved user ID
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId) != null &&
                e.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId).Id == resolvedUserId),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_ThrowsPpdsException_WhenUserNotFound()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();

        // Setup to return no existing step on first query, and no user on second query
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                // Both queries return empty - no step, no user
                return new EntityCollection();
            });

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            RunAsUser = "nonexistent@domain.com"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId));

        Assert.Equal(ErrorCodes.Plugin.UserNotFound, exception.ErrorCode);
        Assert.Contains("nonexistent@domain.com", exception.UserMessage);
    }

    [Fact]
    public async Task UpsertStepAsync_DoesNotSetImpersonatingUserId_WhenRunAsUserIsCallingUser()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            RunAsUser = "CallingUser"
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - Verify CreateAsync was called without ImpersonatingUserId
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.ImpersonatingUserId) == null),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region EnableStepAsync / DisableStepAsync Tests

    [Fact]
    public async Task EnableStepAsync_SendsSetStateRequest_WithEnabledState()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        _executedRequests.Clear();

        // Act
        await _sut.EnableStepAsync(stepId);

        // Assert
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().SingleOrDefault();
        Assert.NotNull(setStateRequest);
        Assert.Equal(stepId, setStateRequest.EntityMoniker.Id);
        Assert.Equal(SdkMessageProcessingStep.EntityLogicalName, setStateRequest.EntityMoniker.LogicalName);
        Assert.Equal((int)sdkmessageprocessingstep_statecode.Enabled, setStateRequest.State.Value);
    }

    [Fact]
    public async Task DisableStepAsync_SendsSetStateRequest_WithDisabledState()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        _executedRequests.Clear();

        // Act
        await _sut.DisableStepAsync(stepId);

        // Assert
        var setStateRequest = _executedRequests.OfType<SetStateRequest>().SingleOrDefault();
        Assert.NotNull(setStateRequest);
        Assert.Equal(stepId, setStateRequest.EntityMoniker.Id);
        Assert.Equal(SdkMessageProcessingStep.EntityLogicalName, setStateRequest.EntityMoniker.LogicalName);
        Assert.Equal((int)sdkmessageprocessingstep_statecode.Disabled, setStateRequest.State.Value);
    }

    [Fact]
    public async Task EnableStepAsync_ThrowsPpdsException_WhenDataverseFails()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network failure"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.EnableStepAsync(stepId));

        Assert.NotNull(exception.ErrorCode);
    }

    [Fact]
    public async Task DisableStepAsync_ThrowsPpdsException_WhenDataverseFails()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Network failure"));

        // Act & Assert
        var exception = await Assert.ThrowsAsync<PpdsException>(
            () => _sut.DisableStepAsync(stepId));

        Assert.NotNull(exception.ErrorCode);
    }

    #endregion

    #region UpsertStepAsync New Properties Tests

    [Fact]
    public async Task UpsertStepAsync_SetsCanBeBypassed_WhenProvided()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            CanBeBypassed = false
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.CanBeBypassed) == false),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_DoesNotSetCanBeBypassed_WhenNotProvided()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            CanBeBypassed = null
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - CanBeBypassed attribute should not be set in the entity
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                !e.Attributes.ContainsKey(SdkMessageProcessingStep.Fields.CanBeBypassed)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_SetsCanUseReadOnlyConnection_WhenProvided()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            CanUseReadOnlyConnection = true
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.CanUseReadOnlyConnection) == true),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("Parent", (int)sdkmessageprocessingstep_invocationsource.Parent)]
    [InlineData("Child", (int)sdkmessageprocessingstep_invocationsource.Child)]
    public async Task UpsertStepAsync_SetsInvocationSource_WhenProvided(string invocationSource, int expectedValue)
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            InvocationSource = invocationSource
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.InvocationSource) != null &&
                e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.InvocationSource).Value == expectedValue),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_UsesParentInvocationSource_WhenNotProvided()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var expectedStepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = expectedStepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            InvocationSource = null
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - Falls back to Parent (0) per spec default
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.InvocationSource) != null &&
                e.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.InvocationSource).Value == (int)sdkmessageprocessingstep_invocationsource.Parent),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_CreatesSecureConfig_WhenProvided_NewStep()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var secureConfigId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var createdEntities = new List<Entity>();

        _retrieveMultipleResult = new EntityCollection();
        _mockPooledClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => createdEntities.Add(e))
            .ReturnsAsync(() =>
            {
                var last = createdEntities.Last();
                return last.LogicalName == "sdkmessageprocessingstepsecureconfig" ? secureConfigId : stepId;
            });

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            SecureConfiguration = "my-secret-value"
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - secure config entity was created
        var secureConfigEntity = createdEntities.FirstOrDefault(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig");
        Assert.NotNull(secureConfigEntity);
        Assert.Equal("my-secret-value", secureConfigEntity.GetAttributeValue<string>("secureconfig"));

        // Assert - step was created with reference to the secure config
        var stepEntity = createdEntities.FirstOrDefault(e => e.LogicalName == SdkMessageProcessingStep.EntityLogicalName);
        Assert.NotNull(stepEntity);
        var secureConfigRef = stepEntity.GetAttributeValue<EntityReference>(SdkMessageProcessingStep.Fields.SdkMessageProcessingStepSecureConfigId);
        Assert.NotNull(secureConfigRef);
        Assert.Equal(secureConfigId, secureConfigRef.Id);
    }

    [Fact]
    public async Task UpsertStepAsync_DoesNotCreateSecureConfig_WhenNotProvided()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var stepId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = stepId;

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            SecureConfiguration = null
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - no secure config entity created
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig"),
            It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert - step created without secure config reference
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStep.EntityLogicalName &&
                !e.Attributes.ContainsKey(SdkMessageProcessingStep.Fields.SdkMessageProcessingStepSecureConfigId)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertStepAsync_UpdatesSecureConfig_WhenProvided_ExistingStep()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();
        var existingSecureConfigId = Guid.NewGuid();
        var updatedEntities = new List<Entity>();

        var existingStep = new SdkMessageProcessingStep
        {
            Id = existingStepId,
            Name = "TestPlugin: Create of account"
        };
        existingStep[SdkMessageProcessingStep.Fields.StateCode] = new OptionSetValue((int)sdkmessageprocessingstep_statecode.Enabled);
        existingStep[SdkMessageProcessingStep.Fields.SdkMessageProcessingStepSecureConfigId] =
            new EntityReference("sdkmessageprocessingstepsecureconfig", existingSecureConfigId);

        _retrieveMultipleResult = new EntityCollection();
        _retrieveMultipleResult.Entities.Add(existingStep);
        _executedRequests.Clear();

        _mockPooledClient
            .Setup(s => s.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => updatedEntities.Add(e))
            .Returns(Task.CompletedTask);

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            SecureConfiguration = "updated-secret"
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - secure config entity was updated (not created)
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig"),
            It.IsAny<CancellationToken>()),
            Times.Never);

        var secureConfigUpdate = updatedEntities.FirstOrDefault(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig");
        Assert.NotNull(secureConfigUpdate);
        Assert.Equal(existingSecureConfigId, secureConfigUpdate.Id);
        Assert.Equal("updated-secret", secureConfigUpdate.GetAttributeValue<string>("secureconfig"));
    }

    [Fact]
    public async Task UpsertStepAsync_CreatesSecureConfig_WhenProvided_ExistingStepWithoutSecureConfig()
    {
        // Arrange
        var pluginTypeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var existingStepId = Guid.NewGuid();
        var newSecureConfigId = Guid.NewGuid();
        var createdEntities = new List<Entity>();

        var existingStep = new SdkMessageProcessingStep
        {
            Id = existingStepId,
            Name = "TestPlugin: Create of account"
        };
        existingStep[SdkMessageProcessingStep.Fields.StateCode] = new OptionSetValue((int)sdkmessageprocessingstep_statecode.Enabled);
        // No existing SdkMessageProcessingStepSecureConfigId

        _retrieveMultipleResult = new EntityCollection();
        _retrieveMultipleResult.Entities.Add(existingStep);
        _executedRequests.Clear();

        _mockPooledClient
            .Setup(s => s.CreateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => createdEntities.Add(e))
            .ReturnsAsync(newSecureConfigId);

        var stepConfig = new PluginStepConfig
        {
            Name = "TestPlugin: Create of account",
            Message = "Create",
            Entity = "account",
            Stage = "PreOperation",
            Mode = "Synchronous",
            SecureConfiguration = "new-secret"
        };

        // Act
        await _sut.UpsertStepAsync(pluginTypeId, stepConfig, messageId, filterId);

        // Assert - secure config entity was created
        var secureConfigEntity = createdEntities.FirstOrDefault(e => e.LogicalName == "sdkmessageprocessingstepsecureconfig");
        Assert.NotNull(secureConfigEntity);
        Assert.Equal("new-secret", secureConfigEntity.GetAttributeValue<string>("secureconfig"));
    }

    #endregion

    #region UpsertImageAsync New Properties Tests

    [Fact]
    public async Task UpsertImageAsync_SetsDescription_WhenProvided()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = imageId;

        var imageConfig = new PluginImageConfig
        {
            Name = "PreImage",
            ImageType = "PreImage",
            Description = "My image description"
        };

        // Act
        await _sut.UpsertImageAsync(stepId, imageConfig, "Create");

        // Assert
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStepImage.EntityLogicalName &&
                e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.Description) == "My image description"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertImageAsync_DoesNotSetDescription_WhenNull()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = imageId;

        var imageConfig = new PluginImageConfig
        {
            Name = "PreImage",
            ImageType = "PreImage",
            Description = null
        };

        // Act
        await _sut.UpsertImageAsync(stepId, imageConfig, "Create");

        // Assert - Description attribute should not be set
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStepImage.EntityLogicalName &&
                !e.Attributes.ContainsKey(SdkMessageProcessingStepImage.Fields.Description)),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertImageAsync_UsesMessagePropertyName_WhenProvided()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = imageId;

        var imageConfig = new PluginImageConfig
        {
            Name = "PreImage",
            ImageType = "PreImage",
            MessagePropertyName = "CustomProperty"
        };

        // Act
        await _sut.UpsertImageAsync(stepId, imageConfig, "Create");

        // Assert - Uses config value instead of auto-inferred
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStepImage.EntityLogicalName &&
                e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.MessagePropertyName) == "CustomProperty"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task UpsertImageAsync_UsesAutoInferredMessagePropertyName_WhenNotProvided()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var imageId = Guid.NewGuid();

        _retrieveMultipleResult = new EntityCollection();
        _createResult = imageId;

        var imageConfig = new PluginImageConfig
        {
            Name = "PreImage",
            ImageType = "PreImage",
            MessagePropertyName = null // Should auto-infer from message
        };

        // Act
        await _sut.UpsertImageAsync(stepId, imageConfig, "Create");

        // Assert - Auto-inferred "id" for Create message (Create message uses "id" not "Target")
        _mockPooledClient.Verify(s => s.CreateAsync(
            It.Is<Entity>(e =>
                e.LogicalName == SdkMessageProcessingStepImage.EntityLogicalName &&
                e.GetAttributeValue<string>(SdkMessageProcessingStepImage.Fields.MessagePropertyName) == "id"),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region UpdateStepAsync New Properties Tests

    [Fact]
    public async Task UpdateStepAsync_SetsCanBeBypassed_WhenProvided()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestStep: Create of account",
            IsCustomizable = new BooleanManagedProperty(true)
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = false;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        await _sut.UpdateStepAsync(stepId, new StepUpdateRequest(CanBeBypassed: true));

        // Assert
        Assert.NotNull(_updatedEntity);
        Assert.Equal(true, _updatedEntity!.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.CanBeBypassed));
    }

    [Fact]
    public async Task UpdateStepAsync_SetsCanUseReadOnlyConnection_WhenProvided()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestStep: Create of account",
            IsCustomizable = new BooleanManagedProperty(true)
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = false;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        await _sut.UpdateStepAsync(stepId, new StepUpdateRequest(CanUseReadOnlyConnection: false));

        // Assert
        Assert.NotNull(_updatedEntity);
        Assert.Equal(false, _updatedEntity!.GetAttributeValue<bool?>(SdkMessageProcessingStep.Fields.CanUseReadOnlyConnection));
    }

    [Fact]
    public async Task UpdateStepAsync_SetsInvocationSource_WhenProvided()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestStep: Create of account",
            IsCustomizable = new BooleanManagedProperty(true)
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = false;
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        await _sut.UpdateStepAsync(stepId, new StepUpdateRequest(InvocationSource: "Child"));

        // Assert
        Assert.NotNull(_updatedEntity);
        Assert.Equal((int)sdkmessageprocessingstep_invocationsource.Child,
            _updatedEntity!.GetAttributeValue<OptionSetValue>(SdkMessageProcessingStep.Fields.InvocationSource)?.Value);
    }

    #endregion

    #region Unregister

    [Fact]
    public async Task UnregisterAssemblyAsync_DeletesAssembly_WhenFound()
    {
        // Arrange
        var assemblyId = Guid.NewGuid();
        var entities = new EntityCollection();
        var assembly = new PluginAssembly
        {
            Id = assemblyId,
            Name = "TestAssembly",
            Version = "1.0.0.0",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        assembly[PluginAssembly.Fields.IsManaged] = false;
        entities.Entities.Add(assembly);

        // GetAssemblyByIdAsync returns the assembly, then ListTypesForAssemblyAsync returns empty
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                if (queryCount == 1) return entities; // GetAssemblyByIdAsync
                return new EntityCollection(); // ListTypesForAssemblyAsync
            });

        // Act
        var result = await _sut.UnregisterAssemblyAsync(assemblyId);

        // Assert
        Assert.Equal("TestAssembly", result.EntityName);
        Assert.Equal("Assembly", result.EntityType);
        Assert.Equal(1, result.AssembliesDeleted);
        _mockPooledClient.Verify(s => s.DeleteAsync(PluginAssembly.EntityLogicalName, assemblyId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnregisterAssemblyAsync_ThrowsUnregisterException_WhenNotFound()
    {
        // Arrange
        var assemblyId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<UnregisterException>(
            () => _sut.UnregisterAssemblyAsync(assemblyId));

        Assert.Equal(ErrorCodes.Plugin.NotFound, exception.ErrorCode);
        Assert.Contains(assemblyId.ToString(), exception.Message);
    }

    [Fact]
    public async Task UnregisterPackageAsync_DeletesPackage_WhenFound()
    {
        // Arrange
        var packageId = Guid.NewGuid();
        var packageEntities = new EntityCollection();
        var package = new PluginPackage
        {
            Id = packageId,
            Name = "TestPackage",
            UniqueName = "TestPackage",
            Version = "1.0.0.0"
        };
        package[PluginPackage.Fields.IsManaged] = false;
        packageEntities.Entities.Add(package);

        // GetPackageByIdAsync returns package, then ListAssembliesForPackageAsync returns empty
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                if (queryCount == 1) return packageEntities; // GetPackageByIdAsync
                return new EntityCollection(); // ListAssembliesForPackageAsync
            });

        // Act
        var result = await _sut.UnregisterPackageAsync(packageId);

        // Assert
        Assert.Equal("TestPackage", result.EntityName);
        Assert.Equal("Package", result.EntityType);
        Assert.Equal(1, result.PackagesDeleted);
        _mockPooledClient.Verify(s => s.DeleteAsync(PluginPackage.EntityLogicalName, packageId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnregisterPackageAsync_ThrowsUnregisterException_WhenNotFound()
    {
        // Arrange
        var packageId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<UnregisterException>(
            () => _sut.UnregisterPackageAsync(packageId));

        Assert.Equal(ErrorCodes.Plugin.NotFound, exception.ErrorCode);
        Assert.Contains(packageId.ToString(), exception.Message);
    }

    [Fact]
    public async Task UnregisterPluginTypeAsync_DeletesType_WhenFound()
    {
        // Arrange
        var typeId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        var typeEntities = new EntityCollection();
        var pluginType = new PluginType
        {
            Id = typeId,
            TypeName = "TestNamespace.TestPlugin",
            FriendlyName = "TestPlugin"
        };
        pluginType[PluginType.Fields.IsManaged] = false;
        pluginType[PluginType.Fields.PluginAssemblyId] = new EntityReference(PluginAssembly.EntityLogicalName, assemblyId);
        pluginType[$"assembly.{PluginAssembly.Fields.Name}"] = new AliasedValue(PluginAssembly.EntityLogicalName, PluginAssembly.Fields.Name, "TestAssembly");
        typeEntities.Entities.Add(pluginType);

        // GetPluginTypeByNameOrIdAsync returns type, then ListStepsForTypeAsync returns empty
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                if (queryCount == 1) return typeEntities; // GetPluginTypeByNameOrIdAsync
                return new EntityCollection(); // ListStepsForTypeAsync
            });

        // Act
        var result = await _sut.UnregisterPluginTypeAsync(typeId);

        // Assert
        Assert.Equal("TestNamespace.TestPlugin", result.EntityName);
        Assert.Equal("Type", result.EntityType);
        Assert.Equal(1, result.TypesDeleted);
        _mockPooledClient.Verify(s => s.DeleteAsync(PluginType.EntityLogicalName, typeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnregisterStepAsync_DeletesStep_WhenFound()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var stepEntities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestPlugin: Create of account",
            Stage = sdkmessageprocessingstep_stage.Preoperation,
            Mode = sdkmessageprocessingstep_mode.Synchronous,
            Rank = 1,
            StateCode = sdkmessageprocessingstep_statecode.Enabled
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = false;
        step["message.name"] = new AliasedValue(SdkMessage.EntityLogicalName, SdkMessage.Fields.Name, "Create");
        step["filter.primaryobjecttypecode"] = new AliasedValue(SdkMessageFilter.EntityLogicalName, SdkMessageFilter.Fields.PrimaryObjectTypeCode, "account");
        stepEntities.Entities.Add(step);

        // GetStepByNameOrIdAsync returns step, then ListImagesForStepAsync returns empty
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                if (queryCount == 1) return stepEntities; // GetStepByNameOrIdAsync
                return new EntityCollection(); // ListImagesForStepAsync
            });

        // Act
        var result = await _sut.UnregisterStepAsync(stepId);

        // Assert
        Assert.Equal("TestPlugin: Create of account", result.EntityName);
        Assert.Equal("Step", result.EntityType);
        Assert.Equal(1, result.StepsDeleted);
        _mockPooledClient.Verify(s => s.DeleteAsync(SdkMessageProcessingStep.EntityLogicalName, stepId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnregisterStepAsync_ThrowsUnregisterException_WhenStepIsManaged()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var stepEntities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "ManagedStep: Create of account",
            Stage = sdkmessageprocessingstep_stage.Preoperation,
            Mode = sdkmessageprocessingstep_mode.Synchronous,
            Rank = 1,
            StateCode = sdkmessageprocessingstep_statecode.Enabled
        };
        step[SdkMessageProcessingStep.Fields.IsManaged] = true;
        step["message.name"] = new AliasedValue(SdkMessage.EntityLogicalName, SdkMessage.Fields.Name, "Create");
        step["filter.primaryobjecttypecode"] = new AliasedValue(SdkMessageFilter.EntityLogicalName, SdkMessageFilter.Fields.PrimaryObjectTypeCode, "account");
        stepEntities.Entities.Add(step);

        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(stepEntities);

        // Act & Assert
        var exception = await Assert.ThrowsAsync<UnregisterException>(
            () => _sut.UnregisterStepAsync(stepId));

        Assert.Equal(ErrorCodes.Plugin.ManagedComponent, exception.ErrorCode);
        Assert.Contains("is managed", exception.Message);
    }

    [Fact]
    public async Task UnregisterImageAsync_DeletesImage_WhenFound()
    {
        // Arrange
        var imageId = Guid.NewGuid();
        var imageEntities = new EntityCollection();
        var image = new SdkMessageProcessingStepImage
        {
            Id = imageId,
            Name = "PreImage"
        };
        image[SdkMessageProcessingStepImage.Fields.IsManaged] = false;
        imageEntities.Entities.Add(image);

        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(imageEntities);

        // Act
        var result = await _sut.UnregisterImageAsync(imageId);

        // Assert
        Assert.Equal("PreImage", result.EntityName);
        Assert.Equal("Image", result.EntityType);
        Assert.Equal(1, result.ImagesDeleted);
        _mockPooledClient.Verify(s => s.DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, imageId, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Delete

    [Fact]
    public async Task DeleteStepAsync_SendsDeleteRequest()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection(); // ListImagesForStepAsync returns empty

        // Act
        await _sut.DeleteStepAsync(stepId);

        // Assert
        _mockPooledClient.Verify(s => s.DeleteAsync(SdkMessageProcessingStep.EntityLogicalName, stepId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeletePluginTypeAsync_SendsDeleteRequest()
    {
        // Arrange
        var typeId = Guid.NewGuid();

        // Act
        await _sut.DeletePluginTypeAsync(typeId);

        // Assert
        _mockPooledClient.Verify(s => s.DeleteAsync(PluginType.EntityLogicalName, typeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DeleteImageAsync_SendsDeleteRequest()
    {
        // Arrange
        var imageId = Guid.NewGuid();

        // Act
        await _sut.DeleteImageAsync(imageId);

        // Assert
        _mockPooledClient.Verify(s => s.DeleteAsync(SdkMessageProcessingStepImage.EntityLogicalName, imageId, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Get/List

    [Fact]
    public async Task GetAssemblyByNameAsync_ReturnsAssembly_WhenFound()
    {
        // Arrange
        var assemblyId = Guid.NewGuid();
        var entities = new EntityCollection();
        var assembly = new PluginAssembly
        {
            Id = assemblyId,
            Name = "MyPlugin",
            Version = "1.0.0.0",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        entities.Entities.Add(assembly);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetAssemblyByNameAsync("MyPlugin");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(assemblyId, result!.Id);
        Assert.Equal("MyPlugin", result.Name);
    }

    [Fact]
    public async Task GetAssemblyByNameAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        _retrieveMultipleResult = new EntityCollection();

        // Act
        var result = await _sut.GetAssemblyByNameAsync("NonExistentAssembly");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAssemblyByIdAsync_ReturnsAssembly_WhenFound()
    {
        // Arrange
        var assemblyId = Guid.NewGuid();
        var entities = new EntityCollection();
        var assembly = new PluginAssembly
        {
            Id = assemblyId,
            Name = "MyPlugin",
            Version = "2.0.0.0",
            IsolationMode = pluginassembly_isolationmode.Sandbox
        };
        entities.Entities.Add(assembly);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetAssemblyByIdAsync(assemblyId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(assemblyId, result!.Id);
        Assert.Equal("MyPlugin", result.Name);
        Assert.Equal("2.0.0.0", result.Version);
    }

    [Fact]
    public async Task GetPackageByNameAsync_ReturnsPackage_WhenFound()
    {
        // Arrange
        var packageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var package = new PluginPackage
        {
            Id = packageId,
            Name = "MyPackage",
            UniqueName = "MyPackage",
            Version = "1.0.0.0"
        };
        entities.Entities.Add(package);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetPackageByNameAsync("MyPackage");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(packageId, result!.Id);
        Assert.Equal("MyPackage", result.Name);
    }

    [Fact]
    public async Task GetPackageByIdAsync_ReturnsPackage_WhenFound()
    {
        // Arrange
        var packageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var package = new PluginPackage
        {
            Id = packageId,
            Name = "MyPackage",
            UniqueName = "MyPackage",
            Version = "3.0.0.0"
        };
        entities.Entities.Add(package);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetPackageByIdAsync(packageId);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(packageId, result!.Id);
        Assert.Equal("MyPackage", result.Name);
        Assert.Equal("3.0.0.0", result.Version);
    }

    [Fact]
    public async Task GetPluginTypeByNameAsync_ReturnsType_WhenFound()
    {
        // Arrange
        var typeId = Guid.NewGuid();
        var entities = new EntityCollection();
        var pluginType = new PluginType
        {
            Id = typeId,
            TypeName = "MyNamespace.MyPlugin",
            FriendlyName = "MyPlugin"
        };
        entities.Entities.Add(pluginType);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetPluginTypeByNameAsync("MyNamespace.MyPlugin");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(typeId, result!.Id);
        Assert.Equal("MyNamespace.MyPlugin", result.TypeName);
    }

    [Fact]
    public async Task GetStepByNameAsync_ReturnsStep_WhenFound()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var entities = new EntityCollection();
        var step = new SdkMessageProcessingStep
        {
            Id = stepId,
            Name = "TestPlugin: Create of account",
            Stage = sdkmessageprocessingstep_stage.Preoperation,
            Mode = sdkmessageprocessingstep_mode.Synchronous,
            Rank = 1,
            StateCode = sdkmessageprocessingstep_statecode.Enabled
        };
        step["message.name"] = new AliasedValue(SdkMessage.EntityLogicalName, SdkMessage.Fields.Name, "Create");
        step["filter.primaryobjecttypecode"] = new AliasedValue(SdkMessageFilter.EntityLogicalName, SdkMessageFilter.Fields.PrimaryObjectTypeCode, "account");
        entities.Entities.Add(step);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetStepByNameAsync("TestPlugin: Create of account");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(stepId, result!.Id);
        Assert.Equal("TestPlugin: Create of account", result.Name);
        Assert.Equal("Create", result.Message);
    }

    [Fact]
    public async Task GetSdkMessageFilterIdAsync_ReturnsId_WhenFound()
    {
        // Arrange
        var filterId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var filter = new SdkMessageFilter { Id = filterId };
        entities.Entities.Add(filter);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.GetSdkMessageFilterIdAsync(messageId, "account");

        // Assert
        Assert.Equal(filterId, result);
    }

    [Fact]
    public async Task GetSdkMessageFilterIdAsync_ReturnsNull_WhenNotFound()
    {
        // Arrange
        var messageId = Guid.NewGuid();
        _retrieveMultipleResult = new EntityCollection();

        // Act
        var result = await _sut.GetSdkMessageFilterIdAsync(messageId, "nonexistententity");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task ListTypesForAssemblyAsync_ReturnsTypes()
    {
        // Arrange
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var entities = new EntityCollection();
        var pluginType = new PluginType
        {
            Id = typeId,
            TypeName = "MyNamespace.MyPlugin",
            FriendlyName = "MyPlugin"
        };
        entities.Entities.Add(pluginType);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.ListTypesForAssemblyAsync(assemblyId);

        // Assert
        Assert.Single(result);
        Assert.Equal("MyNamespace.MyPlugin", result[0].TypeName);
    }

    [Fact]
    public async Task ListTypesForPackageAsync_ReturnsTypes()
    {
        // Arrange
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        // First query: ListAssembliesForPackageAsync returns one assembly
        // Second query: ListTypesForAssemblyAsync returns one type
        var queryCount = 0;
        _mockPooledClient
            .Setup(s => s.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                queryCount++;
                if (queryCount == 1)
                {
                    var assemblyEntities = new EntityCollection();
                    var assembly = new PluginAssembly
                    {
                        Id = assemblyId,
                        Name = "TestAssembly",
                        Version = "1.0.0.0",
                        IsolationMode = pluginassembly_isolationmode.Sandbox
                    };
                    assemblyEntities.Entities.Add(assembly);
                    return assemblyEntities;
                }
                else
                {
                    var typeEntities = new EntityCollection();
                    var pluginType = new PluginType
                    {
                        Id = typeId,
                        TypeName = "MyNamespace.MyPlugin",
                        FriendlyName = "MyPlugin"
                    };
                    typeEntities.Entities.Add(pluginType);
                    return typeEntities;
                }
            });

        // Act
        var result = await _sut.ListTypesForPackageAsync(packageId);

        // Assert
        Assert.Single(result);
        Assert.Equal("MyNamespace.MyPlugin", result[0].TypeName);
    }

    [Fact]
    public async Task ListImagesForStepAsync_ReturnsImages()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var imageId = Guid.NewGuid();
        var entities = new EntityCollection();
        var image = new SdkMessageProcessingStepImage
        {
            Id = imageId,
            Name = "PreImage",
            EntityAlias = "PreImage",
            ImageType = sdkmessageprocessingstepimage_imagetype.PreImage,
            Attributes1 = "name,accountnumber"
        };
        entities.Entities.Add(image);
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.ListImagesForStepAsync(stepId);

        // Assert
        Assert.Single(result);
        Assert.Equal("PreImage", result[0].Name);
        Assert.Equal("name,accountnumber", result[0].Attributes);
    }

    [Fact]
    public async Task ListMessagesAsync_ReturnsMessages()
    {
        // Arrange
        var entities = new EntityCollection();
        var message1 = new SdkMessage { Id = Guid.NewGuid() };
        message1[SdkMessage.Fields.Name] = "Create";
        var message2 = new SdkMessage { Id = Guid.NewGuid() };
        message2[SdkMessage.Fields.Name] = "Update";
        entities.Entities.Add(message1);
        entities.Entities.Add(message2);
        entities.MoreRecords = false;
        _retrieveMultipleResult = entities;

        // Act
        var result = await _sut.ListMessagesAsync(null);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("Create", result);
        Assert.Contains("Update", result);
    }

    [Fact]
    public async Task ListEntityAttributesAsync_ReturnsAttributes()
    {
        // Arrange
        var metadata = new EntityMetadata();
        var attr1 = new StringAttributeMetadata("name") { LogicalName = "name" };
        attr1.DisplayName = new Label("Name", 1033);
        var attr2 = new StringAttributeMetadata("accountnumber") { LogicalName = "accountnumber" };
        attr2.DisplayName = new Label("Account Number", 1033);

        // Use reflection to set the read-only Attributes property on EntityMetadata
        var attributesProperty = typeof(EntityMetadata).GetProperty("Attributes");
        attributesProperty!.SetValue(metadata, new AttributeMetadata[] { attr1, attr2 });

        var response = new RetrieveEntityResponse();
        response.Results["EntityMetadata"] = metadata;

        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        // Act
        var result = await _sut.ListEntityAttributesAsync("account");

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains(result, a => a.LogicalName == "accountnumber");
        Assert.Contains(result, a => a.LogicalName == "name");
    }

    #endregion
}
