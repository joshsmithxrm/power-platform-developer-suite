using System.CommandLine;
using PPDS.Migration.Cli.Commands;
using PPDS.Migration.Cli.Infrastructure;

namespace PPDS.Migration.Cli;

/// <summary>
/// Entry point for the ppds-migrate CLI tool.
/// </summary>
public static class Program
{
    /// <summary>
    /// Global option for Dataverse environment URL.
    /// Required for interactive and managed auth modes.
    /// </summary>
    public static readonly Option<string?> UrlOption = new("--url")
    {
        Description = "Dataverse environment URL (e.g., https://org.crm.dynamics.com)",
        Recursive = true
    };

    /// <summary>
    /// Global option for authentication mode.
    /// </summary>
    public static readonly Option<AuthMode> AuthOption = new("--auth")
    {
        Description = "Authentication mode: interactive (default), env, managed",
        DefaultValueFactory = _ => AuthMode.Interactive,
        Recursive = true
    };

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PPDS Migration CLI - High-performance Dataverse data migration tool");

        // Add global options (Recursive = true makes them available to all subcommands)
        rootCommand.Options.Add(UrlOption);
        rootCommand.Options.Add(AuthOption);

        // Add subcommands
        rootCommand.Subcommands.Add(ExportCommand.Create());
        rootCommand.Subcommands.Add(ImportCommand.Create());
        rootCommand.Subcommands.Add(AnalyzeCommand.Create());
        rootCommand.Subcommands.Add(MigrateCommand.Create());
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
