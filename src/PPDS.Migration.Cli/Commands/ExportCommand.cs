using System.CommandLine;
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
        var schemaOption = new Option<FileInfo>(
            aliases: ["--schema", "-s"],
            description: "Path to schema.xml file")
        {
            IsRequired = true
        };

        var outputOption = new Option<FileInfo>(
            aliases: ["--output", "-o"],
            description: "Output ZIP file path")
        {
            IsRequired = true
        };

        var parallelOption = new Option<int>(
            name: "--parallel",
            getDefaultValue: () => Environment.ProcessorCount * 2,
            description: "Degree of parallelism for concurrent entity exports");

        var pageSizeOption = new Option<int>(
            name: "--page-size",
            getDefaultValue: () => 5000,
            description: "FetchXML page size for data retrieval");

        var includeFilesOption = new Option<bool>(
            name: "--include-files",
            getDefaultValue: () => false,
            description: "Export file attachments (notes, annotations)");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Output progress as JSON (for tool integration)");

        var debugOption = new Option<bool>(
            name: "--debug",
            getDefaultValue: () => false,
            description: "Enable diagnostic logging output");

        var envOption = new Option<string>(
            name: "--env",
            description: "Environment name from configuration (e.g., Dev, QA, Prod)")
        {
            IsRequired = true
        };

        var configOption = new Option<FileInfo?>(
            name: "--config",
            description: "Path to configuration file (default: appsettings.json in current directory)");

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
            debugOption
        };

        command.SetHandler(async (context) =>
        {
            var schema = context.ParseResult.GetValueForOption(schemaOption)!;
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var env = context.ParseResult.GetValueForOption(envOption)!;
            var config = context.ParseResult.GetValueForOption(configOption);
            var secretsId = context.ParseResult.GetValueForOption(Program.SecretsIdOption);
            var parallel = context.ParseResult.GetValueForOption(parallelOption);
            var pageSize = context.ParseResult.GetValueForOption(pageSizeOption);
            var includeFiles = context.ParseResult.GetValueForOption(includeFilesOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);
            var debug = context.ParseResult.GetValueForOption(debugOption);

            // Resolve connection from configuration
            ConnectionResolver.ResolvedConnection resolved;
            try
            {
                resolved = ConnectionResolver.Resolve(env, config?.FullName, secretsId, "connection");
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            context.ExitCode = await ExecuteAsync(
                resolved.Config, schema, output, parallel, pageSize,
                includeFiles, json, debug, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ConnectionResolver.ConnectionConfig connection,
        FileInfo schema,
        FileInfo output,
        int parallel,
        int pageSize,
        bool includeFiles,
        bool json,
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
                Message = $"Connecting to Dataverse ({connection.Url})..."
            });

            await using var serviceProvider = ServiceFactory.CreateProvider(connection, debug: debug);
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
