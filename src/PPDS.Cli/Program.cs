using System.CommandLine;
using PPDS.Cli.Commands.Auth;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Commands.Metadata;
using PPDS.Cli.Commands.Plugins;
using PPDS.Cli.Commands.Query;
using PPDS.Cli.Commands.Internal;
using PPDS.Cli.Commands.Serve;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli;

/// <summary>
/// Entry point for the ppds CLI tool.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PPDS CLI - Power Platform Developer Suite command-line tool");

        // Add command groups
        rootCommand.Subcommands.Add(AuthCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.CreateOrgAlias()); // 'org' alias for 'env'
        rootCommand.Subcommands.Add(DataCommandGroup.Create());
        rootCommand.Subcommands.Add(PluginsCommandGroup.Create());
        rootCommand.Subcommands.Add(MetadataCommandGroup.Create());
        rootCommand.Subcommands.Add(QueryCommandGroup.Create());
        rootCommand.Subcommands.Add(ServeCommand.Create());

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
}
