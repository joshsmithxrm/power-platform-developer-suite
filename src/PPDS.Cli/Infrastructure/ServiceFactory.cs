using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Factory for creating configured service providers for CLI commands.
/// </summary>
public static class ServiceFactory
{
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

    /// <summary>
    /// Creates a service provider for offline analysis (no Dataverse connection needed).
    /// </summary>
    /// <returns>A service provider with analysis services registered.</returns>
    public static ServiceProvider CreateAnalysisProvider()
    {
        var services = new ServiceCollection();
        services.AddDataverseMigration();
        return services.BuildServiceProvider();
    }
}
