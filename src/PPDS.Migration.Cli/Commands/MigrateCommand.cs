using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Export;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Migrate data from one Dataverse environment to another.
/// </summary>
public static class MigrateCommand
{
    public static Command Create()
    {
        var sourceConnectionOption = new Option<string?>(
            aliases: ["--source-connection", "--source"],
            description: ConnectionResolver.GetHelpDescription(ConnectionResolver.SourceConnectionEnvVar));

        var targetConnectionOption = new Option<string?>(
            aliases: ["--target-connection", "--target"],
            description: ConnectionResolver.GetHelpDescription(ConnectionResolver.TargetConnectionEnvVar));

        var schemaOption = new Option<FileInfo>(
            aliases: ["--schema", "-s"],
            description: "Path to schema.xml file")
        {
            IsRequired = true
        };

        var tempDirOption = new Option<DirectoryInfo?>(
            name: "--temp-dir",
            description: "Temporary directory for intermediate data file (default: system temp)");

        var batchSizeOption = new Option<int>(
            name: "--batch-size",
            getDefaultValue: () => 1000,
            description: "Records per batch for import");

        var bypassPluginsOption = new Option<bool>(
            name: "--bypass-plugins",
            getDefaultValue: () => false,
            description: "Bypass custom plugin execution on target");

        var bypassFlowsOption = new Option<bool>(
            name: "--bypass-flows",
            getDefaultValue: () => false,
            description: "Bypass Power Automate flow triggers on target");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Output progress as JSON (for tool integration)");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            getDefaultValue: () => false,
            description: "Verbose output");

        var command = new Command("migrate", "Migrate data from source to target Dataverse environment")
        {
            sourceConnectionOption,
            targetConnectionOption,
            schemaOption,
            tempDirOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            jsonOption,
            verboseOption
        };

        command.SetHandler(async (context) =>
        {
            var sourceArg = context.ParseResult.GetValueForOption(sourceConnectionOption);
            var targetArg = context.ParseResult.GetValueForOption(targetConnectionOption);
            var schema = context.ParseResult.GetValueForOption(schemaOption)!;
            var tempDir = context.ParseResult.GetValueForOption(tempDirOption);
            var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
            var bypassPlugins = context.ParseResult.GetValueForOption(bypassPluginsOption);
            var bypassFlows = context.ParseResult.GetValueForOption(bypassFlowsOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

            // Resolve connection strings from arguments or environment variables
            string sourceConnection;
            string targetConnection;
            try
            {
                sourceConnection = ConnectionResolver.Resolve(
                    sourceArg,
                    ConnectionResolver.SourceConnectionEnvVar,
                    "source-connection");

                targetConnection = ConnectionResolver.Resolve(
                    targetArg,
                    ConnectionResolver.TargetConnectionEnvVar,
                    "target-connection");
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            context.ExitCode = await ExecuteAsync(
                sourceConnection, targetConnection, schema, tempDir,
                batchSize, bypassPlugins, bypassFlows, json, verbose, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string sourceConnection,
        string targetConnection,
        FileInfo schema,
        DirectoryInfo? tempDir,
        int batchSize,
        bool bypassPlugins,
        bool bypassFlows,
        bool json,
        bool verbose,
        CancellationToken cancellationToken)
    {
        string? tempDataFile = null;

        try
        {
            // Validate schema file exists
            if (!schema.Exists)
            {
                ConsoleOutput.WriteError($"Schema file not found: {schema.FullName}", json);
                return ExitCodes.InvalidArguments;
            }

            // Determine temp directory
            var tempDirectory = tempDir?.FullName ?? Path.GetTempPath();
            if (!Directory.Exists(tempDirectory))
            {
                ConsoleOutput.WriteError($"Temporary directory does not exist: {tempDirectory}", json);
                return ExitCodes.InvalidArguments;
            }

            // Create temp file path for intermediate data
            tempDataFile = Path.Combine(tempDirectory, $"ppds-migrate-{Guid.NewGuid():N}.zip");

            // Create progress reporter
            var progressReporter = ServiceFactory.CreateProgressReporter(json);

            // Phase 1: Export from source
            if (!json)
            {
                Console.WriteLine("Phase 1: Exporting from source environment...");
            }
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Connecting to source environment..."
            });

            await using var sourceProvider = ServiceFactory.CreateProvider(sourceConnection, "Source");
            var exporter = sourceProvider.GetRequiredService<IExporter>();

            var exportResult = await exporter.ExportAsync(
                schema.FullName,
                tempDataFile,
                new ExportOptions(),
                progressReporter,
                cancellationToken);

            if (!exportResult.Success)
            {
                ConsoleOutput.WriteError($"Export failed with {exportResult.Errors.Count} error(s).", json);
                return ExitCodes.Failure;
            }

            // Phase 2: Import to target
            if (!json)
            {
                Console.WriteLine();
                Console.WriteLine("Phase 2: Importing to target environment...");
            }
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = "Connecting to target environment..."
            });

            await using var targetProvider = ServiceFactory.CreateProvider(targetConnection, "Target");
            var importer = targetProvider.GetRequiredService<IImporter>();

            var importOptions = new ImportOptions
            {
                BatchSize = batchSize,
                BypassCustomPluginExecution = bypassPlugins,
                BypassPowerAutomateFlows = bypassFlows
            };

            var importResult = await importer.ImportAsync(
                tempDataFile,
                importOptions,
                progressReporter,
                cancellationToken);

            if (!importResult.Success)
            {
                ConsoleOutput.WriteError($"Import failed with {importResult.Errors.Count} error(s).", json);
                return ExitCodes.Failure;
            }

            // Report completion
            if (!json)
            {
                Console.WriteLine();
                Console.WriteLine("Migration completed successfully.");
                Console.WriteLine($"Exported: {exportResult.RecordsExported:N0} records");
                Console.WriteLine($"Imported: {importResult.RecordsImported:N0} records");
                Console.WriteLine($"Total duration: {exportResult.Duration + importResult.Duration:hh\\:mm\\:ss}");
            }
            else
            {
                var totalRecords = exportResult.RecordsExported;
                var totalDuration = exportResult.Duration + importResult.Duration;
                ConsoleOutput.WriteCompletion(totalDuration, totalRecords, 0, json);
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.WriteError("Migration cancelled by user.", json);
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError($"Migration failed: {ex.Message}", json);
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
        finally
        {
            // Clean up temp file
            if (tempDataFile != null && File.Exists(tempDataFile))
            {
                try
                {
                    File.Delete(tempDataFile);
                    if (!json)
                    {
                        Console.WriteLine($"Cleaned up temporary file: {tempDataFile}");
                    }
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}
