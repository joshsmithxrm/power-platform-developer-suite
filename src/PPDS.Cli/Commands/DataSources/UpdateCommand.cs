using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// Update command for data sources.
/// Note: entitydatasource has no mutable attributes (only a name, which is the logical name
/// and cannot be changed after creation). This command exists for CLI surface completeness
/// but always returns an informational error.
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Data source logical name or GUID"
        };

        var command = new Command("update", "Update a data source (no mutable attributes; see help)")
        {
            nameOrIdArgument,
            DataSourcesCommandGroup.ProfileOption,
            DataSourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction((parseResult, _) =>
        {
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "Data sources have no mutable attributes. The logical name (entitydatasource) is " +
                "assigned at creation time and cannot be changed. To rename, unregister and re-register.",
                Target: parseResult.GetValue(nameOrIdArgument)));

            return Task.FromResult(ExitCodes.InvalidArguments);
        });

        return command;
    }
}
