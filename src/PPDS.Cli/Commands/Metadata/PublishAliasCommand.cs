using System.CommandLine;
using PPDS.Cli.Commands.Publish;
using PPDS.Cli.Infrastructure;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Alias for 'ppds publish --type entity'. Auto-injects --type.
/// </summary>
public static class PublishAliasCommand
{
    public static Command Create()
    {
        var namesArgument = new Argument<string[]>("names")
        {
            Description = "Entity logical names to publish",
            Arity = ArgumentArity.ZeroOrMore
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Publish all entities in this solution"
        };

        var command = new Command("publish", "Publish entity metadata (alias for ppds publish --type entity)")
        {
            namesArgument,
            solutionOption,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var names = parseResult.GetValue(namesArgument) ?? [];
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            // Delegate to top-level publish with --type entity injected
            return await PublishCommandGroup.ExecuteAsync(
                names,
                all: false,
                type: "entity",
                solution: solution,
                profile: profile,
                environment: environment,
                globalOptions: globalOptions,
                cancellationToken: cancellationToken);
        });

        return command;
    }
}
