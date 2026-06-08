using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Adds one or more tables (entities) to a model-driven app's sitemap navigation.
/// </summary>
public static class AddTableCommand
{
    public static Command Create()
    {
        var entitiesArgument = new Argument<string[]>("entities")
        {
            Description = "Logical name(s) of the table(s) to add",
            Arity = ArgumentArity.OneOrMore
        };

        var groupOption = new Option<string?>("--group")
        {
            Description = "Navigation group name (creates if not found)"
        };

        var areaOption = new Option<string?>("--area")
        {
            Description = "Navigation area name (creates if not found, defaults to first area)"
        };

        var titleOption = new Option<string?>("--title")
        {
            Description = "Display title for the SubArea (defaults to entity's DisplayName, applies to first entity when multiple specified)"
        };

        var command = new Command("add-table", "Add one or more tables to the app's sitemap navigation")
        {
            entitiesArgument,
            ModelDrivenAppCommandGroup.AppOption,
            groupOption,
            areaOption,
            titleOption,
            ModelDrivenAppCommandGroup.SolutionOption,
            ModelDrivenAppCommandGroup.PublishOption,
            ModelDrivenAppCommandGroup.ConfirmOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var entities = parseResult.GetValue(entitiesArgument) ?? [];
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var group = parseResult.GetValue(groupOption);
            var area = parseResult.GetValue(areaOption);
            var title = parseResult.GetValue(titleOption);
            var solution = parseResult.GetValue(ModelDrivenAppCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ModelDrivenAppCommandGroup.PublishOption);
            var confirm = parseResult.GetValue(ModelDrivenAppCommandGroup.ConfirmOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(entities, app, group, area, title, solution, publish, confirm, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string[] entities,
        string? appName,
        string? group,
        string? area,
        string? title,
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

            var options = new AddTableOptions(group, area, title, solution, publish, confirm);
            await service.AddTableAsync(appName, entities, options, null, ct);

            if (!globalOptions.IsJsonMode)
            {
                var entityList = string.Join(", ", entities);
                Console.Error.WriteLine($"Added {entities.Length} table(s) to app '{appName}': {entityList}");
            }
            else
            {
                writer.WriteSuccess(new { success = true, app = appName, entities });
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"adding tables to app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
