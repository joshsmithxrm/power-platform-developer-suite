using System.ComponentModel;
using Spectre.Console.Cli;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Query;

[Description("Run a FetchXML or SQL query against Dataverse.")]
public sealed class RunCommand : Command<RunCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<query>")]
        [Description("Query text or path to a .xml/.sql file.")]
        public string Query { get; set; } = string.Empty;

        [CommandOption("-t|--top")]
        [Description("Limit the number of rows returned.")]
        [DefaultValue(50)]
        public int Top { get; set; } = 50;
    }

    public override int Execute(CommandContext context, Settings settings) => 0;
}
