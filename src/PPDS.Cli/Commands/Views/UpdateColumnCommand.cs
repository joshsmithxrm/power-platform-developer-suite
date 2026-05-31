using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Update the width of an existing column in a view.
/// </summary>
public static class UpdateColumnCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name"
        };

        var columnOption = new Option<string>("--column", "-c")
        {
            Description = "[Required] Attribute name of the column to update"
        };

        var widthOption = new Option<int>("--width")
        {
            Description = "[Required] New column width in pixels (positive integer)"
        };

        var command = new Command("update-column", "Update the width of an existing column in a view")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            columnOption,
            widthOption,
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
            var width = parseResult.GetValue(widthOption);
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            if (width <= 0)
            {
                writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidValue,
                    "--width must be a positive integer."));
                return ExitCodes.InvalidArguments;
            }

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
                await service.UpdateColumnAsync(entity, viewName, column, width,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Column '{column}' updated in view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"Column '{column}' updated in view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "updating column", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
