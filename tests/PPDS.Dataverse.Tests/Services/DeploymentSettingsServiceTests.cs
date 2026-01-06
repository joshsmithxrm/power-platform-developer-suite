using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Services;
using Xunit;

namespace PPDS.Dataverse.Tests.Services;

public class DeploymentSettingsServiceTests
{
    [Fact]
    public void Constructor_ThrowsOnNullEnvVarService()
    {
        // Arrange
        var connectionRefService = new Mock<IConnectionReferenceService>().Object;
        var logger = new NullLogger<DeploymentSettingsService>();

        // Act
        var act = () => new DeploymentSettingsService(null!, connectionRefService, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("envVarService");
    }

    [Fact]
    public void Constructor_ThrowsOnNullConnectionRefService()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>().Object;
        var logger = new NullLogger<DeploymentSettingsService>();

        // Act
        var act = () => new DeploymentSettingsService(envVarService, null!, logger);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("connectionRefService");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>().Object;
        var connectionRefService = new Mock<IConnectionReferenceService>().Object;

        // Act
        var act = () => new DeploymentSettingsService(envVarService, connectionRefService, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .And.ParamName.Should().Be("logger");
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>().Object;
        var connectionRefService = new Mock<IConnectionReferenceService>().Object;
        var logger = new NullLogger<DeploymentSettingsService>();

        // Act
        var service = new DeploymentSettingsService(envVarService, connectionRefService, logger);

        // Assert
        service.Should().NotBeNull();
    }

    [Fact]
    public void AddDataverseConnectionPool_RegistersIDeploymentSettingsService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Primary")
            {
                Url = "https://test.crm.dynamics.com",
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AuthType = DataverseAuthType.ClientSecret
            });
        });

        // Act
        var provider = services.BuildServiceProvider();
        var dsService = provider.GetService<IDeploymentSettingsService>();

        // Assert
        dsService.Should().NotBeNull();
        dsService.Should().BeOfType<DeploymentSettingsService>();
    }

    [Fact]
    public void AddDataverseConnectionPool_DeploymentSettingsServiceIsTransient()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection("Primary")
            {
                Url = "https://test.crm.dynamics.com",
                ClientId = "test-client-id",
                ClientSecret = "test-secret",
                AuthType = DataverseAuthType.ClientSecret
            });
        });

        // Act
        var provider = services.BuildServiceProvider();
        var service1 = provider.GetService<IDeploymentSettingsService>();
        var service2 = provider.GetService<IDeploymentSettingsService>();

        // Assert
        service1.Should().NotBeSameAs(service2);
    }

    [Fact]
    public async Task GenerateAsync_ReturnsSettingsWithEnvVarsAndConnectionRefs()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    SchemaName = "cr_TestVar1",
                    DisplayName = "Test Variable 1",
                    Type = "String",
                    CurrentValue = "value1"
                },
                new()
                {
                    Id = Guid.NewGuid(),
                    SchemaName = "cr_TestVar2",
                    DisplayName = "Test Variable 2",
                    Type = "String",
                    DefaultValue = "default2"
                }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    LogicalName = "cr_dataverse",
                    ConnectionId = "conn-123",
                    ConnectorId = "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"
                }
            });

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.GenerateAsync("TestSolution");

        // Assert
        result.EnvironmentVariables.Should().HaveCount(2);
        result.EnvironmentVariables[0].SchemaName.Should().Be("cr_TestVar1");
        result.EnvironmentVariables[0].Value.Should().Be("value1");
        result.EnvironmentVariables[1].SchemaName.Should().Be("cr_TestVar2");
        result.EnvironmentVariables[1].Value.Should().Be("default2");

        result.ConnectionReferences.Should().HaveCount(1);
        result.ConnectionReferences[0].LogicalName.Should().Be("cr_dataverse");
        result.ConnectionReferences[0].ConnectionId.Should().Be("conn-123");
    }

    [Fact]
    public async Task GenerateAsync_ExcludesSecretEnvironmentVariables()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new() { Id = Guid.NewGuid(), SchemaName = "cr_NormalVar", Type = "String", CurrentValue = "normal" },
                new() { Id = Guid.NewGuid(), SchemaName = "cr_SecretVar", Type = "Secret", CurrentValue = "secret-value" }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>());

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.GenerateAsync("TestSolution");

        // Assert
        result.EnvironmentVariables.Should().HaveCount(1);
        result.EnvironmentVariables[0].SchemaName.Should().Be("cr_NormalVar");
    }

    [Fact]
    public async Task GenerateAsync_SortsEntriesByName()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new() { Id = Guid.NewGuid(), SchemaName = "cr_Zebra", Type = "String" },
                new() { Id = Guid.NewGuid(), SchemaName = "cr_Apple", Type = "String" },
                new() { Id = Guid.NewGuid(), SchemaName = "cr_Mango", Type = "String" }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>
            {
                new() { Id = Guid.NewGuid(), LogicalName = "cr_zebra_conn" },
                new() { Id = Guid.NewGuid(), LogicalName = "cr_apple_conn" }
            });

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.GenerateAsync("TestSolution");

        // Assert
        result.EnvironmentVariables.Select(ev => ev.SchemaName)
            .Should().BeEquivalentTo(new[] { "cr_Apple", "cr_Mango", "cr_Zebra" },
                options => options.WithStrictOrdering());

        result.ConnectionReferences.Select(cr => cr.LogicalName)
            .Should().BeEquivalentTo(new[] { "cr_apple_conn", "cr_zebra_conn" },
                options => options.WithStrictOrdering());
    }

    [Fact]
    public async Task SyncAsync_PreservesExistingValues()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new() { Id = Guid.NewGuid(), SchemaName = "cr_Var1", Type = "String", CurrentValue = "new-value" }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>
            {
                new() { Id = Guid.NewGuid(), LogicalName = "cr_conn1", ConnectionId = "new-conn-id" }
            });

        var existingSettings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>
            {
                new() { SchemaName = "cr_Var1", Value = "preserved-value" }
            },
            ConnectionReferences = new List<ConnectionReferenceEntry>
            {
                new() { LogicalName = "cr_conn1", ConnectionId = "preserved-conn-id", ConnectorId = "preserved-connector" }
            }
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.SyncAsync("TestSolution", existingSettings);

        // Assert
        result.Settings.EnvironmentVariables[0].Value.Should().Be("preserved-value");
        result.Settings.ConnectionReferences[0].ConnectionId.Should().Be("preserved-conn-id");
        result.EnvironmentVariables.Preserved.Should().Be(1);
        result.ConnectionReferences.Preserved.Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_AddsNewEntries()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new() { Id = Guid.NewGuid(), SchemaName = "cr_NewVar", Type = "String", CurrentValue = "new-value" }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>
            {
                new() { Id = Guid.NewGuid(), LogicalName = "cr_new_conn", ConnectionId = "new-conn-id", ConnectorId = "new-connector" }
            });

        var existingSettings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>(),
            ConnectionReferences = new List<ConnectionReferenceEntry>()
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.SyncAsync("TestSolution", existingSettings);

        // Assert
        result.Settings.EnvironmentVariables.Should().HaveCount(1);
        result.Settings.EnvironmentVariables[0].Value.Should().Be("new-value");
        result.EnvironmentVariables.Added.Should().Be(1);
        result.ConnectionReferences.Added.Should().Be(1);
    }

    [Fact]
    public async Task SyncAsync_ReportsRemovedEntries()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>());

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>());

        var existingSettings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>
            {
                new() { SchemaName = "cr_OldVar", Value = "old-value" }
            },
            ConnectionReferences = new List<ConnectionReferenceEntry>
            {
                new() { LogicalName = "cr_old_conn", ConnectionId = "old-id" }
            }
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.SyncAsync("TestSolution", existingSettings);

        // Assert
        result.Settings.EnvironmentVariables.Should().BeEmpty();
        result.Settings.ConnectionReferences.Should().BeEmpty();
        result.EnvironmentVariables.Removed.Should().Be(1);
        result.ConnectionReferences.Removed.Should().Be(1);
    }

    [Fact]
    public async Task ValidateAsync_DetectsStaleEntries()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>());

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>());

        var settings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>
            {
                new() { SchemaName = "cr_StaleVar", Value = "value" }
            },
            ConnectionReferences = new List<ConnectionReferenceEntry>
            {
                new() { LogicalName = "cr_stale_conn", ConnectionId = "id" }
            }
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.ValidateAsync("TestSolution", settings);

        // Assert
        result.Issues.Should().Contain(i => i.Name == "cr_StaleVar" && i.Severity == ValidationSeverity.Warning);
        result.Issues.Should().Contain(i => i.Name == "cr_stale_conn" && i.Severity == ValidationSeverity.Warning);
    }

    [Fact]
    public async Task ValidateAsync_DetectsMissingRequiredEnvVars()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new() { Id = Guid.NewGuid(), SchemaName = "cr_RequiredVar", Type = "String", IsRequired = true }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>());

        var settings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>(),
            ConnectionReferences = new List<ConnectionReferenceEntry>()
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.ValidateAsync("TestSolution", settings);

        // Assert
        result.Issues.Should().Contain(i =>
            i.Name == "cr_RequiredVar" &&
            i.Severity == ValidationSeverity.Error &&
            i.Message.Contains("missing"));
    }

    [Fact]
    public async Task ValidateAsync_DetectsEmptyRequiredEnvVarValues()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>
            {
                new() { Id = Guid.NewGuid(), SchemaName = "cr_RequiredVar", Type = "String", IsRequired = true }
            });

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>());

        var settings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>
            {
                new() { SchemaName = "cr_RequiredVar", Value = "" }
            },
            ConnectionReferences = new List<ConnectionReferenceEntry>()
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.ValidateAsync("TestSolution", settings);

        // Assert
        result.Issues.Should().Contain(i =>
            i.Name == "cr_RequiredVar" &&
            i.Severity == ValidationSeverity.Error &&
            i.Message.Contains("empty"));
    }

    [Fact]
    public async Task ValidateAsync_WarnsOnMissingConnectionId()
    {
        // Arrange
        var envVarService = new Mock<IEnvironmentVariableService>();
        envVarService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EnvironmentVariableInfo>());

        var connectionRefService = new Mock<IConnectionReferenceService>();
        connectionRefService.Setup(s => s.ListAsync(It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConnectionReferenceInfo>
            {
                new() { Id = Guid.NewGuid(), LogicalName = "cr_unbound", ConnectorId = "connector-id" }
            });

        var settings = new DeploymentSettingsFile
        {
            EnvironmentVariables = new List<EnvironmentVariableEntry>(),
            ConnectionReferences = new List<ConnectionReferenceEntry>
            {
                new() { LogicalName = "cr_unbound", ConnectionId = "", ConnectorId = "connector-id" }
            }
        };

        var logger = new NullLogger<DeploymentSettingsService>();
        var service = new DeploymentSettingsService(envVarService.Object, connectionRefService.Object, logger);

        // Act
        var result = await service.ValidateAsync("TestSolution", settings);

        // Assert
        result.Issues.Should().Contain(i =>
            i.Name == "cr_unbound" &&
            i.Severity == ValidationSeverity.Warning &&
            i.Message.Contains("ConnectionId"));
    }
}
