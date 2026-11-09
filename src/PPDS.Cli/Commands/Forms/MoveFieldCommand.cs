using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.Forms;

public static class MoveFieldCommand
{
    public static Command Create() => Create(null);
    internal static Command Create(Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
        => FieldLayoutCommand.Create(move: true, providerFactory);
}
