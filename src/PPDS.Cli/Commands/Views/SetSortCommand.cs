using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Set the sort order for a view.
/// </summary>
public static class SetSortCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name",
            Required = true
        };

        var sortOption = new Option<string[]>("--sort")
        {
            Description = "[Required] Sort specification: 'attributename:asc' or 'attributename:desc'. Repeatable; applied in declaration order (first = primary).",
            AllowMultipleArgumentsPerToken = false,
            Required = true
        };
        sortOption.Arity = ArgumentArity.OneOrMore;

        var command = new Command("set-sort", "Set the sort order for a view. Multiple --sort flags applied in declaration order.")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            sortOption,
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
            var sortSpecs = parseResult.GetValue(sortOption) ?? [];
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            var sorts = new List<ViewSortOrder>();
            foreach (var spec in sortSpecs)
            {
                var parts = spec.Split(':', 2);
                if (parts.Length != 2 || (parts[1] != "asc" && parts[1] != "desc"))
                {
                    writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidValue,
                        $"Invalid sort spec '{spec}'. Expected format: 'attributename:asc' or 'attributename:desc'."));
                    return ExitCodes.InvalidArguments;
                }
                sorts.Add(new ViewSortOrder(parts[0], parts[1] == "desc"));
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
                await service.SetSortAsync(entity, viewName, sorts,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Sort order set for view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"Sort order set for view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "setting sort", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
