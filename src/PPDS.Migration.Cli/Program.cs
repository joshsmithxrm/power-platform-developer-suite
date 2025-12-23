using System.CommandLine;
using PPDS.Migration.Cli.Commands;

namespace PPDS.Migration.Cli;

/// <summary>
/// Entry point for the ppds-migrate CLI tool.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("PPDS Migration CLI - High-performance Dataverse data migration tool")
        {
            Name = "ppds-migrate"
        };

        // Add subcommands
        rootCommand.AddCommand(ExportCommand.Create());
        rootCommand.AddCommand(ImportCommand.Create());
        rootCommand.AddCommand(AnalyzeCommand.Create());
        rootCommand.AddCommand(MigrateCommand.Create());
        rootCommand.AddCommand(SchemaCommand.Create());

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
