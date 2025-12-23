using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Import data from a ZIP file into a Dataverse environment.
/// </summary>
public static class ImportCommand
{
    public static Command Create()
    {
        var connectionOption = new Option<string?>(
            aliases: ["--connection", "-c"],
            description: ConnectionResolver.GetHelpDescription(ConnectionResolver.ConnectionEnvVar));

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

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            getDefaultValue: () => false,
            description: "Verbose output");

        var command = new Command("import", "Import data from a ZIP file into Dataverse")
        {
            connectionOption,
            dataOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            modeOption,
            userMappingOption,
            jsonOption,
            verboseOption
        };

        command.SetHandler(async (context) =>
        {
            var connectionArg = context.ParseResult.GetValueForOption(connectionOption);
            var data = context.ParseResult.GetValueForOption(dataOption)!;
            var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
            var bypassPlugins = context.ParseResult.GetValueForOption(bypassPluginsOption);
            var bypassFlows = context.ParseResult.GetValueForOption(bypassFlowsOption);
            var continueOnError = context.ParseResult.GetValueForOption(continueOnErrorOption);
            var mode = context.ParseResult.GetValueForOption(modeOption);
            var userMappingFile = context.ParseResult.GetValueForOption(userMappingOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

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

            // Resolve connection string from argument or environment variable
            string connection;
            try
            {
                connection = ConnectionResolver.Resolve(
                    connectionArg,
                    ConnectionResolver.ConnectionEnvVar,
                    "connection");
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            context.ExitCode = await ExecuteAsync(
                connection, data, batchSize, bypassPlugins, bypassFlows,
                continueOnError, mode, userMappingFile, json, verbose, context.GetCancellationToken());
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string connection,
        FileInfo data,
        int batchSize,
        bool bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        ImportMode mode,
        FileInfo? userMappingFile,
        bool json,
        bool verbose,
        CancellationToken cancellationToken)
    {
        try
        {
            // Create service provider and get importer
            await using var serviceProvider = ServiceFactory.CreateProvider(connection);
            var importer = serviceProvider.GetRequiredService<IImporter>();
            var progressReporter = ServiceFactory.CreateProgressReporter(json);

            // Load user mappings if provided
            UserMappingCollection? userMappings = null;
            if (userMappingFile != null)
            {
                if (!json)
                {
                    Console.WriteLine($"Loading user mappings from {userMappingFile.FullName}...");
                }

                var mappingReader = new UserMappingReader();
                userMappings = await mappingReader.ReadAsync(userMappingFile.FullName, cancellationToken);

                if (!json)
                {
                    Console.WriteLine($"Loaded {userMappings.Mappings.Count} user mapping(s).");
                }
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

            // Execute import
            var result = await importer.ImportAsync(
                data.FullName,
                importOptions,
                progressReporter,
                cancellationToken);

            // Report completion
            if (!result.Success)
            {
                ConsoleOutput.WriteError($"Import completed with {result.Errors.Count} error(s).", json);
                return ExitCodes.Failure;
            }

            if (!json)
            {
                Console.WriteLine();
                Console.WriteLine("Import completed successfully.");
                Console.WriteLine($"Tiers: {result.TiersProcessed}, Records: {result.RecordsImported:N0}");
                Console.WriteLine($"Duration: {result.Duration:hh\\:mm\\:ss}, Rate: {result.RecordsPerSecond:F1} rec/s");
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.WriteError("Import cancelled by user.", json);
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError($"Import failed: {ex.Message}", json);
            if (verbose)
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
