using System.CommandLine;
using System.Runtime.InteropServices;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Import data from a ZIP file into a Dataverse environment.
/// </summary>
public static class ImportCommand
{
    public static Command Create()
    {
        var dataOption = new Option<FileInfo>("--data", "-d")
        {
            Description = "Path to data.zip file",
            Required = true
        }.AcceptExistingOnly();

        var bypassPluginsOption = new Option<string?>("--bypass-plugins")
        {
            Description = "Bypass custom plugin execution: sync, async, or all (requires prvBypassCustomBusinessLogic privilege)"
        };
        bypassPluginsOption.AcceptOnlyFromAmong("sync", "async", "all");

        var bypassFlowsOption = new Option<bool>("--bypass-flows")
        {
            Description = "Bypass Power Automate flow triggers (no special privilege required)",
            DefaultValueFactory = _ => false
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Continue import on individual record failures",
            DefaultValueFactory = _ => false
        };

        var modeOption = new Option<ImportMode>("--mode", "-m")
        {
            Description = "Import mode: Create, Update, or Upsert",
            DefaultValueFactory = _ => ImportMode.Upsert
        };

        var userMappingOption = new Option<FileInfo?>("--user-mapping", "-u")
        {
            Description = "Path to user mapping XML file for remapping user references"
        };
        userMappingOption.Validators.Add(result =>
        {
            var file = result.GetValue(userMappingOption);
            if (file is { Exists: false })
                result.AddError($"User mapping file not found: {file.FullName}");
        });

        var stripOwnerFieldsOption = new Option<bool>("--strip-owner-fields")
        {
            Description = "Strip ownership fields (ownerid, createdby, modifiedby) allowing Dataverse to assign current user",
            DefaultValueFactory = _ => false
        };

        var skipMissingColumnsOption = new Option<bool>("--skip-missing-columns")
        {
            Description = "Skip columns that exist in exported data but not in target environment (prevents schema mismatch errors)",
            DefaultValueFactory = _ => false
        };

        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format",
            DefaultValueFactory = _ => OutputFormat.Text
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

        var errorReportOption = new Option<FileInfo?>("--error-report")
        {
            Description = "Base path for output files (creates .errors.jsonl, .progress.log, .summary.json). Errors are streamed in real-time."
        };

        var command = new Command("import", "Import data from a ZIP file into Dataverse")
        {
            dataOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            modeOption,
            userMappingOption,
            stripOwnerFieldsOption,
            skipMissingColumnsOption,
            outputFormatOption,
            verboseOption,
            debugOption,
            errorReportOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var data = parseResult.GetValue(dataOption)!;
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var bypassPluginsValue = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var mode = parseResult.GetValue(modeOption);
            var userMappingFile = parseResult.GetValue(userMappingOption);
            var stripOwnerFields = parseResult.GetValue(stripOwnerFieldsOption);
            var skipMissingColumns = parseResult.GetValue(skipMissingColumnsOption);
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);
            var errorReport = parseResult.GetValue(errorReportOption);

            var bypassPlugins = DataCommandGroup.ParseBypassPlugins(bypassPluginsValue);

            return await ExecuteAsync(
                profile, environment, data, bypassPlugins, bypassFlows,
                continueOnError, mode, userMappingFile, stripOwnerFields,
                skipMissingColumns, outputFormat, verbose, debug, errorReport, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profileName,
        string? environment,
        FileInfo data,
        CustomLogicBypass bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        ImportMode mode,
        FileInfo? userMappingFile,
        bool stripOwnerFields,
        bool skipMissingColumns,
        OutputFormat outputFormat,
        bool verbose,
        bool debug,
        FileInfo? errorReport,
        CancellationToken cancellationToken)
    {
        var progressReporter = ServiceFactory.CreateProgressReporter(outputFormat, "Import");

        try
        {
            // Factory handles environment resolution automatically
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profileName,
                environment,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            if (outputFormat != OutputFormat.Json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var importer = serviceProvider.GetRequiredService<IImporter>();
            var connectionPool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();

            // Get current user ID for fallback when user mappings can't resolve a reference
            var whoAmIResponse = (WhoAmIResponse)await connectionPool.ExecuteAsync(
                new WhoAmIRequest(), cancellationToken);
            var currentUserId = whoAmIResponse.UserId;

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
            else if (stripOwnerFields)
            {
                // When stripping owner fields without explicit mapping, create a default mapping
                // that will use the current user as fallback for any unresolved user references
                userMappings = new UserMappingCollection { UseCurrentUserAsDefault = true };
            }

            if (stripOwnerFields)
            {
                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = "Owner fields will be stripped (ownerid, createdby, modifiedby, etc.)"
                });
            }

            // Create streaming output manager if error report path specified
            ImportOutputManager? outputManager = null;
            var basePath = errorReport?.FullName;
            if (basePath != null)
            {
                // Remove extension if provided (e.g., .json) to use as base path
                var extension = Path.GetExtension(basePath);
                if (!string.IsNullOrEmpty(extension))
                {
                    basePath = basePath[..^extension.Length];
                }

                outputManager = new ImportOutputManager(basePath);

                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Streaming output to: {outputManager.ErrorsPath}"
                });
            }

            try
            {
                var importOptions = new ImportOptions
                {
                    BypassCustomPlugins = bypassPlugins,
                    BypassPowerAutomateFlows = bypassFlows,
                    ContinueOnError = continueOnError,
                    Mode = mode,
                    UserMappings = userMappings,
                    StripOwnerFields = stripOwnerFields,
                    SkipMissingColumns = skipMissingColumns,
                    CurrentUserId = currentUserId,
                    // Wire up error streaming callback
                    ErrorCallback = outputManager != null ? outputManager.LogError : null
                };

                var result = await importer.ImportAsync(
                    data.FullName,
                    importOptions,
                    progressReporter,
                    cancellationToken);

                // Write summary if output manager is active
                if (outputManager != null)
                {
                    var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                    var executionContext = new ImportExecutionContext
                    {
                        CliVersion = ErrorOutput.Version,
                        SdkVersion = ErrorOutput.SdkVersion,
                        RuntimeVersion = Environment.Version.ToString(),
                        Platform = RuntimeInformation.OSDescription,
                        ImportMode = importOptions.Mode.ToString(),
                        StripOwnerFields = importOptions.StripOwnerFields,
                        BypassPlugins = importOptions.BypassCustomPlugins != CustomLogicBypass.None,
                        UserMappingProvided = importOptions.UserMappings != null
                    };

                    await outputManager.WriteSummaryAsync(
                        result,
                        data.FullName,
                        connectionInfo.EnvironmentUrl,
                        executionContext,
                        cancellationToken);

                    Console.Error.WriteLine($"Output written to:");
                    Console.Error.WriteLine($"  Errors: {outputManager.ErrorsPath}");
                    Console.Error.WriteLine($"  Progress: {outputManager.ProgressPath}");
                    Console.Error.WriteLine($"  Summary: {outputManager.SummaryPath}");
                }

                return result.Success ? ExitCodes.Success : ExitCodes.Failure;
            }
            finally
            {
                // Always dispose the output manager to flush streams
                if (outputManager != null)
                {
                    await outputManager.DisposeAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Import cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (SchemaMismatchException ex)
        {
            // Schema mismatch gets special handling - display the detailed message
            Console.Error.WriteLine();
            Console.Error.WriteLine(ex.Message);
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
}
