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
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Roles;
using PPDS.Cli.Commands.Serve;
using PPDS.Cli.Commands.Solutions;
using PPDS.Cli.Commands.Users;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Tui;
using Spectre.Console;

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
        "--help", "-h", "-?", "--version"
    };

    public static async Task<int> Main(string[] args)
    {
        // No arguments = launch TUI directly (first-class experience)
        if (args.Length == 0)
        {
            return LaunchTui();
        }

        // Write version header for diagnostic context (skip for help/version/interactive)
        if (!args.Any(a => SkipVersionHeaderArgs.Contains(a)) && !IsInteractiveMode(args))
        {
            ErrorOutput.WriteVersionHeader();
        }

        var rootCommand = new RootCommand(
            "PPDS CLI - Power Platform Developer Suite command-line tool" + Environment.NewLine +
            Environment.NewLine +
            "Documentation: https://github.com/joshsmithxrm/power-platform-developer-suite/blob/main/src/PPDS.Cli/README.md");

        // Add command groups
        rootCommand.Subcommands.Add(AuthCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.CreateOrgAlias()); // 'org' alias for 'env'
        rootCommand.Subcommands.Add(DataCommandGroup.Create());
        rootCommand.Subcommands.Add(PluginsCommandGroup.Create());
        rootCommand.Subcommands.Add(PluginTracesCommandGroup.Create());
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
        rootCommand.Subcommands.Add(ServeCommand.Create());
        rootCommand.Subcommands.Add(DocsCommand.Create());
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

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
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

    /// <summary>
    /// Launches the Terminal.Gui TUI application.
    /// </summary>
    /// <returns>Exit code (0 for success).</returns>
    private static int LaunchTui()
    {
        // Check if we're in a TTY environment
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            Console.Error.WriteLine("Error: Interactive mode requires a terminal (TTY).");
            Console.Error.WriteLine("This may occur in CI/CD pipelines, redirected input, or non-interactive shells.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Use standard CLI commands instead:");
            Console.Error.WriteLine("  ppds auth list       - List profiles");
            Console.Error.WriteLine("  ppds auth select     - Select a profile");
            Console.Error.WriteLine("  ppds env list        - List environments");
            Console.Error.WriteLine("  ppds env select      - Select an environment");
            Console.Error.WriteLine();
            Console.Error.WriteLine("Or run 'ppds --help' for full command list.");
            return ExitCodes.Failure;
        }

        try
        {
            using var app = new PpdsApplication(
                profileName: null, // Uses active profile
                deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback);

            return app.Run(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // User cancelled - not an error
            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Interactive mode error: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
