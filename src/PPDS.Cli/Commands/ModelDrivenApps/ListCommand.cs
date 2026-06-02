using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Lists all model-driven apps in the environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List all model-driven apps in the environment")
        {
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken ct)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

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

            var apps = await service.ListAppsAsync(ct);

            if (globalOptions.IsJsonMode)
            {
                var output = apps.Select(a => new AppListItem
                {
                    AppModuleId = a.AppModuleId,
                    DisplayName = a.DisplayName,
                    UniqueName = a.UniqueName,
                    ComponentCount = a.ComponentCount
                }).ToList();
                writer.WriteSuccess(output);
            }
            else
            {
                if (apps.Count == 0)
                {
                    Console.Error.WriteLine("No model-driven apps found.");
                }
                else
                {
                    var nameWidth = Math.Max(4, apps.Max(a => a.DisplayName.Length));
                    var uniqueWidth = Math.Max(11, apps.Max(a => a.UniqueName.Length));

                    Console.WriteLine($"{"Name".PadRight(nameWidth)}  {"Unique Name".PadRight(uniqueWidth)}  Components");
                    Console.WriteLine(new string('─', nameWidth + uniqueWidth + 16));

                    foreach (var app in apps)
                    {
                        Console.WriteLine($"{app.DisplayName.PadRight(nameWidth)}  {app.UniqueName.PadRight(uniqueWidth)}  {app.ComponentCount}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing model-driven apps", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private sealed class AppListItem
    {
        [JsonPropertyName("appModuleId")]
        public Guid AppModuleId { get; set; }

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("componentCount")]
        public int ComponentCount { get; set; }
    }
}
