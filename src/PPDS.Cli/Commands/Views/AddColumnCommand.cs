using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Add one or more columns to a view.
/// </summary>
public static class AddColumnCommand
{
    public static Command Create()
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name or ID",
            Required = true
        };

        var columnOption = new Option<string[]>("--column", "-c")
        {
            Description = "[Required] Column to add. Format: 'attributename' or 'attributename:width' (default width 150). Repeatable.",
            AllowMultipleArgumentsPerToken = false,
            Required = true
        };
        columnOption.Arity = ArgumentArity.OneOrMore;

        var viaRelationshipOption = new Option<string?>("--via-relationship")
        {
            Description = "Lookup/relationship attribute to use for related-entity columns"
        };

        var command = new Command("add-column", "Add one or more columns to a view")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            columnOption,
            viaRelationshipOption,
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
            var columnSpecs = parseResult.GetValue(columnOption) ?? [];
            var viaRelationship = parseResult.GetValue(viaRelationshipOption);
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            var columns = new List<ColumnSpec>();
            foreach (var spec in columnSpecs)
            {
                var parts = spec.Split(':', 2);
                if (parts.Length == 2)
                {
                    if (!int.TryParse(parts[1], out var width) || width <= 0)
                    {
                        writer.WriteError(new StructuredError(ErrorCodes.Validation.InvalidValue,
                            $"Invalid column spec '{spec}': width must be a positive integer."));
                        return ExitCodes.InvalidArguments;
                    }
                    columns.Add(new ColumnSpec(parts[0], width));
                }
                else
                {
                    columns.Add(new ColumnSpec(parts[0]));
                }
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
                await service.AddColumnAsync(entity, viewName, columns, viaRelationship,
                    publish, solution, cancellationToken: cancellationToken);

                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Column(s) added to view '{viewName}'.");
                else
                    writer.WriteSuccess(new { message = $"Column(s) added to view '{viewName}'." });

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                var error = ExceptionMapper.Map(ex, context: "adding column", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
