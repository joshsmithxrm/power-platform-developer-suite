using System.CommandLine;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Query;

public static class QueryCommandGroup
{
    public static Command Create()
    {
        var root = new Command("query", "Run queries against Dataverse.");
        root.Subcommands.Add(CreateRun());
        root.Subcommands.Add(CreateHidden());
        return root;
    }

    private static Command CreateRun()
    {
        var queryArg = new Argument<string>("query")
        {
            Description = "Query text or path to a .xml/.sql file."
        };

        var top = new Option<int>("--top", "-t")
        {
            Description = "Limit the number of rows returned."
        };

        return new Command("run", "Run a FetchXML or SQL query against Dataverse.")
        {
            queryArg,
            top,
        };
    }

    // This command is marked Hidden and must be excluded from generator output
    // (exercises the Hidden filter in CliReferenceGenerator).
    private static Command CreateHidden()
    {
        var cmd = new Command("hidden", "This command should never appear in generated docs.")
        {
            Hidden = true,
        };
        return cmd;
    }
}
