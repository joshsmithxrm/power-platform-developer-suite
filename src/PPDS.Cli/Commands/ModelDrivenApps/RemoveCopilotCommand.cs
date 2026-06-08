using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Removes a Copilot (bot) binding from a model-driven app by deleting the
/// <c>appelement</c> whose <c>objectid</c> targets the bot.
/// </summary>
public static class RemoveCopilotCommand
{
    public static Command Create()
    {
        var botOption = new Option<string?>("--bot")
        {
            Description = "[Required] Copilot agent (bot) display name, schema name, or id"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview the binding that would be removed; makes no changes"
        };

        var command = new Command("remove-copilot", "Remove a Copilot Studio agent (bot) from the app")
        {
            ModelDrivenAppCommandGroup.AppOption,
            botOption,
            ModelDrivenAppCommandGroup.PublishOption,
            dryRunOption,
            ModelDrivenAppCommandGroup.ConfirmOption,
            ModelDrivenAppCommandGroup.ProfileOption,
            ModelDrivenAppCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, ct) =>
        {
            var app = parseResult.GetValue(ModelDrivenAppCommandGroup.AppOption);
            var bot = parseResult.GetValue(botOption);
            var publish = parseResult.GetValue(ModelDrivenAppCommandGroup.PublishOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var confirm = parseResult.GetValue(ModelDrivenAppCommandGroup.ConfirmOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, bot, publish, dryRun, confirm, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? bot,
        bool publish,
        bool dryRun,
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

        if (string.IsNullOrWhiteSpace(bot))
        {
            writer.WriteError(new StructuredError(ErrorCodes.Validation.RequiredField, "--bot is required."));
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

            var options = new CopilotOptions(publish, dryRun, Force: false, Confirm: confirm);
            var result = await service.RemoveCopilotAsync(appName, bot, options, null, ct);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    success = true,
                    dryRun = result.DryRun,
                    app = result.AppName,
                    bot = result.BotName,
                    botId = result.BotId,
                    appElementId = result.AppElementId,
                    published = result.Published
                });
            }
            else if (result.DryRun)
            {
                Console.Error.WriteLine($"DRY RUN — would remove Copilot '{result.BotName}' from app '{result.AppName}':");
                Console.Error.WriteLine($"  appelement {result.AppElementId} ({result.UniqueName})");
                Console.Error.WriteLine("No changes made.");
            }
            else
            {
                Console.Error.WriteLine($"Removed Copilot '{result.BotName}' from app '{result.AppName}'.");
                Console.Error.WriteLine(result.Published
                    ? "App published."
                    : $"Not published. Re-run with --publish or: ppds publish --app \"{result.AppName}\"");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"removing Copilot '{bot}' from app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
