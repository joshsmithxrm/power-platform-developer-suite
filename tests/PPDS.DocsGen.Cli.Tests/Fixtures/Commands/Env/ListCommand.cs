using System.ComponentModel;
using Spectre.Console.Cli;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Env;

[Description("List known environments.")]
public sealed class ListCommand : Command<ListCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-f|--format")]
        [Description("Output format (text or json).")]
        [DefaultValue("text")]
        public string Format { get; set; } = "text";
    }

    public override int Execute(CommandContext context, Settings settings) => 0;
}
