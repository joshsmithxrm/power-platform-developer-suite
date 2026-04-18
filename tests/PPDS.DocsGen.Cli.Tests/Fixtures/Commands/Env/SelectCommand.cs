using System.ComponentModel;
using Spectre.Console.Cli;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Env;

[Description("Select the active environment by name or URL.")]
public sealed class SelectCommand : AsyncCommand<SelectCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Environment identifier — name, URL, or unique name.")]
        public string Target { get; set; } = string.Empty;
    }

    public override Task<int> ExecuteAsync(CommandContext context, Settings settings) => Task.FromResult(0);
}
