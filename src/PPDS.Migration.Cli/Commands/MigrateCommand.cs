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

        var debugOption = new Option<bool>(
            name: "--debug",
            getDefaultValue: () => false,
            description: "Enable diagnostic logging output");

        var sourceEnvOption = new Option<string>(
            name: "--source-env",
            description: "Source environment name from configuration (e.g., Dev)")
        {
            IsRequired = true
        };

        var targetEnvOption = new Option<string>(
            name: "--target-env",
            description: "Target environment name from configuration (e.g., Prod)")
        {
            IsRequired = true
        };

        var configOption = new Option<FileInfo?>(
            name: "--config",
            description: "Path to configuration file (default: appsettings.json in current directory)");

        var command = new Command("migrate",
            "Migrate data from source to target Dataverse environment. " +
            ConfigurationHelper.GetConfigurationHelpDescription())
        {
            schemaOption,
            sourceEnvOption,
            targetEnvOption,
            configOption,
            tempDirOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            jsonOption,
            debugOption
        };

        command.SetHandler(async (context) =>
        {
            var schema = context.ParseResult.GetValueForOption(schemaOption)!;
            var sourceEnv = context.ParseResult.GetValueForOption(sourceEnvOption)!;
            var targetEnv = context.ParseResult.GetValueForOption(targetEnvOption)!;
            var config = context.ParseResult.GetValueForOption(configOption);
            var secretsId = context.ParseResult.GetValueForOption(Program.SecretsIdOption);
            var tempDir = context.ParseResult.GetValueForOption(tempDirOption);
            var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
            var bypassPlugins = context.ParseResult.GetValueForOption(bypassPluginsOption);
            var bypassFlows = context.ParseResult.GetValueForOption(bypassFlowsOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);
            var debug = context.ParseResult.GetValueForOption(debugOption);

            // Resolve source and target connections from configuration
            ConnectionResolver.ResolvedConnection sourceResolved;
            ConnectionResolver.ResolvedConnection targetResolved;
            try
            {
                (sourceResolved, targetResolved) = ConnectionResolver.ResolveSourceTarget(
                    sourceEnv, targetEnv, config?.FullName, secretsId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            context.ExitCode = await ExecuteAsync(
                sourceResolved.Config, targetResolved.Config, schema, tempDir,
                batchSize, bypassPlugins, bypassFlows, json, debug, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ConnectionResolver.ConnectionConfig sourceConnection,
        ConnectionResolver.ConnectionConfig targetConnection,
        FileInfo schema,
        DirectoryInfo? tempDir,
        int batchSize,
        bool bypassPlugins,
        bool bypassFlows,
        bool json,
        bool debug,
        CancellationToken cancellationToken)
    {
        string? tempDataFile = null;

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

            // Determine temp directory
            var tempDirectory = tempDir?.FullName ?? Path.GetTempPath();
            if (!Directory.Exists(tempDirectory))
            {
                progressReporter.Error(new DirectoryNotFoundException($"Temporary directory does not exist: {tempDirectory}"), null);
                return ExitCodes.InvalidArguments;
            }

            // Create temp file path for intermediate data
            tempDataFile = Path.Combine(tempDirectory, $"ppds-migrate-{Guid.NewGuid():N}.zip");

            // Phase 1: Export from source
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Phase 1: Connecting to source ({sourceConnection.Url})..."
            });

            await using var sourceProvider = ServiceFactory.CreateProvider(sourceConnection, "Source", debug);
            var exporter = sourceProvider.GetRequiredService<IExporter>();

            var exportResult = await exporter.ExportAsync(
                schema.FullName,
                tempDataFile,
                new ExportOptions(),
                progressReporter,
                cancellationToken);

            if (!exportResult.Success)
            {
                return ExitCodes.Failure;
            }

            // Phase 2: Import to target
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Phase 2: Connecting to target ({targetConnection.Url})..."
            });

            await using var targetProvider = ServiceFactory.CreateProvider(targetConnection, "Target", debug);
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

            return importResult.Success ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Migration cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            progressReporter.Error(ex, "Migration failed");
            if (debug)
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
                    progressReporter.Report(new ProgressEventArgs
                    {
                        Phase = MigrationPhase.Complete,
                        Message = "Cleaned up temporary file."
                    });
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }
}
