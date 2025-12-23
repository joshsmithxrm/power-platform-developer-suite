using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Analysis;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;
using PPDS.Migration.Schema;

namespace PPDS.Migration.DependencyInjection
{
    /// <summary>
    /// Extension methods for registering migration services.
    /// </summary>
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// Adds Dataverse migration services to the service collection.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <param name="configure">Action to configure migration options.</param>
        /// <returns>The service collection for chaining.</returns>
        /// <exception cref="InvalidOperationException">Thrown when PPDS.Dataverse connection pool is not registered.</exception>
        public static IServiceCollection AddDataverseMigration(
            this IServiceCollection services,
            Action<MigrationOptions>? configure = null)
        {
            // Verify PPDS.Dataverse is registered
            if (!services.Any(s => s.ServiceType == typeof(IDataverseConnectionPool)))
            {
                throw new InvalidOperationException(
                    "AddDataverseConnectionPool() must be called before AddDataverseMigration(). " +
                    "Migration requires a connection pool for Dataverse operations.");
            }

            // Configure options
            if (configure != null)
            {
                services.Configure(configure);
            }

            // Formats
            services.AddTransient<ICmtSchemaReader, CmtSchemaReader>();
            services.AddTransient<ICmtSchemaWriter, CmtSchemaWriter>();
            services.AddTransient<ICmtDataReader, CmtDataReader>();
            services.AddTransient<ICmtDataWriter, CmtDataWriter>();

            // Schema generation
            services.AddTransient<ISchemaGenerator, DataverseSchemaGenerator>();

            // Analysis
            services.AddTransient<IDependencyGraphBuilder, DependencyGraphBuilder>();
            services.AddTransient<IExecutionPlanBuilder, ExecutionPlanBuilder>();

            // Export
            services.AddTransient<IExporter, ParallelExporter>();

            // Import
            services.AddTransient<IPluginStepManager, PluginStepManager>();
            services.AddTransient<IImporter, TieredImporter>();

            // Progress reporters
            services.AddTransient<ConsoleProgressReporter>();
            services.AddTransient<JsonProgressReporter>(sp =>
                new JsonProgressReporter(Console.Out));

            return services;
        }

        /// <summary>
        /// Adds Dataverse migration services with default options.
        /// </summary>
        /// <param name="services">The service collection.</param>
        /// <returns>The service collection for chaining.</returns>
        public static IServiceCollection AddDataverseMigration(this IServiceCollection services)
            => AddDataverseMigration(services, null);
    }
}
