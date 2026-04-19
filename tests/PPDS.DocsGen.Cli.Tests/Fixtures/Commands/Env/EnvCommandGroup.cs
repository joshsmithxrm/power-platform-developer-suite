using System.CommandLine;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Env;

public static class EnvCommandGroup
{
    public static Command Create()
    {
        var root = new Command("env", "Environment management commands.");
        root.Subcommands.Add(CreateList());
        root.Subcommands.Add(CreateSelect());
        return root;
    }

    private static Command CreateList()
    {
        var format = new Option<string>("--format", "-f")
        {
            Description = "Output format (text or json)."
        };

        return new Command("list", "List known environments.") { format };
    }

    private static Command CreateSelect()
    {
        var target = new Argument<string>("target")
        {
            Description = "Environment identifier — name, URL, or unique name."
        };

        return new Command("select", "Select the active environment by name or URL.") { target };
    }
}
