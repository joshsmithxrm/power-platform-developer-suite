using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.BulkOperations;
using PPDS.Migration.Export;
using PPDS.Migration.Import;
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
            Description = "Authentication profile for target environment (defaults to active profile)"
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
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            var bypassPlugins = ParseBypassPlugins(bypassPluginsValue);

            return await ExecuteAsync(
                sourceProfile, targetProfile,
                sourceEnv, targetEnv,
                schema, tempDir, parallel, batchSize,
                bypassPlugins, bypassFlows,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static CustomLogicBypass ParseBypassPlugins(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "sync" => CustomLogicBypass.Synchronous,
            "async" => CustomLogicBypass.Asynchronous,
            "all" => CustomLogicBypass.All,
            _ => CustomLogicBypass.None
        };
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

            await using var targetProvider = await ProfileServiceFactory.CreateFromProfileAsync(
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

            var importOptions = new ImportOptions
            {
                BypassCustomPlugins = bypassPlugins,
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
            progressReporter.Error(new OperationCanceledException(), "Copy cancelled by user.");
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
