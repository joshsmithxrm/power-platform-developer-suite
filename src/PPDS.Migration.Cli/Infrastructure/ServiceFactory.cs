using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Dataverse.DependencyInjection;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Analysis;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Factory for creating configured service providers for CLI commands.
/// </summary>
public static class ServiceFactory
{
    /// <summary>
    /// Creates a service provider configured with a single Dataverse connection.
    /// </summary>
    /// <param name="connectionString">The Dataverse connection string.</param>
    /// <param name="connectionName">Optional name for the connection. Default: "Primary"</param>
    /// <returns>A configured service provider.</returns>
    public static ServiceProvider CreateProvider(string connectionString, string connectionName = "Primary")
    {
        var services = new ServiceCollection();

        // Add logging (minimal for CLI - no console output to avoid interfering with CLI)
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Add Dataverse connection pool
        services.AddDataverseConnectionPool(options =>
        {
            options.Connections.Add(new DataverseConnection(connectionName, connectionString));
            options.Pool.Enabled = true;
            options.Pool.MaxPoolSize = Environment.ProcessorCount * 4;
            options.Pool.DisableAffinityCookie = true;
        });

        // Add migration services
        services.AddDataverseMigration();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a service provider for schema analysis (no connection required).
    /// </summary>
    /// <returns>A configured service provider with schema reading capabilities.</returns>
    public static ServiceProvider CreateAnalysisProvider()
    {
        var services = new ServiceCollection();

        // Add logging (minimal for CLI)
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
        });

        // Register only the analysis components (no Dataverse connection needed)
        services.AddTransient<ICmtSchemaReader, CmtSchemaReader>();
        services.AddTransient<IDependencyGraphBuilder, DependencyGraphBuilder>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates a progress reporter based on the output mode.
    /// </summary>
    /// <param name="useJson">Whether to output JSON format.</param>
    /// <returns>An appropriate progress reporter.</returns>
    public static IProgressReporter CreateProgressReporter(bool useJson)
    {
        return useJson
            ? new JsonProgressReporter(Console.Out)
            : new ConsoleProgressReporter();
    }
}
