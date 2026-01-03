using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure.Logging;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Migration.Analysis;
using PPDS.Migration.Formats;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Factory for creating configured service providers for CLI commands.
/// </summary>
public static class ServiceFactory
{
    /// <summary>
    /// Creates a progress reporter based on the output format.
    /// </summary>
    /// <param name="outputFormat">The output format.</param>
    /// <param name="operationName">The operation name for completion messages (e.g., "Export", "Import").</param>
    /// <returns>An appropriate progress reporter.</returns>
    /// <remarks>
    /// Progress is written to stderr to keep stdout clean for command results,
    /// enabling piping (e.g., <c>ppds data export | jq</c>) without interference.
    /// </remarks>
    public static IProgressReporter CreateProgressReporter(OutputFormat outputFormat, string operationName = "Operation")
    {
        // Progress goes to stderr to keep stdout clean for results
        IProgressReporter reporter = outputFormat == OutputFormat.Json
            ? new JsonProgressReporter(Console.Error)
            : new ConsoleProgressReporter();

        reporter.OperationName = operationName;
        return reporter;
    }

    /// <summary>
    /// Creates a service provider for offline analysis (no Dataverse connection needed).
    /// Only registers schema reading and dependency analysis services.
    /// </summary>
    /// <returns>A service provider with analysis services registered.</returns>
    public static ServiceProvider CreateAnalysisProvider()
    {
        var services = new ServiceCollection();

        // Only register services needed for offline analysis - no connection pool required
        services.AddTransient<ICmtSchemaReader, CmtSchemaReader>();
        services.AddTransient<IDependencyGraphBuilder, DependencyGraphBuilder>();

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Creates an output writer based on the output format.
    /// </summary>
    /// <param name="outputFormat">The desired output format.</param>
    /// <param name="debugMode">Whether to include full error details.</param>
    /// <returns>An appropriate IOutputWriter implementation.</returns>
    public static IOutputWriter CreateOutputWriter(OutputFormat outputFormat, bool debugMode = false)
    {
        return outputFormat == OutputFormat.Json
            ? new JsonOutputWriter(debugMode: debugMode)
            : new TextOutputWriter(debugMode);
    }

    /// <summary>
    /// Creates an output writer from global option values.
    /// </summary>
    /// <param name="options">The global option values.</param>
    /// <returns>An appropriate IOutputWriter implementation.</returns>
    public static IOutputWriter CreateOutputWriter(GlobalOptionValues options)
    {
        return CreateOutputWriter(options.OutputFormat, options.Debug);
    }

    /// <summary>
    /// Creates CLI logger options from global option values.
    /// </summary>
    /// <param name="options">The global option values.</param>
    /// <returns>Configured logger options.</returns>
    public static CliLoggerOptions CreateLoggerOptions(GlobalOptionValues options)
    {
        return new CliLoggerOptions
        {
            MinimumLevel = CliLoggerOptions.ResolveLogLevel(options.Quiet, options.Verbose, options.Debug),
            UseJsonFormat = options.IsJsonMode,
            CorrelationId = options.CorrelationId,
            EnableColors = !options.IsJsonMode
        };
    }

    /// <summary>
    /// Creates CLI logger options from individual flags.
    /// </summary>
    /// <param name="quiet">Whether --quiet flag was specified.</param>
    /// <param name="verbose">Whether --verbose flag was specified.</param>
    /// <param name="debug">Whether --debug flag was specified.</param>
    /// <param name="jsonFormat">Whether JSON output format was requested.</param>
    /// <param name="correlationId">Optional correlation ID.</param>
    /// <returns>Configured logger options.</returns>
    public static CliLoggerOptions CreateLoggerOptions(
        bool quiet = false,
        bool verbose = false,
        bool debug = false,
        bool jsonFormat = false,
        string? correlationId = null)
    {
        return new CliLoggerOptions
        {
            MinimumLevel = CliLoggerOptions.ResolveLogLevel(quiet, verbose, debug),
            UseJsonFormat = jsonFormat,
            CorrelationId = correlationId,
            EnableColors = !jsonFormat
        };
    }
}
