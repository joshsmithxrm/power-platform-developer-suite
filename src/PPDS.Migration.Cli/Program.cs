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
        Description = "Authentication mode: interactive (default), config, env, managed",
        DefaultValueFactory = _ => AuthMode.Interactive,
        Recursive = true
    };

    /// <summary>
    /// Global option for User Secrets ID (cross-process secret sharing).
    /// </summary>
    public static readonly Option<string?> SecretsIdOption = new("--secrets-id")
    {
        Description = "User Secrets ID for cross-process secret sharing (used with --auth config)",
        Recursive = true
    };

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PPDS Migration CLI - High-performance Dataverse data migration tool");

        // Add global options (Recursive = true makes it available to all subcommands)
        rootCommand.Options.Add(UrlOption);
        rootCommand.Options.Add(AuthOption);
        rootCommand.Options.Add(SecretsIdOption);

        // Add subcommands
        rootCommand.Subcommands.Add(ExportCommand.Create());
        rootCommand.Subcommands.Add(ImportCommand.Create());
        rootCommand.Subcommands.Add(AnalyzeCommand.Create());
        rootCommand.Subcommands.Add(MigrateCommand.Create());
        rootCommand.Subcommands.Add(SchemaCommand.Create());
        rootCommand.Subcommands.Add(ConfigCommand.Create());

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
