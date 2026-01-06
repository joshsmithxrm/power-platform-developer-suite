using System;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Configuration;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Resilience;
using PPDS.Dataverse.Services;

namespace PPDS.Dataverse.DependencyInjection
{
    /// <summary>
    /// Extension methods for configuring Dataverse services in an <see cref="IServiceCollection"/>.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds Dataverse connection pooling services with a configuration action.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Action to configure options.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <example>
        /// <code>
        /// services.AddDataverseConnectionPool(options =>
        /// {
        ///     options.Connections.Add(new DataverseConnection("Primary", connectionString));
        ///     options.Pool.MaxPoolSize = 50;
        ///     options.Pool.DisableAffinityCookie = true;
        /// });
        /// </code>
        /// </example>
        public static IServiceCollection AddDataverseConnectionPool(
            this IServiceCollection services,
            Action<DataverseOptions> configure)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configure == null)
            {
                throw new ArgumentNullException(nameof(configure));
            }

            services.Configure(configure);

            RegisterServices(services);

            return services;
        }

        /// <summary>
        /// Adds Dataverse connection pooling services from configuration.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configuration">The configuration root.</param>
        /// <param name="environment">
        /// Optional environment name. When specified, uses the connections from that
        /// named environment (Environments dictionary) instead of root-level Connections.
        /// When not specified but Environments are defined, uses DefaultEnvironment or the first environment.
        /// </param>
        /// <param name="sectionName">The configuration section name. Default: "Dataverse"</param>
        /// <returns>The service collection for chaining.</returns>
        /// <remarks>
        /// <para>
        /// <strong>Environment Resolution Order:</strong>
        /// </para>
        /// <list type="number">
        /// <item>Explicit <paramref name="environment"/> parameter (highest priority)</item>
        /// <item>DefaultEnvironment from configuration</item>
        /// <item>First environment if Environments are defined</item>
        /// <item>Root-level Connections (fallback)</item>
        /// </list>
        /// <para>
        /// <strong>Property Inheritance:</strong>
        /// Connections inherit Url and TenantId from their environment, which inherits from root.
        /// Connection-level settings override environment-level, which override root-level.
        /// </para>
        /// </remarks>
        /// <example>
        /// <code>
        /// // Simple single-environment config:
        /// // {
        /// //   "Dataverse": {
        /// //     "TenantId": "your-tenant-id",
        /// //     "Url": "https://org.crm.dynamics.com",
        /// //     "Connections": [{ "Name": "Primary", "ClientId": "...", "ClientSecret": "..." }]
        /// //   }
        /// // }
        /// services.AddDataverseConnectionPool(configuration);
        ///
        /// // Multi-environment config (Url/TenantId inherited to connections):
        /// // {
        /// //   "Dataverse": {
        /// //     "TenantId": "shared-tenant-id",
        /// //     "DefaultEnvironment": "Dev",
        /// //     "Environments": {
        /// //       "Dev": {
        /// //         "Url": "https://dev.crm.dynamics.com",
        /// //         "Connections": [{ "Name": "Primary", "ClientId": "...", "ClientSecret": "..." }]
        /// //       },
        /// //       "QA": {
        /// //         "Url": "https://qa.crm.dynamics.com",
        /// //         "Connections": [{ "Name": "Primary", "ClientId": "...", "ClientSecret": "..." }]
        /// //       }
        /// //     }
        /// //   }
        /// // }
        /// services.AddDataverseConnectionPool(configuration);  // Uses DefaultEnvironment
        /// services.AddDataverseConnectionPool(configuration, environment: "QA");  // Override
        /// </code>
        /// </example>
        public static IServiceCollection AddDataverseConnectionPool(
            this IServiceCollection services,
            IConfiguration configuration,
            string? environment = null,
            string sectionName = "Dataverse")
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (configuration == null)
            {
                throw new ArgumentNullException(nameof(configuration));
            }

            var section = configuration.GetSection(sectionName);

            services.Configure<DataverseOptions>(options =>
            {
                // Bind configuration first
                section.Bind(options);

                // Resolve which connections to use
                if (!string.IsNullOrEmpty(environment))
                {
                    // Explicit environment parameter - use that
                    var env = EnvironmentResolver.GetEnvironment(options, environment);
                    ApplyEnvironment(options, env);
                }
                else if (options.Environments.Count > 0)
                {
                    // Environments defined but no explicit param - use default resolution
                    var env = EnvironmentResolver.GetDefaultEnvironment(options);
                    ApplyEnvironment(options, env);
                }
                else
                {
                    // Root-level Connections - track source index (no environment)
                    TrackConnectionSources(options.Connections, environmentName: null);
                }

                // Inherit Url/TenantId to connections that don't have them set
                foreach (var connection in options.Connections)
                {
                    if (string.IsNullOrEmpty(connection.Url))
                    {
                        connection.Url = options.Url;
                    }
                    if (string.IsNullOrEmpty(connection.TenantId))
                    {
                        connection.TenantId = options.TenantId;
                    }
                }
            });

            RegisterServices(services);

            return services;
        }

        /// <summary>
        /// Applies environment settings to the root options.
        /// </summary>
        private static void ApplyEnvironment(DataverseOptions options, DataverseEnvironmentOptions env)
        {
            // Copy environment connections to root level for the pool to use
            options.Connections = env.Connections;

            // Track source environment and index on each connection for error messages
            TrackConnectionSources(options.Connections, env.Name);

            // Environment-level Url/TenantId override root if present
            if (!string.IsNullOrEmpty(env.Url))
            {
                options.Url = env.Url;
            }
            if (!string.IsNullOrEmpty(env.TenantId))
            {
                options.TenantId = env.TenantId;
            }
        }

        /// <summary>
        /// Tracks the source environment and index on each connection for error messages.
        /// </summary>
        private static void TrackConnectionSources(List<DataverseConnection> connections, string? environmentName)
        {
            for (int i = 0; i < connections.Count; i++)
            {
                connections[i].SourceEnvironment = environmentName;
                connections[i].SourceIndex = i;
            }
        }

        private static void RegisterServices(IServiceCollection services)
        {
            // Register shared services (used by both library and CLI)
            services.RegisterDataverseServices();

            // Connection pool (singleton - long-lived, manages DOP tracking)
            // Registered separately because CLI uses a factory delegate for IConnectionSource[]
            services.AddSingleton<IDataverseConnectionPool, DataverseConnectionPool>();
        }

        /// <summary>
        /// Registers core Dataverse services including throttle tracking and pool-consuming services.
        /// Called by both library registration and CLI ProfileServiceFactory.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This method exists to prevent DI registration divergence between library and CLI.
        /// When adding a new service that should be available in CLI, add it here.
        /// </para>
        /// <para>
        /// <strong>Note:</strong> <see cref="IDataverseConnectionPool"/> is NOT registered here because
        /// the library and CLI use different construction patterns:
        /// <list type="bullet">
        /// <item>Library: Direct type registration from configuration</item>
        /// <item>CLI: Factory delegate with <c>IConnectionSource[]</c> from auth profiles</item>
        /// </list>
        /// </para>
        /// </remarks>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection RegisterDataverseServices(this IServiceCollection services)
        {
            // Throttle tracker (singleton - shared state for throttle detection)
            services.AddSingleton<IThrottleTracker, ThrottleTracker>();

            // Pool-consuming services (transient - stateless, get pool connection per operation)
            services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();
            services.AddTransient<IMetadataService, DataverseMetadataService>();
            services.AddTransient<IQueryExecutor, QueryExecutor>();

            // Domain services (transient - CLI command support)
            services.AddTransient<ISolutionService, SolutionService>();
            services.AddTransient<IImportJobService, ImportJobService>();
            services.AddTransient<IEnvironmentVariableService, EnvironmentVariableService>();
            services.AddTransient<IUserService, UserService>();
            services.AddTransient<IRoleService, RoleService>();

            // Phase 2 services
            services.AddTransient<IFlowService, FlowService>();
            services.AddTransient<IConnectionReferenceService, ConnectionReferenceService>();
            services.AddTransient<IDeploymentSettingsService, DeploymentSettingsService>();

            return services;
        }
    }
}
