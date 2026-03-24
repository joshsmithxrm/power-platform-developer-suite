using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Alias for 'ppds publish --type webresource'. Auto-injects --type.
/// </summary>
public static class PublishAliasCommand
{
    public static Command Create()
    {
        var namesArgument = new Argument<string[]>("names")
        {
            Description = "Web resource names, partial names, or GUIDs to publish",
            Arity = ArgumentArity.ZeroOrMore
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Publish all web resources in this solution"
        };

        var command = new Command("publish", "Publish web resources (alias for ppds publish --type webresource)")
        {
            namesArgument,
            solutionOption,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var names = parseResult.GetValue(namesArgument) ?? [];
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            // Delegate to top-level publish with --type webresource injected
            return await PublishCommandGroup.ExecuteAsync(
                names,
                all: false,
                type: "webresource",
                solution: solution,
                profile: profile,
                environment: environment,
                globalOptions: globalOptions,
                cancellationToken: cancellationToken);
        });

        return command;
    }
}
