using System.CommandLine;
using PPDS.Dataverse.BulkOperations;

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
        var command = new Command("data", "Data operations: export, import, copy, analyze, schema, users, load");

        command.Subcommands.Add(ExportCommand.Create());
        command.Subcommands.Add(ImportCommand.Create());
        command.Subcommands.Add(CopyCommand.Create());
        command.Subcommands.Add(AnalyzeCommand.Create());
        command.Subcommands.Add(SchemaCommand.Create());
        command.Subcommands.Add(UsersCommand.Create());
        command.Subcommands.Add(LoadCommand.Create());

        return command;
    }

    /// <summary>
    /// Parses the --bypass-plugins option value to CustomLogicBypass enum.
    /// </summary>
    /// <param name="value">The option value: "sync", "async", "all", or null.</param>
    /// <returns>The corresponding CustomLogicBypass value.</returns>
    internal static CustomLogicBypass ParseBypassPlugins(string? value)
    {
        return value?.ToLowerInvariant() switch
        {
            "sync" => CustomLogicBypass.Synchronous,
            "async" => CustomLogicBypass.Asynchronous,
            "all" => CustomLogicBypass.All,
            _ => CustomLogicBypass.None
        };
    }
}
