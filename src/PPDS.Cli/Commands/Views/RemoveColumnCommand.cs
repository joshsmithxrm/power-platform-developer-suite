using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Remove a column from a view.
/// </summary>
public static class RemoveColumnCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name"
        };

        var columnOption = new Option<string>("--column", "-c")
        {
            Description = "[Required] Attribute name of the column to remove"
        };

        var command = new Command("remove-column", "Remove a column from a view by attribute name")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            columnOption,
            ViewsCommandGroup.SolutionOption,
            ViewsCommandGroup.PublishOption,
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ViewsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ViewsCommandGroup.EnvironmentOption);
            var entity = parseResult.GetValue(ViewsCommandGroup.EntityOption)!;
            var viewName = parseResult.GetValue(viewOption)!;
            var column = parseResult.GetValue(columnOption)!;
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            try
            {
                await using var sp = await ProfileServiceFactory.CreateFromProfilesAsync(
                    profile, environment,
                    globalOptions.Verbose, globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback,
                    cancellationToken);

                if (!globalOptions.IsJsonMode)
                {
                    ConsoleHeader.WriteConnectedAs(sp.GetRequiredService<ResolvedConnectionInfo>());
                    Console.Error.WriteLine();
                }

                var service = sp.GetRequiredService<IViewService>();
                await service.RemoveColumnAsync(entity, viewName, column,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Column '{column}' removed from view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"Column '{column}' removed from view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "removing column", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
