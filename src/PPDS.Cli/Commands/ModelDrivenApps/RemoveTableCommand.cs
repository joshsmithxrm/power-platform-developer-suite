using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Removes a table from a model-driven app's sitemap navigation and cleans up its explicit components.
/// </summary>
public static class RemoveTableCommand
{
    public static Command Create()
    {
        var entityOption = new Option<string?>("--entity")
        {
            Description = "[Required] Logical name of the table to remove"
        };

        var command = new Command("remove-table", "Remove a table from the app's sitemap navigation and clean up its components")
        {
            ModelDrivenAppCommandGroup.AppOption,
            entityOption,
            ModelDrivenAppCommandGroup.SolutionOption,
            ModelDrivenAppCommandGroup.PublishOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var entity = parseResult.GetValue(entityOption);
            var solution = parseResult.GetValue(ModelDrivenAppCommandGroup.SolutionOption);
            var publish = parseResult.GetValue(ModelDrivenAppCommandGroup.PublishOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, entity, solution, publish, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? entity,
        string? solution,
        bool publish,
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

            var options = new ModifyOptions(solution, publish);
            await service.RemoveTableAsync(appName, entity, options, null, ct);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Removed table '{entity}' from app '{appName}'.");
            }
            else
            {
                writer.WriteSuccess(new { success = true, app = appName, entity });
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"removing table '{entity}' from app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
