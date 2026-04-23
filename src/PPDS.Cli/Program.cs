using System.CommandLine;
using PPDS.Cli.Commands.Auth;
using PPDS.Cli.Commands.Connections;
using PPDS.Cli.Commands.ConnectionReferences;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Commands.DeploymentSettings;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Commands.EnvironmentVariables;
using PPDS.Cli.Commands.Flows;
using PPDS.Cli.Commands.ImportJobs;
using PPDS.Cli.Commands.Internal;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Commands.PluginTraces;
using PPDS.Cli.Commands.CustomApis;
using PPDS.Cli.Commands.DataProviders;
using PPDS.Cli.Commands.DataSources;
using PPDS.Cli.Commands.ServiceEndpoints;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Roles;
using PPDS.Cli.Commands.Serve;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Commands.WebResources;
using PPDS.Cli.Commands.Solutions;
using PPDS.Cli.Commands.Users;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.UpdateCheck;
using PPDS.Cli.Commands.Logs;
using PPDS.Dataverse.Diagnostics;

namespace PPDS.Cli;

/// <summary>
/// Entry point for the ppds CLI tool.
/// </summary>
public static class Program
{
    /// <summary>
    /// Arguments that should skip the version header (help/version output).
    /// </summary>
    private static readonly HashSet<string> SkipVersionHeaderArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "--help", "-h", "-?", "--version", "version"
    };

    public static async Task<int> Main(string[] args)
    {
        // Seed the ambient correlation-id scope before any command runs. Every log line emitted
        // by the CLI, RPC handlers invoked in-process, and Dataverse services will pick this up
        // via CorrelationIdScope.Current. The scope lives for the process lifetime; RPC calls
        // can push nested scopes per method invocation.
        using var correlationScope = CorrelationIdScope.Push(CorrelationIdScope.NewId());

        // No arguments = launch TUI directly (first-class experience)
        if (args.Length == 0)
        {
            return InteractiveCommand.LaunchTui();
        }

        // Write version header for diagnostic context (skip for help/version/interactive)
        if (!args.Any(a => SkipVersionHeaderArgs.Contains(a)) && !IsInteractiveMode(args))
        {
            ErrorOutput.WriteVersionHeader();
            ReadAndDeleteUpdateStatus();

            // Show cached update notification (guarded by --quiet)
            if (StartupUpdateNotifier.ShouldShow(args))
            {
                var updateService = new UpdateCheckService();
                var cached = updateService.GetCachedResult();
                var updateMessage = StartupUpdateNotifier.FormatNotification(cached);
                if (updateMessage != null)
                {
                    Console.Error.WriteLine(updateMessage);
                }

                // Fire-and-forget background cache refresh for next startup
                updateService.RefreshCacheInBackgroundIfStale(ErrorOutput.Version);
            }
        }

        var rootCommand = new RootCommand(
            "PPDS CLI - Power Platform Developer Suite command-line tool" + Environment.NewLine +
            Environment.NewLine +
            $"Documentation: {DocsCommand.DocsUrl}");

        // Add command groups
        rootCommand.Subcommands.Add(AuthCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.CreateOrgAlias()); // 'org' alias for 'env'
        rootCommand.Subcommands.Add(DataCommandGroup.Create());
        rootCommand.Subcommands.Add(PluginsCommandGroup.Create());
        rootCommand.Subcommands.Add(PluginTracesCommandGroup.Create());
        rootCommand.Subcommands.Add(ServiceEndpointsCommandGroup.Create());
        rootCommand.Subcommands.Add(CustomApisCommandGroup.Create());
        rootCommand.Subcommands.Add(DataProvidersCommandGroup.Create());
        rootCommand.Subcommands.Add(DataSourcesCommandGroup.Create());
        rootCommand.Subcommands.Add(MetadataCommandGroup.Create());
        rootCommand.Subcommands.Add(QueryCommandGroup.Create());
        rootCommand.Subcommands.Add(SolutionsCommandGroup.Create());
        rootCommand.Subcommands.Add(ImportJobsCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvironmentVariablesCommandGroup.Create());
        rootCommand.Subcommands.Add(FlowsCommandGroup.Create());
        rootCommand.Subcommands.Add(ConnectionsCommandGroup.Create());
        rootCommand.Subcommands.Add(ConnectionReferencesCommandGroup.Create());
        rootCommand.Subcommands.Add(DeploymentSettingsCommandGroup.Create());
        rootCommand.Subcommands.Add(UsersCommandGroup.Create());
        rootCommand.Subcommands.Add(RolesCommandGroup.Create());
        rootCommand.Subcommands.Add(WebResourcesCommandGroup.Create());
        rootCommand.Subcommands.Add(PublishCommandGroup.Create());
        rootCommand.Subcommands.Add(ServeCommand.Create());
        rootCommand.Subcommands.Add(DocsCommand.Create());
        rootCommand.Subcommands.Add(VersionCommand.Create());
        rootCommand.Subcommands.Add(LogsCommandGroup.Create());
        rootCommand.Subcommands.Add(InteractiveCommand.Create());

        // Internal/debug commands - only visible when PPDS_INTERNAL=1
        if (Environment.GetEnvironmentVariable("PPDS_INTERNAL") == "1")
        {
            rootCommand.Subcommands.Add(InternalCommandGroup.Create());
        }

        // Prepend [Required] to required option descriptions for scannability
        HelpCustomization.ApplyRequiredOptionStyle(rootCommand);

        // Note: System.CommandLine handles Ctrl+C automatically and passes the
        // CancellationToken to command handlers via SetAction's cancellationToken parameter.
        // No manual CancelKeyPress handler is needed.

        // ── Invoke / catch section ──────────────────────────────────────────────────────
        // Parse is included in the try so that any unexpected exception during arg-binding
        // (e.g. a type-converter that throws) is caught and rendered as a clean one-liner on
        // stderr rather than a raw stack trace.  System.CommandLine validation errors (e.g.
        // invalid GUID) are surfaced via parseResult.Errors and handled by InvokeAsync's
        // built-in error renderer; we only catch exceptions that leak past that mechanism.
        try
        {
            var parseResult = rootCommand.Parse(args);
            return await parseResult.InvokeAsync();
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var debug = args.Any(a => a == "--debug");
            var (error, exitCode) = ExceptionMapper.MapWithExitCode(ex, debug: debug);

            // Never expose stack traces unless --debug is set.  The build-path in stack frames
            // (e.g. D:\a\power-platform-developer-suite\...) leaks CI internals and confuses users.
            if (debug)
            {
                Console.Error.WriteLine(ex.ToString());
            }
            else
            {
                Console.Error.WriteLine($"Error: {error.Message}");
                if (error.Details != null)
                    Console.Error.WriteLine(error.Details);
            }

            return exitCode;
        }
        // ────────────────────────────────────────────────────────────────────────────────
    }

    /// <summary>
    /// Reads the post-update status file written by the detached wrapper script,
    /// displays the result to the user, and deletes the file.
    /// </summary>
    private static void ReadAndDeleteUpdateStatus()
    {
        try
        {
            var statusPath = Path.Combine(
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                ".ppds", "update-status.json");

            if (!File.Exists(statusPath))
                return;

            var json = File.ReadAllText(statusPath);
            File.Delete(statusPath);

            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
            var version = root.TryGetProperty("targetVersion", out var v) ? v.GetString() : null;

            if (success && version is not null)
            {
                Console.Error.WriteLine($"Successfully updated to {version}.");
            }
            else
            {
                Console.Error.WriteLine("Update failed. Run manually: dotnet tool update PPDS.Cli -g");
            }
        }
        catch
        {
            // Status file read/delete failure is not fatal
        }
    }

    /// <summary>
    /// Determines if the CLI should run in interactive mode (for version header skip).
    /// </summary>
    /// <param name="args">Command line arguments.</param>
    /// <returns>True if interactive mode was requested.</returns>
    private static bool IsInteractiveMode(string[] args)
    {
        if (args.Length == 0)
            return false;

        var firstArg = args[0].ToLowerInvariant();
        return firstArg == "interactive";
    }
}
