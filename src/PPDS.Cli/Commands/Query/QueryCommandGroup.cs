using System.CommandLine;
using PPDS.Cli.Commands.Query.History;

namespace PPDS.Cli.Commands.Query;

/// <summary>
/// Query command group for executing FetchXML and SQL queries against Dataverse.
/// </summary>
public static class QueryCommandGroup
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
    public static readonly Option<string?> EnvironmentOption = new("--environment", "-env")
    {
        Description = "Override the environment URL. Takes precedence over profile's bound environment."
    };

    /// <summary>
    /// Top/limit option for limiting results.
    /// </summary>
    public static readonly Option<int?> TopOption = new("--top", "-t")
    {
        Description = "Limit the number of results returned"
    };

    /// <summary>
    /// Page number option for paging.
    /// </summary>
    public static readonly Option<int?> PageOption = new("--page")
    {
        Description = "Page number (1-based) for paged results"
    };

    /// <summary>
    /// Paging cookie option for continuation.
    /// </summary>
    public static readonly Option<string?> PagingCookieOption = new("--paging-cookie")
    {
        Description = "Paging cookie from previous query for continuation"
    };

    /// <summary>
    /// Count option to include total record count.
    /// </summary>
    public static readonly Option<bool> CountOption = new("--count", "-c")
    {
        Description = "Include total record count in results"
    };

    /// <summary>
    /// Creates the 'query' command group with all subcommands.
    /// </summary>
    public static Command Create()
    {
        var command = new Command("query", "Execute FetchXML and SQL queries against Dataverse");

        command.Subcommands.Add(FetchCommand.Create());
        command.Subcommands.Add(SqlCommand.Create());
        command.Subcommands.Add(HistoryCommandGroup.Create());

        return command;
    }
}
