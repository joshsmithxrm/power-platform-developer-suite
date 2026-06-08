using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Lists the Copilot Studio agents (bots) wired into a model-driven app.
/// </summary>
public static class ListCopilotsCommand
{
    public static Command Create()
    {
        var command = new Command("list-copilots", "List the Copilot Studio agents (bots) wired into the app")
        {
            ModelDrivenAppCommandGroup.AppOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
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

            var copilots = await service.ListCopilotsAsync(appName, ct);

            if (globalOptions.IsJsonMode)
            {
                var output = copilots.Select(c => new CopilotListItem
                {
                    AppElementId = c.AppElementId,
                    UniqueName = c.UniqueName,
                    Name = c.Name,
                    BotId = c.BotId,
                    BotName = c.BotName
                }).ToList();
                writer.WriteSuccess(output);
            }
            else if (copilots.Count == 0)
            {
                Console.Error.WriteLine($"No Copilots wired into app '{appName}'.");
            }
            else
            {
                var nameWidth = Math.Max(8, copilots.Max(c => (c.BotName ?? c.Name).Length));
                Console.WriteLine($"{"Copilot".PadRight(nameWidth)}  Bot Id");
                Console.WriteLine(new string('─', nameWidth + 40));
                foreach (var copilot in copilots)
                {
                    Console.WriteLine($"{(copilot.BotName ?? copilot.Name).PadRight(nameWidth)}  {copilot.BotId}");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"listing Copilots for app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private sealed class CopilotListItem
    {
        [JsonPropertyName("appElementId")]
        public Guid AppElementId { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("botId")]
        public Guid BotId { get; set; }

        [JsonPropertyName("botName")]
        public string? BotName { get; set; }
    }
}
