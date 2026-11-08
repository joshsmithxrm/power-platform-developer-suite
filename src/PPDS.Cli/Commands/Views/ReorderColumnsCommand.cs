using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Views;

namespace PPDS.Cli.Commands.Views;

/// <summary>
/// Reorder every existing column in a view without removing columns.
/// </summary>
public static class ReorderColumnsCommand
{
    public static Command Create() => Create(null);

    internal static Command Create(
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
    {
        var viewOption = new Option<string>("--view", "-v")
        {
            Description = "[Required] View name or ID",
            Required = true
        };

        var columnsOption = new Option<string>("--columns")
        {
            Description = "[Required] Comma-separated list of every existing column exactly once, in desired order (case-insensitive).",
            Required = true
        };

        var command = new Command("reorder-columns",
            "Reorder every existing column exactly once. Use remove-column to remove a column.")
        {
            ViewsCommandGroup.ProfileOption,
            ViewsCommandGroup.EnvironmentOption,
            ViewsCommandGroup.ConfirmEnvironmentOption,
            ViewsCommandGroup.EntityOption,
            viewOption,
            columnsOption,
            ViewsCommandGroup.SolutionOption,
            ViewsCommandGroup.PublishOption,
        };

        GlobalOptions.AddToCommand(command);

        command.Validators.Add(result =>
        {
            var columns = result.GetValue(columnsOption);
            if (columns == null) return; // Required-option validation reports a missing value.
            try
            {
                ViewService.ValidateColumnOrder(columns.Split(','));
            }
            catch (PpdsException ex)
            {
                result.AddError(ex.UserMessage);
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ViewsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ViewsCommandGroup.EnvironmentOption);
            var confirmEnvironment = parseResult.GetValue(ViewsCommandGroup.ConfirmEnvironmentOption);
            var entity = parseResult.GetValue(ViewsCommandGroup.EntityOption)!;
            var viewName = parseResult.GetValue(viewOption)!;
            var columnsRaw = parseResult.GetValue(columnsOption)!;
            var solution = parseResult.GetValue(ViewsCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ViewsCommandGroup.PublishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            var writer = ServiceFactory.CreateOutputWriter(globalOptions);

            var ordered = columnsRaw
                .Split(',', StringSplitOptions.TrimEntries)
                .ToList();

            try
            {
                await using var sp = providerFactory is null
                    ? await ProfileServiceFactory.CreateFromProfilesAsync(
                        profile, environment,
                        globalOptions.Verbose, globalOptions.Debug,
                        ProfileServiceFactory.DefaultDeviceCodeCallback,
                        cancellationToken)
                    : await providerFactory(profile, environment, globalOptions, cancellationToken);

                await ViewsCommandGroup.ConfirmEnvironmentAsync(sp, confirmEnvironment, cancellationToken);

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
                var error = ExceptionMapper.Map(ex, context: ex is PpdsException ? null : "reordering columns", debug: globalOptions.Debug);
                writer.WriteError(error);
                return ExceptionMapper.ToExitCode(ex);
            }
        });

        return command;
    }
}
