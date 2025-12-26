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
        var schemaOption = new Option<FileInfo>("--schema", "-s")
        {
            Description = "Path to schema.xml file",
            Required = true
        }.AcceptExistingOnly();

        var sourceUrlOption = new Option<string>("--source-url")
        {
            Description = "Source Dataverse environment URL",
            Required = true
        };

        var targetUrlOption = new Option<string>("--target-url")
        {
            Description = "Target Dataverse environment URL",
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

        var command = new Command("migrate", "Migrate data from source to target Dataverse environment")
        {
            schemaOption,
            sourceUrlOption,
            targetUrlOption,
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
            var sourceUrl = parseResult.GetValue(sourceUrlOption)!;
            var targetUrl = parseResult.GetValue(targetUrlOption)!;
            var authMode = parseResult.GetValue(Program.AuthOption);
            var tempDir = parseResult.GetValue(tempDirOption);
            var bypassPlugins = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            // Migrate only supports interactive and managed auth
            // (env auth has only one set of credentials, can't work with two environments)
            if (authMode == AuthMode.Env)
            {
                ConsoleOutput.WriteError(
                    "--auth env is not supported for migrate command because it uses a single credential. " +
                    "Use --auth interactive (default) or --auth managed instead. " +
                    "For service principal auth with two environments, use 'export' then 'import' separately.",
                    json);
                return ExitCodes.InvalidArguments;
            }

            // Create auth results for both environments
            var sourceAuth = new AuthResolver.AuthResult(authMode, sourceUrl);
            var targetAuth = new AuthResolver.AuthResult(authMode, targetUrl);

            return await ExecuteAsync(
                sourceAuth, targetAuth, schema, tempDir, bypassPlugins, bypassFlows,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        AuthResolver.AuthResult sourceAuth,
        AuthResolver.AuthResult targetAuth,
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
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

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
            tempDataFile = Path.Combine(tempDirectory, $"ppds-migrate-{Guid.NewGuid():N}.zip");

            // Build auth mode info for status messages
            var authModeInfo = sourceAuth.Mode switch
            {
                AuthMode.Interactive => " (interactive login)",
                AuthMode.Managed => " (managed identity)",
                _ => ""
            };

            // Phase 1: Export from source
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Phase 1: Connecting to source ({sourceAuth.Url}){authModeInfo}..."
            });

            await using var sourceProvider = ServiceFactory.CreateProviderForAuthMode(sourceAuth, verbose, debug);
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
                Message = $"Phase 2: Connecting to target ({targetAuth.Url}){authModeInfo}..."
            });

            await using var targetProvider = ServiceFactory.CreateProviderForAuthMode(targetAuth, verbose, debug);
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
