using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Set the authoritative column order for a view.
/// </summary>
public static class ReorderColumnsCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name"
        };

        var columnsOption = new Option<string>("--columns")
        {
            Description = "[Required] Comma-separated list of attribute names in desired order. Columns not in the list are dropped."
        };

        var command = new Command("reorder-columns",
            "Set the authoritative column order for a view. Columns not listed are dropped.")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            columnsOption,
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
            var columnsRaw = parseResult.GetValue(columnsOption)!;
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            var ordered = columnsRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

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
                await service.ReorderColumnsAsync(entity, viewName, ordered,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Columns reordered in view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"Columns reordered in view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "reordering columns", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
