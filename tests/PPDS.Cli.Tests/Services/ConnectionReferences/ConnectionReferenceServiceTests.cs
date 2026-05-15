using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Moq;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.ConnectionReferences;
using PPDS.Cli.Services.Flows;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.ConnectionReferences;

public class ConnectionReferenceServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var flowService = new Mock<IFlowService>().Object;
        var logger = new NullLogger<ConnectionReferenceService>();

        // Act
        var act = () => new ConnectionReferenceService(null!, flowService, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullFlowService()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<ConnectionReferenceService>();

        // Act
        var act = () => new ConnectionReferenceService(pool, null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("flowService");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var flowService = new Mock<IFlowService>().Object;

        // Act
        var act = () => new ConnectionReferenceService(pool, flowService, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var flowService = new Mock<IFlowService>().Object;
        var logger = new NullLogger<ConnectionReferenceService>();

        // Act
        var service = new ConnectionReferenceService(pool, flowService, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddCliApplicationServices_RegistersIConnectionReferenceService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var crService = provider.GetService<IConnectionReferenceService>();

        // Assert
        crService.Should().NotBeNull();
        crService.Should().BeOfType<ConnectionReferenceService>();
    }

    [Fact]
    public void AddCliApplicationServices_ConnectionReferenceServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<IConnectionReferenceService>();
        var service2 = provider.GetService<IConnectionReferenceService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }

    // ── BindAsync (issue #592) ───────────────────────────────────────────

    private static (ConnectionReferenceService Service, Mock<IPooledClient> Client) CreateServiceWithPool()
    {
        var pool = new Mock<IDataverseConnectionPool>();
        var client = new Mock<IPooledClient>(MockBehavior.Loose);
        pool.Setup(p => p.GetClientAsync(null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(client.Object);
        var flowService = new Mock<IFlowService>().Object;
        var service = new ConnectionReferenceService(pool.Object, flowService, NullLogger<ConnectionReferenceService>.Instance);
        return (service, client);
    }

    private static Entity MakeConnRefEntity(Guid id, string logicalName, string? connectionId, string connectorId = "shared_test")
    {
        var entity = new Entity("connectionreference", id);
        entity["connectionreferencelogicalname"] = logicalName;
        entity["connectionreferencedisplayname"] = logicalName;
        entity["connectionid"] = connectionId;
        entity["connectorid"] = connectorId;
        entity["statecode"] = new OptionSetValue(0);
        return entity;
    }

    [Fact]
    public async Task BindAsync_ThrowsValidationException_WhenLogicalNameMissing()
    {
        var (service, _) = CreateServiceWithPool();

        var act = async () => await service.BindAsync("", "conn-123");

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Validation.RequiredField);
    }

    [Fact]
    public async Task BindAsync_ThrowsNotFound_WhenReferenceMissing()
    {
        var (service, client) = CreateServiceWithPool();
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var act = async () => await service.BindAsync("ghost_cr", "conn-123");

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Operation.NotFound);
        // Negative path: UpdateAsync MUST NOT have been called when the CR doesn't exist.
        client.Verify(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BindAsync_WritesConnectionIdToDataverse_AndReturnsUpdatedInfo()
    {
        var (service, client) = CreateServiceWithPool();
        var crId = Guid.NewGuid();

        // GetAsync (pre-update logical-name lookup) returns existing CR with no binding;
        // GetByIdAsync (post-update re-read by Guid) returns CR with the new connectionid.
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { MakeConnRefEntity(crId, "myapp_cr", connectionId: null) }));
        client.Setup(c => c.RetrieveAsync("connectionreference", crId, It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeConnRefEntity(crId, "myapp_cr", connectionId: "new-conn-id"));

        Entity? capturedUpdate = null;
        client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedUpdate = e)
            .Returns(Task.CompletedTask);

        var result = await service.BindAsync("myapp_cr", "new-conn-id");

        result.Should().NotBeNull();
        result.LogicalName.Should().Be("myapp_cr");
        result.ConnectionId.Should().Be("new-conn-id");
        result.IsBound.Should().BeTrue();

        capturedUpdate.Should().NotBeNull();
        capturedUpdate!.LogicalName.Should().Be("connectionreference");
        capturedUpdate.Id.Should().Be(crId);
        capturedUpdate["connectionid"].Should().Be("new-conn-id");
        client.Verify(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task BindAsync_ClearsBinding_WhenConnectionIdIsNullOrWhitespace()
    {
        var (service, client) = CreateServiceWithPool();
        var crId = Guid.NewGuid();

        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { MakeConnRefEntity(crId, "myapp_cr", connectionId: "old-conn") }));
        client.Setup(c => c.RetrieveAsync("connectionreference", crId, It.IsAny<ColumnSet>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeConnRefEntity(crId, "myapp_cr", connectionId: null));

        Entity? capturedUpdate = null;
        client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((e, _) => capturedUpdate = e)
            .Returns(Task.CompletedTask);

        var result = await service.BindAsync("myapp_cr", "   ");

        result.IsBound.Should().BeFalse();
        capturedUpdate.Should().NotBeNull();
        capturedUpdate!["connectionid"].Should().BeNull();
    }

    [Fact]
    public async Task BindAsync_WrapsDataverseFailure_InPpdsException()
    {
        var (service, client) = CreateServiceWithPool();
        var crId = Guid.NewGuid();

        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { MakeConnRefEntity(crId, "myapp_cr", connectionId: null) }));

        client.Setup(c => c.UpdateAsync(It.IsAny<Entity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("dataverse exploded"));

        var act = async () => await service.BindAsync("myapp_cr", "conn-123");

        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.External.ServiceUnavailable);
        ex.Which.InnerException.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task GetAsync_QueryDoesNotFilterByStateCode_SoInactiveRefsResolve()
    {
        // The "All" toggle in the panel surfaces inactive (statecode=1) refs.
        // Bind must resolve their Id via GetAsync, so logical-name lookup
        // must NOT add a statecode filter.
        var (service, client) = CreateServiceWithPool();
        QueryBase? capturedQuery = null;
        client.Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => capturedQuery = q)
            .ReturnsAsync(new EntityCollection(new List<Entity> { MakeConnRefEntity(Guid.NewGuid(), "myapp_cr", connectionId: null) }));

        var result = await service.GetAsync("myapp_cr");

        result.Should().NotBeNull();
        var query = capturedQuery.Should().BeOfType<QueryExpression>().Subject;
        query.Criteria.Conditions
            .Should().NotContain(c => c.AttributeName == "statecode",
                "GetAsync is keyed by unique logical name; state filtering is a list-level concern.");
    }
}
