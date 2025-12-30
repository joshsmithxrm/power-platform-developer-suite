using System.CommandLine;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Auth;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Commands.Env;
using PPDS.Cli.Commands.Plugins;
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
        rootCommand.Subcommands.Add(SchemaCommand.Create());
        rootCommand.Subcommands.Add(UsersCommand.Create());

        // Prepend [Required] to required option descriptions for scannability
        HelpCustomization.ApplyRequiredOptionStyle(rootCommand);

        // Handle cancellation
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("\nCancellation requested. Waiting for current operation to complete...");
        };

        var parseResult = rootCommand.Parse(args);
        return await parseResult.InvokeAsync();
    }
}
