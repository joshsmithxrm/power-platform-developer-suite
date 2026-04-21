using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Cli.Infrastructure.Safety;
using PPDS.Cli.Services.SolutionComponents;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Tests.Services.Shared;
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
}
