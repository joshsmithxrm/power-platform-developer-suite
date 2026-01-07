using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// View and set plugin trace logging settings.
/// </summary>
public static class SettingsCommand
{
    public static Command Create()
    {
        var command = new Command("settings", "View or set plugin trace logging settings");

        command.Subcommands.Add(CreateGetCommand());
        command.Subcommands.Add(CreateSetCommand());

        return command;
    }

    private static Command CreateGetCommand()
    {
        var command = new Command("get", "Get current plugin trace logging setting")
        {
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteGetAsync(profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static Command CreateSetCommand()
    {
        var valueArgument = new Argument<string>("value")
        {
            Description = "The trace setting: off, exception, or all"
        };

        var command = new Command("set", "Set plugin trace logging setting")
        {
            valueArgument,
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var value = parseResult.GetValue(valueArgument);
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteSetAsync(value!, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteGetAsync(
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var settings = await traceService.GetSettingsAsync(cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new SettingsOutput
                {
                    Setting = settings.Setting.ToString().ToLowerInvariant(),
                    SettingName = settings.SettingName
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Plugin Trace Logging: {settings.SettingName}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("Available settings:");
                Console.Error.WriteLine("  off       - No tracing (recommended for production)");
                Console.Error.WriteLine("  exception - Log only exceptions");
                Console.Error.WriteLine("  all       - Log all plugin executions (debugging)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting plugin trace settings", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> ExecuteSetAsync(
        string value,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Parse the setting value
        PluginTraceLogSetting? setting = value.ToLowerInvariant() switch
        {
            "off" or "0" => PluginTraceLogSetting.Off,
            "exception" or "exceptions" or "1" => PluginTraceLogSetting.Exception,
            "all" or "2" => PluginTraceLogSetting.All,
            _ => null
        };

        if (setting == null)
        {
            var message = $"Invalid trace setting: '{value}'. Valid values: off, exception, all";
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                message,
                null,
                "value");

            if (globalOptions.IsJsonMode)
            {
                writer.WriteError(error);
            }
            else
            {
                Console.Error.WriteLine($"Error: {message}");
            }
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            await traceService.SetSettingsAsync(setting.Value, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new SetSettingsOutput
                {
                    Setting = setting.Value.ToString().ToLowerInvariant(),
                    Message = $"Plugin trace logging set to: {setting.Value}"
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Plugin trace logging set to: {setting.Value}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "setting plugin trace settings", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SettingsOutput
    {
        [JsonPropertyName("setting")]
        public string Setting { get; set; } = "";

        [JsonPropertyName("settingName")]
        public string SettingName { get; set; } = "";
    }

    private sealed class SetSettingsOutput
    {
        [JsonPropertyName("setting")]
        public string Setting { get; set; } = "";

        [JsonPropertyName("message")]
        public string Message { get; set; } = "";
    }

    #endregion
}
