using System;
using System.Collections.Generic;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using Xunit;

namespace PPDS.Dataverse.Tests.DependencyInjection;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddDataverseConnectionPool_RegistersRequiredServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
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

        // Assert
        var provider = services.BuildServiceProvider();

        provider.GetService<IThrottleTracker>().Should().NotBeNull();
        provider.GetService<IDataverseConnectionPool>().Should().NotBeNull();
        provider.GetService<IBulkOperationExecutor>().Should().NotBeNull();
    }

    [Fact]
    public void AddDataverseConnectionPool_ThrottleTrackerIsSingleton()
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
        var tracker1 = provider.GetService<IThrottleTracker>();
        var tracker2 = provider.GetService<IThrottleTracker>();

        // Assert
        tracker1.Should().BeSameAs(tracker2);
    }

    [Fact]
    public void AddDataverseConnectionPool_ConnectionPoolIsSingleton()
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
        var pool1 = provider.GetService<IDataverseConnectionPool>();
        var pool2 = provider.GetService<IDataverseConnectionPool>();

        // Assert
        pool1.Should().BeSameAs(pool2);
    }

    [Fact]
    public void AddDataverseConnectionPool_BulkExecutorIsTransient()
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
        var executor1 = provider.GetService<IBulkOperationExecutor>();
        var executor2 = provider.GetService<IBulkOperationExecutor>();

        // Assert
        executor1.Should().NotBeSameAs(executor2);
    }

    [Fact]
    public void AddDataverseConnectionPool_ThrowsOnNullServices()
    {
        // Act & Assert
        var act = () => ServiceCollectionExtensions.AddDataverseConnectionPool(null!, _ => { });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void AddDataverseConnectionPool_ThrowsOnNullConfigure()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddDataverseConnectionPool((Action<DataverseOptions>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #region Multi-Environment Configuration Tests

    [Fact]
    public void AddDataverseConnectionPool_WithEnvironment_UsesEnvironmentConnections()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:TenantId"] = "dev-tenant-id",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "DevPrimary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "dev-client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "dev-secret",
            ["Dataverse:Environments:Dev:Connections:0:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:QA:Url"] = "https://qa.crm.dynamics.com",
            ["Dataverse:Environments:QA:Connections:0:Name"] = "QAPrimary",
            ["Dataverse:Environments:QA:Connections:0:ClientId"] = "qa-client-id",
            ["Dataverse:Environments:QA:Connections:0:ClientSecret"] = "qa-secret",
            ["Dataverse:Environments:QA:Connections:0:Url"] = "https://qa.crm.dynamics.com",
            ["Dataverse:Pool:DisableAffinityCookie"] = "true"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections.Should().HaveCount(1);
        options.Connections[0].Name.Should().Be("DevPrimary");
        options.Connections[0].ClientId.Should().Be("dev-client-id");
        options.Url.Should().Be("https://dev.crm.dynamics.com");
        options.TenantId.Should().Be("dev-tenant-id");
    }

    [Fact]
    public void AddDataverseConnectionPool_WithEnvironment_QA_UsesQAConnections()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "DevPrimary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "dev-client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "dev-secret",
            ["Dataverse:Environments:Dev:Connections:0:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:QA:Url"] = "https://qa.crm.dynamics.com",
            ["Dataverse:Environments:QA:TenantId"] = "qa-tenant-id",
            ["Dataverse:Environments:QA:Connections:0:Name"] = "QAPrimary",
            ["Dataverse:Environments:QA:Connections:0:ClientId"] = "qa-client-id",
            ["Dataverse:Environments:QA:Connections:0:ClientSecret"] = "qa-secret",
            ["Dataverse:Environments:QA:Connections:0:Url"] = "https://qa.crm.dynamics.com"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "QA");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections.Should().HaveCount(1);
        options.Connections[0].Name.Should().Be("QAPrimary");
        options.Connections[0].ClientId.Should().Be("qa-client-id");
        options.Url.Should().Be("https://qa.crm.dynamics.com");
        options.TenantId.Should().Be("qa-tenant-id");
    }

    [Fact]
    public void AddDataverseConnectionPool_WithoutEnvironment_UsesRootConnections()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Url"] = "https://root.crm.dynamics.com",
            ["Dataverse:Connections:0:Name"] = "RootPrimary",
            ["Dataverse:Connections:0:ClientId"] = "root-client-id",
            ["Dataverse:Connections:0:ClientSecret"] = "root-secret",
            ["Dataverse:Connections:0:Url"] = "https://root.crm.dynamics.com"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections.Should().HaveCount(1);
        options.Connections[0].Name.Should().Be("RootPrimary");
        options.Url.Should().Be("https://root.crm.dynamics.com");
    }

    [Fact]
    public void AddDataverseConnectionPool_InvalidEnvironment_ThrowsKeyNotFoundException()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "DevPrimary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "dev-client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "dev-secret",
            ["Dataverse:Environments:Dev:Connections:0:Url"] = "https://dev.crm.dynamics.com"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataverseConnectionPool(configuration, environment: "NonExistent");

        // Act & Assert - Error occurs when options are resolved
        var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<IOptions<DataverseOptions>>().Value;
        act.Should().Throw<KeyNotFoundException>()
            .WithMessage("*NonExistent*not found*");
    }

    [Fact]
    public void AddDataverseConnectionPool_EnvironmentInheritsRootPoolSettings()
    {
        // Arrange
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Pool:DisableAffinityCookie"] = "true",
            ["Dataverse:Pool:MaxPoolSize"] = "25",
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "DevPrimary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "dev-client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "dev-secret",
            ["Dataverse:Environments:Dev:Connections:0:Url"] = "https://dev.crm.dynamics.com"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert - Pool settings should be inherited from root
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Pool.DisableAffinityCookie.Should().BeTrue();
        options.Pool.MaxPoolSize.Should().Be(25);
    }

    [Fact]
    public void AddDataverseConnectionPool_ThrowsOnNullConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act & Assert
        var act = () => services.AddDataverseConnectionPool((IConfiguration)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Property Inheritance Tests

    [Fact]
    public void AddDataverseConnectionPool_InheritsUrlFromEnvironmentToConnection()
    {
        // Arrange - Connection does NOT have Url, environment does
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "Primary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "secret"
            // Note: No Url on connection - should inherit from environment
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections[0].Url.Should().Be("https://dev.crm.dynamics.com");
    }

    [Fact]
    public void AddDataverseConnectionPool_InheritsTenantIdFromRootToConnection()
    {
        // Arrange - TenantId at root, no environment TenantId
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:TenantId"] = "root-tenant-id",
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "Primary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "secret"
            // Note: No TenantId on environment or connection - should inherit from root
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections[0].TenantId.Should().Be("root-tenant-id");
    }

    [Fact]
    public void AddDataverseConnectionPool_EnvironmentTenantIdOverridesRoot()
    {
        // Arrange - TenantId at root AND environment (environment should win)
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:TenantId"] = "root-tenant-id",
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:TenantId"] = "env-tenant-id",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "Primary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "secret"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections[0].TenantId.Should().Be("env-tenant-id");
    }

    [Fact]
    public void AddDataverseConnectionPool_ConnectionTenantIdWinsOverEnvironment()
    {
        // Arrange - TenantId at all levels (connection should win)
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:TenantId"] = "root-tenant-id",
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:TenantId"] = "env-tenant-id",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "Primary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "secret",
            ["Dataverse:Environments:Dev:Connections:0:TenantId"] = "connection-tenant-id"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections[0].TenantId.Should().Be("connection-tenant-id");
    }

    [Fact]
    public void AddDataverseConnectionPool_UsesDefaultEnvironmentWhenNotSpecified()
    {
        // Arrange - Environments defined, DefaultEnvironment set, no explicit param
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:DefaultEnvironment"] = "QA",
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "DevPrimary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "dev-client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "dev-secret",
            ["Dataverse:Environments:QA:Url"] = "https://qa.crm.dynamics.com",
            ["Dataverse:Environments:QA:Connections:0:Name"] = "QAPrimary",
            ["Dataverse:Environments:QA:Connections:0:ClientId"] = "qa-client-id",
            ["Dataverse:Environments:QA:Connections:0:ClientSecret"] = "qa-secret"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act - No environment parameter, should use DefaultEnvironment
        services.AddDataverseConnectionPool(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections.Should().HaveCount(1);
        options.Connections[0].Name.Should().Be("QAPrimary");
        options.Connections[0].Url.Should().Be("https://qa.crm.dynamics.com");
    }

    [Fact]
    public void AddDataverseConnectionPool_UsesFirstEnvironmentWhenNoDefaultSpecified()
    {
        // Arrange - Environments defined, no DefaultEnvironment, no explicit param
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Environments:Alpha:Url"] = "https://alpha.crm.dynamics.com",
            ["Dataverse:Environments:Alpha:Connections:0:Name"] = "AlphaPrimary",
            ["Dataverse:Environments:Alpha:Connections:0:ClientId"] = "alpha-client-id",
            ["Dataverse:Environments:Alpha:Connections:0:ClientSecret"] = "alpha-secret",
            ["Dataverse:Environments:Beta:Url"] = "https://beta.crm.dynamics.com",
            ["Dataverse:Environments:Beta:Connections:0:Name"] = "BetaPrimary",
            ["Dataverse:Environments:Beta:Connections:0:ClientId"] = "beta-client-id",
            ["Dataverse:Environments:Beta:Connections:0:ClientSecret"] = "beta-secret"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act - No environment parameter, no DefaultEnvironment - should use first
        services.AddDataverseConnectionPool(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections.Should().HaveCount(1);
        // First environment in dictionary (order may vary, but should be one of them)
        options.Connections[0].Name.Should().BeOneOf("AlphaPrimary", "BetaPrimary");
    }

    [Fact]
    public void AddDataverseConnectionPool_RootConnectionsInheritRootTenantId()
    {
        // Arrange - No environments, just root-level connections
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:TenantId"] = "root-tenant-id",
            ["Dataverse:Url"] = "https://org.crm.dynamics.com",
            ["Dataverse:Connections:0:Name"] = "Primary",
            ["Dataverse:Connections:0:ClientId"] = "client-id",
            ["Dataverse:Connections:0:ClientSecret"] = "secret"
            // Note: No Url or TenantId on connection - should inherit from root
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections[0].TenantId.Should().Be("root-tenant-id");
        options.Connections[0].Url.Should().Be("https://org.crm.dynamics.com");
    }

    [Fact]
    public void AddDataverseConnectionPool_MultipleConnectionsInheritFromEnvironment()
    {
        // Arrange - Two connections, both should inherit
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:TenantId"] = "shared-tenant-id",
            ["Dataverse:Environments:Dev:Url"] = "https://dev.crm.dynamics.com",
            ["Dataverse:Environments:Dev:Connections:0:Name"] = "Primary",
            ["Dataverse:Environments:Dev:Connections:0:ClientId"] = "primary-client-id",
            ["Dataverse:Environments:Dev:Connections:0:ClientSecret"] = "primary-secret",
            ["Dataverse:Environments:Dev:Connections:1:Name"] = "Secondary",
            ["Dataverse:Environments:Dev:Connections:1:ClientId"] = "secondary-client-id",
            ["Dataverse:Environments:Dev:Connections:1:ClientSecret"] = "secondary-secret"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddDataverseConnectionPool(configuration, environment: "Dev");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        options.Connections.Should().HaveCount(2);
        options.Connections[0].Url.Should().Be("https://dev.crm.dynamics.com");
        options.Connections[0].TenantId.Should().Be("shared-tenant-id");
        options.Connections[1].Url.Should().Be("https://dev.crm.dynamics.com");
        options.Connections[1].TenantId.Should().Be("shared-tenant-id");
    }

    #endregion
}
