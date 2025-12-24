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
            ["Dataverse:Pool:MaxConnectionsPerUser"] = "25",
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
        options.Pool.MaxConnectionsPerUser.Should().Be(25);
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

    #region AdaptiveRate Configuration Binding Tests

    /// <summary>
    /// Reproduces the bug where configuration binding populates backing fields with preset defaults,
    /// then a subsequent Configure callback setting a different Preset doesn't take effect because
    /// the backing fields are already populated.
    /// </summary>
    [Fact]
    public void AddDataverseConnectionPool_PresetOverride_ShouldUseNewPresetDefaults()
    {
        // Arrange - JSON config with only Preset specified (no explicit property values)
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Url"] = "https://test.crm.dynamics.com",
            ["Dataverse:Connections:0:Name"] = "Primary",
            ["Dataverse:Connections:0:ClientId"] = "test-client-id",
            ["Dataverse:Connections:0:ClientSecret"] = "test-secret",
            ["Dataverse:AdaptiveRate:Preset"] = "Balanced" // Only Preset, no other AdaptiveRate props
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();

        // First Configure - from AddDataverseConnectionPool which calls Bind()
        services.AddDataverseConnectionPool(configuration);

        // Second Configure - override Preset to Conservative (like demo's CommandBase does)
        services.Configure<DataverseOptions>(options =>
        {
            options.AdaptiveRate.Preset = RateControlPreset.Conservative;
        });

        // Act - resolve options
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        // Assert - should use Conservative preset values, not Balanced
        // Conservative: Factor=140, Threshold=6000, DecreaseFactor=0.4, Stabilization=5, Interval=8s
        // Balanced:     Factor=200, Threshold=8000, DecreaseFactor=0.5, Stabilization=3, Interval=5s
        options.AdaptiveRate.Preset.Should().Be(RateControlPreset.Conservative);
        options.AdaptiveRate.ExecutionTimeCeilingFactor.Should().Be(140, "Conservative preset should use 140");
        options.AdaptiveRate.DecreaseFactor.Should().Be(0.4, "Conservative preset should use 0.4");
        options.AdaptiveRate.StabilizationBatches.Should().Be(5, "Conservative preset should use 5");
        options.AdaptiveRate.MinIncreaseInterval.Should().Be(TimeSpan.FromSeconds(8), "Conservative preset should use 8s");
    }

    /// <summary>
    /// Verifies that WITHOUT Bind(), changing Preset correctly changes getter values.
    /// This is the expected behavior that Bind() breaks.
    /// </summary>
    [Fact]
    public void WithoutBind_ChangingPreset_ShouldChangeGetterValues()
    {
        // Arrange
        var options = new AdaptiveRateOptions();
        options.Preset = RateControlPreset.Balanced;

        // Assert initial values
        options.ExecutionTimeCeilingFactor.Should().Be(200, "Balanced default");

        // Act - change preset
        options.Preset = RateControlPreset.Conservative;

        // Assert - getter should now return Conservative default
        options.ExecutionTimeCeilingFactor.Should().Be(140, "should switch to Conservative default");
    }

    /// <summary>
    /// Documents that direct Bind() without the fix has the backing field issue.
    /// This test documents the .NET ConfigurationBinder behavior that we work around
    /// in AddDataverseConnectionPool.
    /// </summary>
    [Fact]
    public void DirectBind_WithoutFix_HasBackingFieldIssue()
    {
        // Arrange - Config with ONLY Preset, no other AdaptiveRate properties
        var configData = new Dictionary<string, string?>
        {
            ["AdaptiveRate:Preset"] = "Balanced"
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var options = new AdaptiveRateOptions();

        // Act - bind directly (without the fix that AddDataverseConnectionPool applies)
        configuration.GetSection("AdaptiveRate").Bind(options);

        // Assert - Preset should be Balanced
        options.Preset.Should().Be(RateControlPreset.Balanced);
        options.ExecutionTimeCeilingFactor.Should().Be(200, "getter returns Balanced default");

        // Act - change Preset
        options.Preset = RateControlPreset.Conservative;

        // Without the fix (ClearNonConfiguredBackingFields), the backing field was populated
        // by Bind() reading the getter and writing to the setter, so it stays 200
        // This documents WHY we need the fix in AddDataverseConnectionPool
        options.ExecutionTimeCeilingFactor.Should().Be(200,
            "without the fix, Bind() populated backing field, so changing Preset doesn't affect this property");
    }

    /// <summary>
    /// Verifies that when individual properties ARE specified in config alongside Preset,
    /// they should override the preset values (this is the expected behavior).
    /// </summary>
    [Fact]
    public void AddDataverseConnectionPool_ExplicitPropertyValues_ShouldOverridePreset()
    {
        // Arrange - JSON config with Preset AND explicit property values
        var configData = new Dictionary<string, string?>
        {
            ["Dataverse:Url"] = "https://test.crm.dynamics.com",
            ["Dataverse:Connections:0:Name"] = "Primary",
            ["Dataverse:Connections:0:ClientId"] = "test-client-id",
            ["Dataverse:Connections:0:ClientSecret"] = "test-secret",
            ["Dataverse:AdaptiveRate:Preset"] = "Conservative",
            ["Dataverse:AdaptiveRate:ExecutionTimeCeilingFactor"] = "250" // Explicit override
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataverseConnectionPool(configuration);

        // Act
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<DataverseOptions>>().Value;

        // Assert - explicit value should override preset
        options.AdaptiveRate.Preset.Should().Be(RateControlPreset.Conservative);
        options.AdaptiveRate.ExecutionTimeCeilingFactor.Should().Be(250, "explicit config value should override preset");

        // Other values should still come from Conservative preset
        options.AdaptiveRate.DecreaseFactor.Should().Be(0.4);
    }

    #endregion
}
