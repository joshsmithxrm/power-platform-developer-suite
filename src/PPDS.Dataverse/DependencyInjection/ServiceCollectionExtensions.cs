using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;

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
        /// <param name="sectionName">The configuration section name. Default: "Dataverse"</param>
        /// <returns>The service collection for chaining.</returns>
        /// <example>
        /// <code>
        /// // appsettings.json:
        /// // {
        /// //   "Dataverse": {
        /// //     "Connections": [{ "Name": "Primary", "ConnectionString": "..." }],
        /// //     "Pool": { "MaxPoolSize": 50 }
        /// //   }
        /// // }
        ///
        /// services.AddDataverseConnectionPool(configuration);
        /// </code>
        /// </example>
        public static IServiceCollection AddDataverseConnectionPool(
            this IServiceCollection services,
            IConfiguration configuration,
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

            services.Configure<DataverseOptions>(configuration.GetSection(sectionName));

            RegisterServices(services);

            return services;
        }

        private static void RegisterServices(IServiceCollection services)
        {
            // Throttle tracker (singleton - shared state)
            services.AddSingleton<IThrottleTracker, ThrottleTracker>();

            // Adaptive rate controller (singleton - maintains per-connection state)
            services.AddSingleton<IAdaptiveRateController, AdaptiveRateController>();

            // Connection pool (singleton - long-lived)
            services.AddSingleton<IDataverseConnectionPool, DataverseConnectionPool>();

            // Bulk operation executor (transient - stateless)
            services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();
        }
    }
}
