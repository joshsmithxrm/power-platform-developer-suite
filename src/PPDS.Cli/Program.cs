using System.CommandLine;
using PPDS.Cli.Commands;
using PPDS.Cli.Commands.Auth;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Commands.Env;

namespace PPDS.Cli;

/// <summary>
/// Entry point for the ppds CLI tool.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PPDS CLI - Unified Power Platform Developer Suite command-line tool");

        // Add command groups
        rootCommand.Subcommands.Add(AuthCommandGroup.Create());
        rootCommand.Subcommands.Add(EnvCommandGroup.Create());
        rootCommand.Subcommands.Add(DataCommandGroup.Create());
        rootCommand.Subcommands.Add(SchemaCommand.Create());
        rootCommand.Subcommands.Add(UsersCommand.Create());

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
