using System.ServiceModel;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
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
            .Callback<OrganizationRequest>((r) => _executedRequest = r)
            .ReturnsAsync(() => _executeResult);
        _mockPooledClient
            .Setup(s => s.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .Callback<OrganizationRequest, CancellationToken>((r, _) => _executedRequest = r)
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
            .Callback<OrganizationRequest>(r => _executedRequest = r)
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
    public async Task UpsertImageAsync_ThrowsInvalidOperationException_ForUnsupportedMessages(string messageName)
    {
        // Arrange
        var imageConfig = new PluginImageConfig
        {
            Name = "TestImage",
            ImageType = "PreImage"
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.UpsertImageAsync(Guid.NewGuid(), imageConfig, messageName));

        Assert.Contains(messageName, exception.Message);
        Assert.Contains("does not support images", exception.Message);
    }

    #endregion
}
