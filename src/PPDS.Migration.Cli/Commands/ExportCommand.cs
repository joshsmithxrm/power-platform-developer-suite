using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Export;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Export data from a Dataverse environment to a ZIP file.
/// </summary>
public static class ExportCommand
{
    public static Command Create()
    {
        var schemaOption = new Option<FileInfo>("--schema", "-s")
        {
            Description = "Path to schema.xml file",
            Required = true
        };

        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output ZIP file path",
            Required = true
        };

        var parallelOption = new Option<int>("--parallel")
        {
            Description = "Degree of parallelism for concurrent entity exports",
            DefaultValueFactory = _ => Environment.ProcessorCount * 2
        };

        var pageSizeOption = new Option<int>("--page-size")
        {
            Description = "FetchXML page size for data retrieval",
            DefaultValueFactory = _ => 5000
        };

        var includeFilesOption = new Option<bool>("--include-files")
        {
            Description = "Export file attachments (notes, annotations)",
            DefaultValueFactory = _ => false
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output progress as JSON (for tool integration)",
            DefaultValueFactory = _ => false
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose logging output",
            DefaultValueFactory = _ => false
        };

        var debugOption = new Option<bool>("--debug")
        {
            Description = "Enable diagnostic logging output",
            DefaultValueFactory = _ => false
        };

        var envOption = new Option<string>("--env")
        {
            Description = "Environment name from configuration (e.g., Dev, QA, Prod)",
            Required = true
        };

        var configOption = new Option<FileInfo?>("--config")
        {
            Description = "Path to configuration file (default: appsettings.json in current directory)"
        };

        var command = new Command("export", "Export data from Dataverse to a ZIP file. " + ConfigurationHelper.GetConfigurationHelpDescription())
        {
            schemaOption,
            outputOption,
            envOption,
            configOption,
            parallelOption,
            pageSizeOption,
            includeFilesOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schema = parseResult.GetValue(schemaOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var env = parseResult.GetValue(envOption)!;
            var config = parseResult.GetValue(configOption);
            var secretsId = parseResult.GetValue(Program.SecretsIdOption);
            var parallel = parseResult.GetValue(parallelOption);
            var pageSize = parseResult.GetValue(pageSizeOption);
            var includeFiles = parseResult.GetValue(includeFilesOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            // Resolve connection from configuration (validates environment exists and has connections)
            ConnectionResolver.ResolvedConnection resolved;
            IConfiguration configuration;
            try
            {
                configuration = ConfigurationHelper.BuildRequired(config?.FullName, secretsId);
                resolved = ConnectionResolver.ResolveFromConfig(configuration, env, "connection");
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                return ExitCodes.InvalidArguments;
            }

            return await ExecuteAsync(
                configuration, env, resolved.Config.Url, schema, output, parallel, pageSize,
                includeFiles, json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        IConfiguration configuration,
        string environmentName,
        string environmentUrl,
        FileInfo schema,
        FileInfo output,
        int parallel,
        int pageSize,
        bool includeFiles,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        // Create progress reporter first - it handles all user-facing output
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            // Validate schema file exists
            if (!schema.Exists)
            {
                progressReporter.Error(new FileNotFoundException("Schema file not found", schema.FullName), null);
                return ExitCodes.InvalidArguments;
            }

            // Validate output directory exists
            var outputDir = output.Directory;
            if (outputDir != null && !outputDir.Exists)
            {
                progressReporter.Error(new DirectoryNotFoundException($"Output directory does not exist: {outputDir.FullName}"), null);
                return ExitCodes.InvalidArguments;
            }

            // Report connecting status
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Connecting to Dataverse ({environmentUrl})..."
            });

            // Use CreateProviderFromConfig to get ALL connections for the environment
            await using var serviceProvider = ServiceFactory.CreateProviderFromConfig(configuration, environmentName, verbose, debug);
            var exporter = serviceProvider.GetRequiredService<IExporter>();

            // Configure export options
            var exportOptions = new ExportOptions
            {
                DegreeOfParallelism = parallel,
                PageSize = pageSize,
                ExportFiles = includeFiles
            };

            // Execute export - progress reporter receives Complete() callback with results
            var result = await exporter.ExportAsync(
                schema.FullName,
                output.FullName,
                exportOptions,
                progressReporter,
                cancellationToken);

            return result.Success ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Export cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            progressReporter.Error(ex, "Export failed");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }
}
