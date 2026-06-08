using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Sets explicit view visibility for an entity in a model-driven app.
/// Requires either --all (include all views) or one or more --view options.
/// </summary>
public static class SetViewsCommand
{
    public static Command Create()
    {
        var entityOption = new Option<string?>("--entity")
        {
            Description = "[Required] Logical name of the table"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Include all views (removes explicit selections, enabling include-all mode)"
        };

        var viewOption = new Option<string[]>("--view")
        {
            Description = "View name to include explicitly (can be specified multiple times). Requires --all or at least one --view.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = false
        };

        var command = new Command("set-views", "Set explicit view visibility for a table in the app. Requires --all or at least one --view.")
        {
            ModelDrivenAppCommandGroup.AppOption,
            entityOption,
            allOption,
            viewOption,
            ModelDrivenAppCommandGroup.SolutionOption,
            ModelDrivenAppCommandGroup.PublishOption,
            ModelDrivenAppCommandGroup.ConfirmOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var entity = parseResult.GetValue(entityOption);
            var all = parseResult.GetValue(allOption);
            var views = parseResult.GetValue(viewOption) ?? [];
            var solution = parseResult.GetValue(ModelDrivenAppCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ModelDrivenAppCommandGroup.PublishOption);
            var confirm = parseResult.GetValue(ModelDrivenAppCommandGroup.ConfirmOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, entity, all, views, solution, publish, confirm, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? entity,
        bool all,
        string[] viewNames,
        string? solution,
        bool publish,
        bool confirm,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken ct)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        if (string.IsNullOrWhiteSpace(appName))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.RequiredField, "--app is required."));
            return ExitCodes.InvalidArguments;
        }

        if (string.IsNullOrWhiteSpace(entity))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.RequiredField, "--entity is required."));
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile, environment, globalOptions.Verbose, globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback, ct);

            var service = serviceProvider.GetRequiredService<IModelDrivenAppService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var options = new ComponentSelectionOptions(all, viewNames, solution, publish, confirm);
            await service.SetViewsAsync(appName, entity, options, null, ct);

            if (!globalOptions.IsJsonMode)
            {
                var msg = all
                    ? $"Set views to include-all for '{entity}' in app '{appName}'."
                    : $"Set {viewNames.Length} explicit view(s) for '{entity}' in app '{appName}'.";
                Console.Error.WriteLine(msg);
            }
            else
            {
                writer.WriteSuccess(new { success = true, app = appName, entity, all, views = viewNames });
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"setting views for entity '{entity}' in app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
