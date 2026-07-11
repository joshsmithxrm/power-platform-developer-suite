using System.CommandLine;

namespace PPDS.Cli.Commands.ImportJobs;

/// <summary>
/// ImportJobs command group for monitoring solution import jobs.
/// </summary>
public static class ImportJobsCommandGroup
{
    /// <summary>
    /// Profile option for authentication.
    /// </summary>
    public static readonly Option<string?> ProfileOption = new("--profile", "-p")
    {
        Description = "Authentication profile name"
    };

    /// <summary>
    /// Environment option for target environment.
    /// </summary>
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-e")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Creates the 'import-jobs' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("import-jobs", "Monitor solution import jobs: list, get, data, wait");
        command.Aliases.Add("importjobs"); // deprecated pre-#1246 name; see Infrastructure/CommandAliasDeprecation.cs

        command.Subcommands.Add(ListCommand.Create());
        command.Subcommands.Add(GetCommand.Create());
        command.Subcommands.Add(DataCommand.Create());
        command.Subcommands.Add(WaitCommand.Create());
        command.Subcommands.Add(UrlCommand.Create());

        return command;
    }
}
