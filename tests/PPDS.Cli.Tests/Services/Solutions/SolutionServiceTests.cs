using System.Collections.Generic;
using System.Reflection;
using System.ServiceModel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.SolutionComponents;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Tests.Services.Shared;
using PPDS.Dataverse.Client;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using Xunit;

namespace PPDS.Cli.Tests.Services.Solutions;

public class SolutionServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullConnectionPool()
    {
        // Arrange
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<SolutionService>();
        var metadataService = new Mock<IMetadataQueryService>().Object;
        var nameResolver = new Mock<IComponentNameResolver>().Object;
        var cachedMetadata = new Mock<ICachedMetadataProvider>().Object;

        // Act
        var act = () => new SolutionService(null!, guard, logger, metadataService, nameResolver, cachedMetadata);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("pool");
    }

    [Fact]
    public void Constructor_ThrowsOnNullGuard()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var logger = new NullLogger<SolutionService>();
        var metadataService = new Mock<IMetadataQueryService>().Object;
        var nameResolver = new Mock<IComponentNameResolver>().Object;
        var cachedMetadata = new Mock<ICachedMetadataProvider>().Object;

        // Act
        var act = () => new SolutionService(pool, null!, logger, metadataService, nameResolver, cachedMetadata);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("guard");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var metadataService = new Mock<IMetadataQueryService>().Object;
        var nameResolver = new Mock<IComponentNameResolver>().Object;
        var cachedMetadata = new Mock<ICachedMetadataProvider>().Object;

        // Act
        var act = () => new SolutionService(pool, guard, null!, metadataService, nameResolver, cachedMetadata);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_ThrowsOnNullMetadataService()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<SolutionService>();
        var nameResolver = new Mock<IComponentNameResolver>().Object;
        var cachedMetadata = new Mock<ICachedMetadataProvider>().Object;

        // Act
        var act = () => new SolutionService(pool, guard, logger, null!, nameResolver, cachedMetadata);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("metadataService");
    }

    [Fact]
    public void Constructor_ThrowsOnNullCachedMetadata()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<SolutionService>();
        var metadataService = new Mock<IMetadataQueryService>().Object;
        var nameResolver = new Mock<IComponentNameResolver>().Object;

        // Act
        var act = () => new SolutionService(pool, guard, logger, metadataService, nameResolver, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("cachedMetadata");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var pool = new Mock<IDataverseConnectionPool>().Object;
        var guard = new InactiveFakeShakedownGuard();
        var logger = new NullLogger<SolutionService>();
        var metadataService = new Mock<IMetadataQueryService>().Object;
        var nameResolver = new Mock<IComponentNameResolver>().Object;
        var cachedMetadata = new Mock<ICachedMetadataProvider>().Object;

        // Act
        var service = new SolutionService(pool, guard, logger, metadataService, nameResolver, cachedMetadata);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddCliApplicationServices_RegistersISolutionService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var solutionService = provider.GetService<ISolutionService>();

        // Assert
        solutionService.Should().NotBeNull();
        solutionService.Should().BeOfType<SolutionService>();
    }

    [Fact]
    public void AddCliApplicationServices_SolutionServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterDataverseServices();
        services.AddSingleton<IDataverseConnectionPool>(new Mock<IDataverseConnectionPool>().Object);
        services.AddCliApplicationServices();
        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<ISolutionService>();
        var service2 = provider.GetService<ISolutionService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }

    [Theory]
    [InlineData(1, "Entity")]
    [InlineData(65, "HierarchyRule")]
    [InlineData(66, "CustomControl")]
    [InlineData(68, "CustomControlDefaultConfig")]
    [InlineData(70, "FieldSecurityProfile")]
    [InlineData(71, "FieldPermission")]
    [InlineData(90, "PluginType")]
    [InlineData(91, "PluginAssembly")]
    [InlineData(92, "SDKMessageProcessingStep")]
    [InlineData(93, "SDKMessageProcessingStepImage")]
    [InlineData(95, "ServiceEndpoint")]
    [InlineData(150, "RoutingRule")]
    [InlineData(151, "RoutingRuleItem")]
    [InlineData(152, "SLA")]
    [InlineData(161, "MobileOfflineProfile")]
    [InlineData(208, "ImportMap")]
    [InlineData(300, "CanvasApp")]
    [InlineData(372, "Connector")]
    public void ComponentTypeNames_MatchesGeneratedEnum(int typeCode, string expectedName)
    {
        var dictField = typeof(SolutionService).GetField(
            "ComponentTypeNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        dictField.Should().NotBeNull("ComponentTypeNames dictionary should exist");

        var dict = dictField!.GetValue(null) as Dictionary<int, string>;
        dict.Should().NotBeNull();
        dict.Should().ContainKey(typeCode);
        dict![typeCode].Should().Be(expectedName);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComponentTypeNames_CoversAllGeneratedEnumValues()
    {
        var dictField = typeof(SolutionService).GetField(
            "ComponentTypeNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        dictField.Should().NotBeNull();

        var dict = dictField!.GetValue(null) as Dictionary<int, string>
            ?? throw new InvalidOperationException("ComponentTypeNames must be Dictionary<int, string>");

        foreach (var value in Enum.GetValues<PPDS.Dataverse.Generated.componenttype>())
        {
            dict.Should().ContainKey((int)value,
                $"ComponentTypeNames must contain generated enum value {value} ({(int)value})");
            dict[(int)value].Should().NotBeNullOrWhiteSpace(
                $"ComponentTypeNames[{(int)value}] must have a non-empty label");
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComponentTypeNames_IncludesModelDrivenApp()
    {
        var dictField = typeof(SolutionService).GetField(
            "ComponentTypeNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        var dict = dictField!.GetValue(null) as Dictionary<int, string>
            ?? throw new InvalidOperationException("ComponentTypeNames must be Dictionary<int, string>");

        dict.Should().ContainKey(80);
        dict[80].Should().Be("Model-Driven App");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ComponentTypeNames_NoDuplicateEnumNameCollisions()
    {
        var dictField = typeof(SolutionService).GetField(
            "ComponentTypeNames",
            BindingFlags.NonPublic | BindingFlags.Static);
        var dict = dictField!.GetValue(null) as Dictionary<int, string>
            ?? throw new InvalidOperationException("ComponentTypeNames must be Dictionary<int, string>");

        // Connector1 (372) should display as "Connector", not the auto-generated "Connector1"
        dict.Should().ContainKey(372);
        dict[372].Should().Be("Connector");
    }

    // ─── H5: D4 fault-wrapping tests ────────────────────────────────────────────

    private static SolutionService CreateService(IDataverseConnectionPool pool)
    {
        return new SolutionService(
            pool,
            new InactiveFakeShakedownGuard(),
            new NullLogger<SolutionService>(),
            new Mock<IMetadataQueryService>().Object,
            new Mock<IComponentNameResolver>().Object,
            new Mock<ICachedMetadataProvider>().Object);
    }

    private static Mock<IDataverseConnectionPool> CreateFaultingPool(Exception fault)
    {
        var mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(fault);
        mockClient
            .Setup(c => c.ExecuteAsync(It.IsAny<OrganizationRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(fault);

        var mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        mockPool
            .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);
        return mockPool;
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WrapsFaultExceptionInPpdsException()
    {
        // Arrange
        var innerFault = new FaultException("Dataverse unavailable");
        var pool = CreateFaultingPool(innerFault);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.ListAsync();

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Solution.ListFailed);
        ex.Which.InnerException.Should().BeSameAs(innerFault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WrapsGenericExceptionInPpdsException()
    {
        // Arrange
        var innerException = new InvalidOperationException("network error");
        var pool = CreateFaultingPool(innerException);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.ListAsync();

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Solution.ListFailed);
        ex.Which.InnerException.Should().BeSameAs(innerException);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task GetAsync_WrapsFaultExceptionInPpdsException()
    {
        // Arrange
        var innerFault = new FaultException("Dataverse unavailable");
        var pool = CreateFaultingPool(innerFault);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.GetAsync("nonexistent");

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Solution.GetFailed);
        ex.Which.InnerException.Should().BeSameAs(innerFault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExportAsync_WrapsFaultExceptionInPpdsException()
    {
        // Arrange
        var innerFault = new FaultException("Export service unavailable");
        var pool = CreateFaultingPool(innerFault);
        var service = CreateService(pool.Object);

        // Act
        var act = () => service.ExportAsync("MySolution");

        // Assert
        var ex = await act.Should().ThrowAsync<PpdsException>();
        ex.Which.ErrorCode.Should().Be(ErrorCodes.Solution.ExportFailed);
        ex.Which.InnerException.Should().BeSameAs(innerFault);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_DoesNotWrapOperationCanceledException()
    {
        // Arrange — OperationCanceledException must propagate unwrapped
        var mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        mockPool
            .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var service = CreateService(mockPool.Object);

        // Act
        var act = () => service.ListAsync();

        // Assert — OperationCanceledException propagates, not PpdsException
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── H1: filter operator tests (Like + %value% pattern) ────────────────────

    private static (Mock<IDataverseConnectionPool> pool, List<QueryBase> capturedQueries) CreateCapturingPool()
    {
        var capturedQueries = new List<QueryBase>();

        var mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .Callback<QueryBase, CancellationToken>((q, _) => capturedQueries.Add(q))
            .ReturnsAsync(new EntityCollection());

        var mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        mockPool
            .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        return (mockPool, capturedQueries);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WithFilter_UsesLikeOperatorNotContains()
    {
        // Arrange
        var (pool, capturedQueries) = CreateCapturingPool();
        var service = CreateService(pool.Object);

        // Act
        await service.ListAsync(filter: "foo");

        // Assert — query must use Like, never Contains
        capturedQueries.Should().ContainSingle();
        var query = (QueryExpression)capturedQueries[0];

        var filterGroup = query.Criteria.Filters
            .FirstOrDefault(f => f.FilterOperator == LogicalOperator.Or);
        filterGroup.Should().NotBeNull("filter group should exist when filter is provided");

        var conditions = filterGroup!.Conditions;
        conditions.Should().HaveCount(2, "filter applies to UniqueName and FriendlyName");
        conditions.Should().AllSatisfy(c =>
            c.Operator.Should().Be(ConditionOperator.Like,
                "filter must use Like not Contains to avoid fulltext-index fault 0x80048415"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WithFilter_AppliesWildcardPattern()
    {
        // Arrange
        var (pool, capturedQueries) = CreateCapturingPool();
        var service = CreateService(pool.Object);

        // Act
        await service.ListAsync(filter: "foo");

        // Assert — each condition value must be %foo%
        capturedQueries.Should().ContainSingle();
        var query = (QueryExpression)capturedQueries[0];

        var filterGroup = query.Criteria.Filters
            .First(f => f.FilterOperator == LogicalOperator.Or);

        filterGroup.Conditions.Should().AllSatisfy(c =>
            c.Values.Should().ContainSingle(v =>
                v.ToString() == "%foo%",
                "Like pattern must be %filter% for substring match"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WithFilter_ReturnsMatchingSolutions()
    {
        // Arrange — mock returns a solution whose friendly name contains the filter term
        var matchingEntity = new Entity(PPDS.Dataverse.Generated.Solution.EntityLogicalName)
        {
            Id = Guid.NewGuid()
        };
        matchingEntity[PPDS.Dataverse.Generated.Solution.Fields.UniqueName] = "MySolution";
        matchingEntity[PPDS.Dataverse.Generated.Solution.Fields.FriendlyName] = "My foo Solution";
        matchingEntity[PPDS.Dataverse.Generated.Solution.Fields.Version] = "1.0.0.0";
        matchingEntity[PPDS.Dataverse.Generated.Solution.Fields.IsManaged] = false;
        matchingEntity[PPDS.Dataverse.Generated.Solution.Fields.IsVisible] = true;
        matchingEntity[PPDS.Dataverse.Generated.Solution.Fields.IsApiManaged] = false;

        var entityCollection = new EntityCollection(new List<Entity> { matchingEntity });

        var mockClient = new Mock<IPooledClient>(MockBehavior.Loose);
        mockClient
            .Setup(c => c.RetrieveMultipleAsync(It.IsAny<QueryBase>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entityCollection);

        var mockPool = new Mock<IDataverseConnectionPool>(MockBehavior.Loose);
        mockPool
            .Setup(p => p.GetClientAsync(It.IsAny<DataverseClientOptions?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mockClient.Object);

        var service = CreateService(mockPool.Object);

        // Act
        var result = await service.ListAsync(filter: "foo");

        // Assert — one match returned with expected friendly name
        result.Items.Should().ContainSingle();
        result.Items[0].FriendlyName.Should().Be("My foo Solution");
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ListAsync_WithoutFilter_DoesNotAddFilterGroup()
    {
        // Arrange
        var (pool, capturedQueries) = CreateCapturingPool();
        var service = CreateService(pool.Object);

        // Act
        await service.ListAsync();

        // Assert — no OR filter group added when no filter provided
        capturedQueries.Should().ContainSingle();
        var query = (QueryExpression)capturedQueries[0];

        query.Criteria.Filters
            .Should().NotContain(f => f.FilterOperator == LogicalOperator.Or,
                "filter group should only be added when a filter is specified");
    }
}
