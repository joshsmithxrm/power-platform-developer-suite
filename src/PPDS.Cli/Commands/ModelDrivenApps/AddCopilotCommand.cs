using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ModelDrivenApps;

namespace PPDS.Cli.Commands.ModelDrivenApps;

/// <summary>
/// Wires a Copilot Studio agent (bot) into a model-driven app by creating an
/// <c>appelement</c> whose polymorphic <c>objectid</c> targets the <c>bot</c> table.
/// </summary>
public static class AddCopilotCommand
{
    public static Command Create()
    {
        var botOption = new Option<string?>("--bot")
        {
            Description = "[Required] Copilot agent (bot) display name, schema name, or id"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview the appelement that would be created (and the eligibility verdict); makes no changes"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Wire the Copilot even if the app is reported unsupported for the app-assistant agent"
        };

        var command = new Command("add-copilot", "Wire a Copilot Studio agent (bot) into the app")
        {
            ModelDrivenAppCommandGroup.AppOption,
            botOption,
            ModelDrivenAppCommandGroup.PublishOption,
            dryRunOption,
            forceOption,
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
            var force = parseResult.GetValue(forceOption);
            var confirm = parseResult.GetValue(ModelDrivenAppCommandGroup.ConfirmOption);
            var profile = parseResult.GetValue(ModelDrivenAppCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ModelDrivenAppCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);
            return await ExecuteAsync(app, bot, publish, dryRun, force, confirm, profile, environment, globalOptions, ct);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? appName,
        string? bot,
        bool publish,
        bool dryRun,
        bool force,
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

            var options = new CopilotOptions(publish, dryRun, force, confirm);
            var result = await service.AddCopilotAsync(appName, bot, options, null, ct);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    success = true,
                    dryRun = result.DryRun,
                    app = result.AppName,
                    appModuleId = result.AppModuleId,
                    bot = result.BotName,
                    botId = result.BotId,
                    appElementId = result.AppElementId,
                    uniqueName = result.UniqueName,
                    published = result.Published,
                    eligible = result.EligibilityReason == null,
                    eligibilityReason = result.EligibilityReason,
                    forced = result.Forced
                });
            }
            else if (result.DryRun)
            {
                Console.Error.WriteLine($"DRY RUN — would wire Copilot '{result.BotName}' into app '{result.AppName}' (create appelement):");
                Console.Error.WriteLine($"  appelement.name       = {result.BotSchemaName}");
                Console.Error.WriteLine($"  appelement.uniquename = {result.UniqueName}");
                Console.Error.WriteLine($"  parentappmoduleid     = appmodule({result.AppModuleId})");
                Console.Error.WriteLine($"  objectid              = bot({result.BotId})  // {result.BotName}");
                Console.Error.WriteLine("  (a unique suffix is appended automatically if this name is already taken)");
                Console.Error.WriteLine(result.EligibilityReason == null
                    ? "  eligibility           = OK (app supports the app-assistant agent)"
                    : $"  eligibility           = UNSUPPORTED — {result.EligibilityReason} (re-run without --dry-run and with --force to wire anyway)");
                Console.Error.WriteLine("No changes made.");
            }
            else
            {
                if (result.Forced && result.EligibilityReason != null)
                {
                    Console.Error.WriteLine($"WARNING: app reported unsupported ({result.EligibilityReason}) — wired anyway because --force was set.");
                }

                Console.Error.WriteLine($"Wired Copilot '{result.BotName}' into app '{result.AppName}' (appelement {result.AppElementId}).");
                Console.Error.WriteLine(result.Published
                    ? "App published."
                    : $"Not published. Re-run with --publish or: ppds publish --app \"{result.AppName}\"");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"wiring Copilot '{bot}' into app '{appName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
