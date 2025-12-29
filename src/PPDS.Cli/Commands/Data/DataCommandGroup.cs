using System.CommandLine;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Data command group for export, import, copy, and analyze operations.
/// </summary>
public static class DataCommandGroup
{
    /// <summary>
    /// Profile option for specifying which authentication profile(s) to use.
    /// Supports comma-separated names for pooling: --profile app1,app2,app3
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Profile name(s). For high-throughput pooling, specify multiple Application User profiles comma-separated (e.g., app1,app2,app3) - each profile multiplies API quota."
    };

    /// <summary>
    /// Environment option for overriding the profile's bound environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-env")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'data' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("data", "Data operations: export, import, copy, analyze");

        command.Subcommands.Add(ExportCommand.Create());
        command.Subcommands.Add(ImportCommand.Create());
        command.Subcommands.Add(CopyCommand.Create());
        command.Subcommands.Add(AnalyzeCommand.Create());

        return command;
    }
}
