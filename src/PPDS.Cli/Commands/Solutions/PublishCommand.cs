using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// Publish all customizations (alias for ppds publish --all).
/// </summary>
public static class PublishCommand
{
    public static Command Create()
    {
        var command = new Command("publish", "Publish all customizations (alias for ppds publish --all)")
        {
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            // Delegate to top-level publish with --all
            return await PublishCommandGroup.ExecuteAsync(
                names: [],
                all: true,
                type: null,
                solution: null,
                profile: profile,
                environment: environment,
                globalOptions: globalOptions,
                cancellationToken: cancellationToken);
        });

        return command;
    }
}
