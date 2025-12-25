using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Import data from a ZIP file into a Dataverse environment.
/// </summary>
public static class ImportCommand
{
    public static Command Create()
    {
        var dataOption = new Option<FileInfo>(
            aliases: ["--data", "-d"],
            description: "Path to data.zip file")
        {
            IsRequired = true
        };

        var batchSizeOption = new Option<int>(
            name: "--batch-size",
            getDefaultValue: () => 1000,
            description: "Records per batch for ExecuteMultiple requests");

        var bypassPluginsOption = new Option<bool>(
            name: "--bypass-plugins",
            getDefaultValue: () => false,
            description: "Bypass custom plugin execution during import");

        var bypassFlowsOption = new Option<bool>(
            name: "--bypass-flows",
            getDefaultValue: () => false,
            description: "Bypass Power Automate flow triggers during import");

        var continueOnErrorOption = new Option<bool>(
            name: "--continue-on-error",
            getDefaultValue: () => false,
            description: "Continue import on individual record failures");

        var modeOption = new Option<ImportMode>(
            name: "--mode",
            getDefaultValue: () => ImportMode.Upsert,
            description: "Import mode: Create, Update, or Upsert");

        var userMappingOption = new Option<FileInfo?>(
            aliases: ["--user-mapping", "-u"],
            description: "Path to user mapping XML file for remapping user references");

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

        var command = new Command("import", "Import data from a ZIP file into Dataverse. " + ConfigurationHelper.GetConfigurationHelpDescription())
        {
            dataOption,
            envOption,
            configOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            modeOption,
            userMappingOption,
            jsonOption,
            debugOption
        };

        command.SetHandler(async (context) =>
        {
            var data = context.ParseResult.GetValueForOption(dataOption)!;
            var env = context.ParseResult.GetValueForOption(envOption)!;
            var config = context.ParseResult.GetValueForOption(configOption);
            var secretsId = context.ParseResult.GetValueForOption(Program.SecretsIdOption);
            var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
            var bypassPlugins = context.ParseResult.GetValueForOption(bypassPluginsOption);
            var bypassFlows = context.ParseResult.GetValueForOption(bypassFlowsOption);
            var continueOnError = context.ParseResult.GetValueForOption(continueOnErrorOption);
            var mode = context.ParseResult.GetValueForOption(modeOption);
            var userMappingFile = context.ParseResult.GetValueForOption(userMappingOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);
            var debug = context.ParseResult.GetValueForOption(debugOption);

            // Validate data file exists first (explicit argument)
            if (!data.Exists)
            {
                ConsoleOutput.WriteError($"Data file not found: {data.FullName}", json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            // Validate user mapping file if specified
            if (userMappingFile != null && !userMappingFile.Exists)
            {
                ConsoleOutput.WriteError($"User mapping file not found: {userMappingFile.FullName}", json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

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
                resolved.Config, data, batchSize, bypassPlugins, bypassFlows,
                continueOnError, mode, userMappingFile, json, debug, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        ConnectionResolver.ConnectionConfig connection,
        FileInfo data,
        int batchSize,
        bool bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        ImportMode mode,
        FileInfo? userMappingFile,
        bool json,
        bool debug,
        CancellationToken cancellationToken)
    {
        // Create progress reporter first - it handles all user-facing output
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            // Report connecting status
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Connecting to Dataverse ({connection.Url})..."
            });

            await using var serviceProvider = ServiceFactory.CreateProvider(connection, debug: debug);
            var importer = serviceProvider.GetRequiredService<IImporter>();

            // Load user mappings if provided
            UserMappingCollection? userMappings = null;
            if (userMappingFile != null)
            {
                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Loading user mappings from {userMappingFile.Name}..."
                });

                var mappingReader = new UserMappingReader();
                userMappings = await mappingReader.ReadAsync(userMappingFile.FullName, cancellationToken);

                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Loaded {userMappings.Mappings.Count} user mapping(s)."
                });
            }

            // Configure import options
            var importOptions = new ImportOptions
            {
                BatchSize = batchSize,
                BypassCustomPluginExecution = bypassPlugins,
                BypassPowerAutomateFlows = bypassFlows,
                ContinueOnError = continueOnError,
                Mode = MapImportMode(mode),
                UserMappings = userMappings
            };

            // Execute import - progress reporter receives Complete() callback with results
            var result = await importer.ImportAsync(
                data.FullName,
                importOptions,
                progressReporter,
                cancellationToken);

            return result.Success ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Import cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            progressReporter.Error(ex, "Import failed");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Maps CLI ImportMode to Migration library ImportMode.
    /// </summary>
    private static PPDS.Migration.Import.ImportMode MapImportMode(ImportMode mode) => mode switch
    {
        ImportMode.Create => PPDS.Migration.Import.ImportMode.Create,
        ImportMode.Update => PPDS.Migration.Import.ImportMode.Update,
        ImportMode.Upsert => PPDS.Migration.Import.ImportMode.Upsert,
        _ => PPDS.Migration.Import.ImportMode.Upsert
    };
}
