using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.BulkOperations;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Copy data from one Dataverse environment to another.
/// </summary>
public static class CopyCommand
{
    public static Command Create()
    {
        var schemaOption = new Option<FileInfo>("--schema", "-s")
        {
            Description = "Path to schema.xml file",
            Required = true
        }.AcceptExistingOnly();

        var sourceProfileOption = new Option<string?>("--source-profile", "-sp")
        {
            Description = "Authentication profile for source environment (defaults to active profile)"
        };

        var targetProfileOption = new Option<string?>("--target-profile", "-tp")
        {
            Description = "Authentication profile(s) for target environment - comma-separated for parallel imports (defaults to active profile)"
        };

        var sourceEnvOption = new Option<string>("--source-env", "-se")
        {
            Description = "Source environment - accepts URL, friendly name, unique name, or ID",
            Required = true
        };

        var targetEnvOption = new Option<string>("--target-env", "-te")
        {
            Description = "Target environment - accepts URL, friendly name, unique name, or ID",
            Required = true
        };

        var tempDirOption = new Option<DirectoryInfo?>("--temp-dir")
        {
            Description = "Temporary directory for intermediate data file (default: system temp)"
        };

        var parallelOption = new Option<int>("--parallel")
        {
            Description = "Maximum concurrent entity exports (only applies when schema contains multiple entities)",
            DefaultValueFactory = _ => Environment.ProcessorCount * 2
        };
        parallelOption.Validators.Add(result =>
        {
            if (result.GetValue(parallelOption) < 1)
                result.AddError("--parallel must be at least 1");
        });

        var batchSizeOption = new Option<int>("--batch-size")
        {
            Description = "Records per API request (all records are exported; this controls request size)",
            DefaultValueFactory = _ => 5000
        };
        batchSizeOption.Validators.Add(result =>
        {
            var value = result.GetValue(batchSizeOption);
            if (value < 1)
                result.AddError("--batch-size must be at least 1");
            if (value > 5000)
                result.AddError("--batch-size cannot exceed 5000 (Dataverse limit)");
        });

        var bypassPluginsOption = new Option<string?>("--bypass-plugins")
        {
            Description = "Bypass custom plugin execution on target: sync, async, or all (requires prvBypassCustomBusinessLogic privilege)"
        };
        bypassPluginsOption.AcceptOnlyFromAmong("sync", "async", "all");

        var bypassFlowsOption = new Option<bool>("--bypass-flows")
        {
            Description = "Bypass Power Automate flow triggers on target (no special privilege required)",
            DefaultValueFactory = _ => false
        };

        var skipMissingColumnsOption = new Option<bool>("--skip-missing-columns")
        {
            Description = "Skip columns that exist in source but not in target environment (prevents schema mismatch errors)",
            DefaultValueFactory = _ => false
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Continue import on individual record failures",
            DefaultValueFactory = _ => true
        };

        var stripOwnerFieldsOption = new Option<bool>("--strip-owner-fields")
        {
            Description = "Strip ownership fields (ownerid, createdby, modifiedby) allowing Dataverse to assign current user",
            DefaultValueFactory = _ => false
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

        var jsonOption = new Option<bool>("--json", "-j")
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

        var command = new Command("copy", "Copy data from source to target Dataverse environment")
        {
            schemaOption,
            sourceProfileOption,
            targetProfileOption,
            sourceEnvOption,
            targetEnvOption,
            tempDirOption,
            parallelOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            skipMissingColumnsOption,
            continueOnErrorOption,
            stripOwnerFieldsOption,
            userMappingOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schema = parseResult.GetValue(schemaOption)!;
            var sourceProfile = parseResult.GetValue(sourceProfileOption);
            var targetProfile = parseResult.GetValue(targetProfileOption);
            var sourceEnv = parseResult.GetValue(sourceEnvOption)!;
            var targetEnv = parseResult.GetValue(targetEnvOption)!;
            var tempDir = parseResult.GetValue(tempDirOption);
            var parallel = parseResult.GetValue(parallelOption);
            var batchSize = parseResult.GetValue(batchSizeOption);
            var bypassPluginsValue = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var skipMissingColumns = parseResult.GetValue(skipMissingColumnsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var stripOwnerFields = parseResult.GetValue(stripOwnerFieldsOption);
            var userMappingFile = parseResult.GetValue(userMappingOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            var bypassPlugins = DataCommandGroup.ParseBypassPlugins(bypassPluginsValue);

            return await ExecuteAsync(
                sourceProfile, targetProfile,
                sourceEnv, targetEnv,
                schema, tempDir, parallel, batchSize,
                bypassPlugins, bypassFlows, skipMissingColumns,
                continueOnError, stripOwnerFields, userMappingFile,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? sourceProfileName,
        string? targetProfileName,
        string sourceEnv,
        string targetEnv,
        FileInfo schema,
        DirectoryInfo? tempDir,
        int parallel,
        int batchSize,
        CustomLogicBypass bypassPlugins,
        bool bypassFlows,
        bool skipMissingColumns,
        bool continueOnError,
        bool stripOwnerFields,
        FileInfo? userMappingFile,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        string? tempDataFile = null;
        var progressReporter = ServiceFactory.CreateProgressReporter(json, "Copy");

        try
        {
            var tempDirectory = tempDir?.FullName ?? Path.GetTempPath();
            if (!Directory.Exists(tempDirectory))
            {
                progressReporter.Error(new DirectoryNotFoundException($"Temporary directory does not exist: {tempDirectory}"), null);
                return ExitCodes.InvalidArguments;
            }

            tempDataFile = Path.Combine(tempDirectory, $"ppds-copy-{Guid.NewGuid():N}.zip");

            // Create source service provider - factory handles environment resolution
            await using var sourceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                sourceProfileName,
                sourceEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            if (!json)
            {
                var sourceConnectionInfo = sourceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAsLabeled("Source", sourceConnectionInfo);
            }

            var exporter = sourceProvider.GetRequiredService<IExporter>();

            var exportOptions = new ExportOptions
            {
                DegreeOfParallelism = parallel,
                PageSize = batchSize
            };

            // Set operation name for export phase
            progressReporter.OperationName = "Export";

            var exportResult = await exporter.ExportAsync(
                schema.FullName,
                tempDataFile,
                exportOptions,
                progressReporter,
                cancellationToken);

            if (!exportResult.Success)
            {
                return ExitCodes.Failure;
            }

            // Reset progress reporter for import phase (restarts stopwatch)
            progressReporter.Reset();
            progressReporter.OperationName = "Copy";

            // Target supports comma-separated profiles for parallel import scaling
            await using var targetProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                targetProfileName,
                targetEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            if (!json)
            {
                var targetConnectionInfo = targetProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAsLabeled("Target", targetConnectionInfo);
                Console.WriteLine();
            }

            var importer = targetProvider.GetRequiredService<IImporter>();

            // Load user mappings if specified
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

            if (stripOwnerFields)
            {
                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = "Owner fields will be stripped (ownerid, createdby, modifiedby, etc.)"
                });
            }

            var importOptions = new ImportOptions
            {
                BypassCustomPlugins = bypassPlugins,
                BypassPowerAutomateFlows = bypassFlows,
                SkipMissingColumns = skipMissingColumns,
                ContinueOnError = continueOnError,
                StripOwnerFields = stripOwnerFields,
                UserMappings = userMappings
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
            progressReporter.Error(new OperationCanceledException(), "Copy cancelled by user.");
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
            progressReporter.Error(ex, "Copy failed");
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
