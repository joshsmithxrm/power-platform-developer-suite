using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
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

        // Profile options
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile for both source and target environments"
        };

        var sourceProfileOption = new Option<string?>("--source-profile")
        {
            Description = "Profile for source environment (overrides --profile for source)"
        };

        var targetProfileOption = new Option<string?>("--target-profile")
        {
            Description = "Profile for target environment (overrides --profile for target)"
        };

        // Environment options
        var sourceEnvOption = new Option<string>("--source-env")
        {
            Description = "Source environment (URL, name, or ID)",
            Required = true
        };

        var targetEnvOption = new Option<string>("--target-env")
        {
            Description = "Target environment (URL, name, or ID)",
            Required = true
        };

        var tempDirOption = new Option<DirectoryInfo?>("--temp-dir")
        {
            Description = "Temporary directory for intermediate data file (default: system temp)"
        };

        var bypassPluginsOption = new Option<bool>("--bypass-plugins")
        {
            Description = "Bypass custom plugin execution on target",
            DefaultValueFactory = _ => false
        };

        var bypassFlowsOption = new Option<bool>("--bypass-flows")
        {
            Description = "Bypass Power Automate flow triggers on target",
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

        var command = new Command("copy", "Copy data from source to target Dataverse environment")
        {
            schemaOption,
            profileOption,
            sourceProfileOption,
            targetProfileOption,
            sourceEnvOption,
            targetEnvOption,
            tempDirOption,
            bypassPluginsOption,
            bypassFlowsOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schema = parseResult.GetValue(schemaOption)!;
            var profile = parseResult.GetValue(profileOption);
            var sourceProfile = parseResult.GetValue(sourceProfileOption);
            var targetProfile = parseResult.GetValue(targetProfileOption);
            var sourceEnv = parseResult.GetValue(sourceEnvOption)!;
            var targetEnv = parseResult.GetValue(targetEnvOption)!;
            var tempDir = parseResult.GetValue(tempDirOption);
            var bypassPlugins = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteAsync(
                profile, sourceProfile, targetProfile,
                sourceEnv, targetEnv,
                schema, tempDir, bypassPlugins, bypassFlows,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? sourceProfile,
        string? targetProfile,
        string sourceEnv,
        string targetEnv,
        FileInfo schema,
        DirectoryInfo? tempDir,
        bool bypassPlugins,
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
            // Determine temp directory
            var tempDirectory = tempDir?.FullName ?? Path.GetTempPath();
            if (!Directory.Exists(tempDirectory))
            {
                progressReporter.Error(new DirectoryNotFoundException($"Temporary directory does not exist: {tempDirectory}"), null);
                return ExitCodes.InvalidArguments;
            }

            // Create temp file path for intermediate data
            tempDataFile = Path.Combine(tempDirectory, $"ppds-copy-{Guid.NewGuid():N}.zip");

            // Determine which profiles to use
            var effectiveSourceProfile = sourceProfile ?? profile;
            var effectiveTargetProfile = targetProfile ?? profile;

            // Phase 1: Export from source
            await using var sourceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                effectiveSourceProfile,
                sourceEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            // Write source connection header (non-JSON mode only)
            if (!json)
            {
                var sourceConnectionInfo = sourceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAsLabeled("Source", sourceConnectionInfo);
            }

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
            await using var targetProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                effectiveTargetProfile,
                targetEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            // Write target connection header (non-JSON mode only)
            if (!json)
            {
                var targetConnectionInfo = targetProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAsLabeled("Target", targetConnectionInfo);
                Console.WriteLine();
            }

            var importer = targetProvider.GetRequiredService<IImporter>();

            var importOptions = new ImportOptions
            {
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
