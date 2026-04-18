using System.ComponentModel;
using Spectre.Console.Cli;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Auth;

// Exercises the no-TSettings base (Command vs Command<T>) while keeping the
// description on [Description] so the MetadataLoadContext-based generator
// resolves it without invoking any code. The property-initializer /
// ctor-assign patterns documented in CliReferenceGenerator remarks are
// deliberately not exercised here.
[Description("Sign out of the fixture service.")]
public sealed class LogoutCommand : Command
{
    public override int Execute(CommandContext context) => 0;
}
