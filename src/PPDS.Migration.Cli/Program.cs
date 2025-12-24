using System.CommandLine;
using PPDS.Migration.Cli.Commands;

namespace PPDS.Migration.Cli;

/// <summary>
/// Entry point for the ppds-migrate CLI tool.
/// </summary>
public static class Program
{
    /// <summary>
    /// Global option for User Secrets ID (cross-process secret sharing).
    /// </summary>
    public static readonly Option<string?> SecretsIdOption = new(
        name: "--secrets-id",
        description: "User Secrets ID for cross-process secret sharing (e.g., from calling application's UserSecretsId)");

    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PPDS Migration CLI - High-performance Dataverse data migration tool")
        {
            Name = "ppds-migrate"
        };

        // Add global options
        rootCommand.AddGlobalOption(SecretsIdOption);

        // Add subcommands
        rootCommand.AddCommand(ExportCommand.Create());
        rootCommand.AddCommand(ImportCommand.Create());
        rootCommand.AddCommand(AnalyzeCommand.Create());
        rootCommand.AddCommand(MigrateCommand.Create());
        rootCommand.AddCommand(SchemaCommand.Create());
        rootCommand.AddCommand(ConfigCommand.Create());

        // Handle cancellation
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
            Console.Error.WriteLine("\nCancellation requested. Waiting for current operation to complete...");
        };

        return await rootCommand.InvokeAsync(args);
    }
}
