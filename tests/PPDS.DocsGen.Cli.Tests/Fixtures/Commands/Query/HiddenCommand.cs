using System.ComponentModel;
using Spectre.Console.Cli;

namespace PPDS.DocsGen.Cli.Tests.FixtureCli.Commands.Query;

/// <summary>
/// Intentionally hidden — used to verify SkipsEditorBrowsableNeverCommands.
/// This type has a [Description] attribute and is otherwise indistinguishable
/// from a real command, so the only reason it should be absent from the
/// generator output is the [EditorBrowsable(Never)] guard.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
[Description("This command should never appear in generated docs.")]
public sealed class HiddenCommand : Command
{
    public override int Execute(CommandContext context) => 0;
}
