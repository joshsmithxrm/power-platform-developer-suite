using System.ServiceModel;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk;
using Moq;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Generated;
using Xunit;

namespace PPDS.Cli.Tests.Plugins.Registration;

public class PluginRegistrationServiceTests
{
    private readonly Mock<IOrganizationService> _mockService;
    private readonly Mock<ILogger<PluginRegistrationService>> _mockLogger;
    private readonly PluginRegistrationService _sut;

    public PluginRegistrationServiceTests()
    {
        _mockService = new Mock<IOrganizationService>();
        _mockLogger = new Mock<ILogger<PluginRegistrationService>>();
        _sut = new PluginRegistrationService(_mockService.Object, _mockLogger.Object);
    }

    [Fact]
    public async Task ListAssembliesAsync_ReturnsEmptyList_WhenNoAssembliesExist()
    {
        // Arrange
        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(new EntityCollection());

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

        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(entities);

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
        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(new EntityCollection());
        _mockService
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(expectedId);

        // Act
        var result = await _sut.UpsertAssemblyAsync("TestAssembly", new byte[] { 1, 2, 3 });

        // Assert
        Assert.Equal(expectedId, result);
        _mockService.Verify(s => s.Create(It.Is<Entity>(e => e.LogicalName == PluginAssembly.EntityLogicalName)), Times.Once);
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

        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(entities);

        // Act
        var result = await _sut.UpsertAssemblyAsync("TestAssembly", new byte[] { 1, 2, 3 });

        // Assert
        Assert.Equal(existingId, result);
        _mockService.Verify(s => s.Update(It.Is<Entity>(e => e.Id == existingId)), Times.Once);
        _mockService.Verify(s => s.Create(It.IsAny<Entity>()), Times.Never);
    }

    [Fact]
    public async Task GetSdkMessageIdAsync_ReturnsNull_WhenMessageNotFound()
    {
        // Arrange
        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(new EntityCollection());

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

        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(entities);

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
        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(new EntityCollection());

        // Create succeeds
        _mockService
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(expectedId);

        // RetrieveEntityRequest throws FaultException (entity metadata not found)
        _mockService
            .Setup(s => s.Execute(It.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity")))
            .Throws(new FaultException("Entity does not exist"));

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
        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(new EntityCollection());

        // Create succeeds
        _mockService
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(expectedId);

        // RetrieveEntityRequest throws FaultException<OrganizationServiceFault>
        var fault = new OrganizationServiceFault { Message = "Entity not found", ErrorCode = -2147220969 };
        _mockService
            .Setup(s => s.Execute(It.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity")))
            .Throws(new FaultException<OrganizationServiceFault>(fault, new FaultReason("Entity not found")));

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
        _mockService
            .Setup(s => s.RetrieveMultiple(It.IsAny<Microsoft.Xrm.Sdk.Query.QueryExpression>()))
            .Returns(new EntityCollection());

        // Create succeeds
        _mockService
            .Setup(s => s.Create(It.IsAny<Entity>()))
            .Returns(expectedId);

        // RetrieveEntityRequest throws (so GetComponentTypeAsync returns 0)
        _mockService
            .Setup(s => s.Execute(It.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity")))
            .Throws(new FaultException("Entity does not exist"));

        // Track if AddSolutionComponent is called
        _mockService
            .Setup(s => s.Execute(It.Is<OrganizationRequest>(r => r.RequestName == "AddSolutionComponent")))
            .Callback(() => addSolutionComponentCalled = true)
            .Returns(new OrganizationResponse());

        // Act
        var result = await _sut.UpsertPluginTypeAsync(assemblyId, "MyPlugin.Plugin", "TestSolution");

        // Assert - Plugin type was created, solution addition was skipped
        Assert.Equal(expectedId, result);
        Assert.False(addSolutionComponentCalled, "AddSolutionComponent should not be called when componentType is 0");
    }

    #endregion
}
