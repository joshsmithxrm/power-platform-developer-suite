using FluentAssertions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Import;
using Xunit;

namespace PPDS.Migration.Tests.Import;

[Trait("Category", "Unit")]
public class PluginStepManagerTests
{
    private readonly Mock<IDataverseConnectionPool> _connectionPool;
    private readonly Mock<IPooledClient> _pooledClient;
    private readonly PluginStepManager _sut;

    public PluginStepManagerTests()
    {
        _connectionPool = new Mock<IDataverseConnectionPool>();
        _pooledClient = new Mock<IPooledClient>();

        _connectionPool
            .Setup(x => x.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_pooledClient.Object);

        _sut = new PluginStepManager(_connectionPool.Object);
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenConnectionPoolIsNull()
    {
        var act = () => new PluginStepManager(null!);

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("connectionPool");
    }

    #region GetActivePluginStepsAsync

    [Fact]
    public async Task GetActivePluginStepsAsync_ReturnsSteps_ForEntityTypeCodes()
    {
        // Arrange
        var stepId1 = Guid.NewGuid();
        var stepId2 = Guid.NewGuid();
        var response = new EntityCollection(new List<Entity>
        {
            new Entity("sdkmessageprocessingstep") { Id = stepId1 },
            new Entity("sdkmessageprocessingstep") { Id = stepId2 }
        });

        _pooledClient
            .Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync(response);

        // Act
        var result = await _sut.GetActivePluginStepsAsync(new[] { 1, 2 });

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(stepId1);
        result.Should().Contain(stepId2);
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_ReturnsEmpty_WhenNoObjectTypeCodes()
    {
        // Act
        var result = await _sut.GetActivePluginStepsAsync(Array.Empty<int>());

        // Assert
        result.Should().BeEmpty();
        _pooledClient.Verify(
            x => x.RetrieveMultipleAsync(It.IsAny<QueryBase>()),
            Times.Never);
    }

    [Fact]
    public async Task GetActivePluginStepsAsync_ReturnsEmpty_WhenNoStepsFound()
    {
        // Arrange
        _pooledClient
            .Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryBase>()))
            .ReturnsAsync(new EntityCollection());

        // Act
        var result = await _sut.GetActivePluginStepsAsync(new[] { 999 });

        // Assert
        result.Should().BeEmpty();
    }

    #endregion

    #region DisablePluginStepsAsync

    [Fact]
    public async Task DisablePluginStepsAsync_SetsStateToDisabled()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var updatedEntities = new List<Entity>();

        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>()))
            .Callback<Entity>(e => updatedEntities.Add(e))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.DisablePluginStepsAsync(new[] { stepId });

        // Assert
        updatedEntities.Should().HaveCount(1);
        updatedEntities[0].Id.Should().Be(stepId);
        updatedEntities[0].LogicalName.Should().Be("sdkmessageprocessingstep");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(1, "StateCode should be Disabled (1)");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(2, "StatusCode should be Disabled (2)");
    }

    [Fact]
    public async Task DisablePluginStepsAsync_DoesNothing_WhenEmptyList()
    {
        // Act
        await _sut.DisablePluginStepsAsync(Array.Empty<Guid>());

        // Assert
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>()), Times.Never);
    }

    [Fact]
    public async Task DisablePluginStepsAsync_ContinuesOnFailure()
    {
        // Arrange
        var stepId1 = Guid.NewGuid();
        var stepId2 = Guid.NewGuid();

        _pooledClient
            .SetupSequence(x => x.UpdateAsync(It.IsAny<Entity>()))
            .ThrowsAsync(new InvalidOperationException("First step failed"))
            .Returns(Task.CompletedTask);

        // Act - should not throw
        await _sut.DisablePluginStepsAsync(new[] { stepId1, stepId2 });

        // Assert - both steps attempted
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DisablePluginStepsAsync_RespectsCancellationToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stepId = Guid.NewGuid();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.DisablePluginStepsAsync(new[] { stepId }, cts.Token));
    }

    #endregion

    #region EnablePluginStepsAsync

    [Fact]
    public async Task EnablePluginStepsAsync_SetsStateToEnabled()
    {
        // Arrange
        var stepId = Guid.NewGuid();
        var updatedEntities = new List<Entity>();

        _pooledClient
            .Setup(x => x.UpdateAsync(It.IsAny<Entity>()))
            .Callback<Entity>(e => updatedEntities.Add(e))
            .Returns(Task.CompletedTask);

        // Act
        await _sut.EnablePluginStepsAsync(new[] { stepId });

        // Assert
        updatedEntities.Should().HaveCount(1);
        updatedEntities[0].Id.Should().Be(stepId);
        updatedEntities[0].LogicalName.Should().Be("sdkmessageprocessingstep");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(0, "StateCode should be Enabled (0)");
        updatedEntities[0].GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(1, "StatusCode should be Enabled (1)");
    }

    [Fact]
    public async Task EnablePluginStepsAsync_DoesNothing_WhenEmptyList()
    {
        // Act
        await _sut.EnablePluginStepsAsync(Array.Empty<Guid>());

        // Assert
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>()), Times.Never);
    }

    [Fact]
    public async Task EnablePluginStepsAsync_ContinuesOnFailure()
    {
        // Arrange
        var stepId1 = Guid.NewGuid();
        var stepId2 = Guid.NewGuid();

        _pooledClient
            .SetupSequence(x => x.UpdateAsync(It.IsAny<Entity>()))
            .ThrowsAsync(new InvalidOperationException("First step failed"))
            .Returns(Task.CompletedTask);

        // Act - should not throw
        await _sut.EnablePluginStepsAsync(new[] { stepId1, stepId2 });

        // Assert - both steps attempted
        _pooledClient.Verify(x => x.UpdateAsync(It.IsAny<Entity>()), Times.Exactly(2));
    }

    #endregion
}
