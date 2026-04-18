using System.ComponentModel;
using Spectre.Console.Cli;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Auth;

[Description("Sign in to the fixture service.")]
public sealed class LoginCommand : Command<LoginCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<username>")]
        [Description("Username to sign in as.")]
        public string Username { get; set; } = string.Empty;

        [CommandOption("-t|--tenant")]
        [Description("Tenant identifier, if your account belongs to multiple tenants.")]
        public string? Tenant { get; set; }

        [CommandOption("--force")]
        [Description("Skip the confirmation prompt.")]
        [DefaultValue(false)]
        public bool Force { get; set; }
    }

    public override int Execute(CommandContext context, Settings settings) => 0;
}
