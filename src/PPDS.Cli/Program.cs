using System.CommandLine;
using PPDS.Cli.Commands.Auth;
using PPDS.Cli.Commands.Backlog;
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
using PPDS.Cli.Commands.Session;
using PPDS.Cli.Commands.Solutions;
using PPDS.Cli.Commands.Users;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;

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
            return InteractiveCommand.LaunchTui();
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
        rootCommand.Subcommands.Add(BacklogCommandGroup.Create());
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
            rootCommand.Subcommands.Add(SessionCommandGroup.Create());
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
}
