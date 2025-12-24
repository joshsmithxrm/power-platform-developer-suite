using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Export;

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

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            getDefaultValue: () => false,
            description: "Verbose output");

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
            verboseOption
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
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

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
                includeFiles, json, verbose, context.GetCancellationToken());
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
        bool verbose,
        CancellationToken cancellationToken)
    {
        try
        {
            // Validate schema file exists
            if (!schema.Exists)
            {
                ConsoleOutput.WriteError($"Schema file not found: {schema.FullName}", json);
                return ExitCodes.InvalidArguments;
            }

            // Validate output directory exists
            var outputDir = output.Directory;
            if (outputDir != null && !outputDir.Exists)
            {
                ConsoleOutput.WriteError($"Output directory does not exist: {outputDir.FullName}", json);
                return ExitCodes.InvalidArguments;
            }

            // Create service provider and get exporter
            await using var serviceProvider = ServiceFactory.CreateProvider(connection);
            var exporter = serviceProvider.GetRequiredService<IExporter>();
            var progressReporter = ServiceFactory.CreateProgressReporter(json);

            // Configure export options
            var exportOptions = new ExportOptions
            {
                DegreeOfParallelism = parallel,
                PageSize = pageSize,
                ExportFiles = includeFiles
            };

            // Execute export
            var result = await exporter.ExportAsync(
                schema.FullName,
                output.FullName,
                exportOptions,
                progressReporter,
                cancellationToken);

            // Report completion
            if (!result.Success)
            {
                ConsoleOutput.WriteError($"Export completed with {result.Errors.Count} error(s).", json);
                return ExitCodes.Failure;
            }

            if (!json)
            {
                Console.WriteLine();
                Console.WriteLine("Export completed successfully.");
                Console.WriteLine($"Output: {output.FullName}");
                Console.WriteLine($"Entities: {result.EntitiesExported}, Records: {result.RecordsExported:N0}");
                Console.WriteLine($"Duration: {result.Duration:hh\\:mm\\:ss}, Rate: {result.RecordsPerSecond:F1} rec/s");
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.WriteError("Export cancelled by user.", json);
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError($"Export failed: {ex.Message}", json);
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }
}
