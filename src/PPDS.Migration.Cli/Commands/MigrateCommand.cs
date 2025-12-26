using System.CommandLine;
using System.CommandLine.Completions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Export;
using PPDS.Migration.Import;
using PPDS.Migration.Progress;

// Aliases for clarity
using AuthResult = PPDS.Migration.Cli.Infrastructure.AuthResolver.AuthResult;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Migrate data from one Dataverse environment to another.
/// </summary>
public static class MigrateCommand
{
    public static Command Create()
    {
        var schemaOption = new Option<FileInfo>("--schema", "-s")
        {
            Description = "Path to schema.xml file",
            Required = true
        }.AcceptExistingOnly();

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

        var sourceEnvOption = new Option<string>("--source-env")
        {
            Description = "Source environment name from configuration (e.g., Dev)",
            Required = true
        };

        var targetEnvOption = new Option<string>("--target-env")
        {
            Description = "Target environment name from configuration (e.g., Prod)",
            Required = true
        };

        // Add tab completion for environment names from configuration
        foreach (var envOption in new[] { sourceEnvOption, targetEnvOption })
        {
            envOption.CompletionSources.Add(ctx =>
            {
                try
                {
                    var config = ConfigurationHelper.Build(null, null);
                    return ConfigurationHelper.GetEnvironmentNames(config)
                        .Select(name => new CompletionItem(name));
                }
                catch
                {
                    return [];
                }
            });
        }

        var configOption = new Option<FileInfo?>("--config")
        {
            Description = "Path to configuration file (default: appsettings.json in current directory)"
        };

        var command = new Command("migrate",
            "Migrate data from source to target Dataverse environment. " +
            ConfigurationHelper.GetConfigurationHelpDescription())
        {
            schemaOption,
            sourceEnvOption,
            targetEnvOption,
            configOption,
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
            var sourceEnv = parseResult.GetValue(sourceEnvOption)!;
            var targetEnv = parseResult.GetValue(targetEnvOption)!;
            var config = parseResult.GetValue(configOption);
            var secretsId = parseResult.GetValue(Program.SecretsIdOption);
            var authMode = parseResult.GetValue(Program.AuthOption);
            var tempDir = parseResult.GetValue(tempDirOption);
            var bypassPlugins = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            // Migrate always requires configuration to resolve URLs for both environments
            // (even with --auth env/interactive/managed, we need config for the environment URLs)
            IConfiguration configuration;
            try
            {
                configuration = ConfigurationHelper.BuildRequired(config?.FullName, secretsId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                return ExitCodes.InvalidArguments;
            }

            // For --auth env mode, we can't use env vars for URLs in migrate (two different envs)
            // Fallback to config mode for URL resolution, but this is a semantic mismatch
            // The user specified --auth env but we need config. Warn them.
            if (authMode == AuthMode.Env)
            {
                ConsoleOutput.WriteError(
                    "Migrate command requires configuration for environment URLs. " +
                    "--auth env is not supported because it cannot specify two different environment URLs. " +
                    "Use --auth config (default), --auth interactive, or --auth managed instead.",
                    json);
                return ExitCodes.InvalidArguments;
            }

            // Resolve auth for both source and target environments
            AuthResolver.AuthResult sourceAuthResult;
            AuthResolver.AuthResult targetAuthResult;
            try
            {
                // For config mode, AuthResolver returns a marker with Url=null
                // For interactive/managed modes, AuthResolver looks up URL from config
                // Note: --url is not used for migrate since we need different URLs for source/target
                sourceAuthResult = AuthResolver.Resolve(authMode, url: null, sourceEnv, configuration);
                targetAuthResult = AuthResolver.Resolve(authMode, url: null, targetEnv, configuration);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                return ExitCodes.InvalidArguments;
            }

            return await ExecuteAsync(
                configuration, sourceEnv, targetEnv, sourceAuthResult, targetAuthResult,
                schema, tempDir, bypassPlugins, bypassFlows, json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        IConfiguration configuration,
        string sourceEnv,
        string targetEnv,
        AuthResolver.AuthResult sourceAuthResult,
        AuthResolver.AuthResult targetAuthResult,
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

        // Create progress reporter first - it handles all user-facing output
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            // File validation now handled by option validators (AcceptExistingOnly)

            // Determine temp directory
            var tempDirectory = tempDir?.FullName ?? Path.GetTempPath();
            if (!Directory.Exists(tempDirectory))
            {
                progressReporter.Error(new DirectoryNotFoundException($"Temporary directory does not exist: {tempDirectory}"), null);
                return ExitCodes.InvalidArguments;
            }

            // Create temp file path for intermediate data
            tempDataFile = Path.Combine(tempDirectory, $"ppds-migrate-{Guid.NewGuid():N}.zip");

            // Determine URLs for status messages
            var sourceDisplayUrl = sourceAuthResult.Url ?? "(from config)";
            var targetDisplayUrl = targetAuthResult.Url ?? "(from config)";

            // Build auth mode info for status messages
            var authModeInfo = sourceAuthResult.Mode switch
            {
                AuthMode.Interactive => " (interactive login)",
                AuthMode.Managed => " (managed identity)",
                _ => ""
            };

            // Phase 1: Export from source
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Phase 1: Connecting to source ({sourceDisplayUrl}){authModeInfo}..."
            });

            // Create service provider for source based on auth mode
            await using var sourceProvider = ServiceFactory.CreateProviderForAuthMode(
                sourceAuthResult.Mode, sourceAuthResult, configuration, sourceEnv, verbose, debug);
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
                Message = $"Phase 2: Connecting to target ({targetDisplayUrl}){authModeInfo}..."
            });

            // Create service provider for target based on auth mode
            await using var targetProvider = ServiceFactory.CreateProviderForAuthMode(
                targetAuthResult.Mode, targetAuthResult, configuration, targetEnv, verbose, debug);
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
