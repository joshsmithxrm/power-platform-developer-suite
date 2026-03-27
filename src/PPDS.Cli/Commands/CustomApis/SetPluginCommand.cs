using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Set or clear the implementing plugin type on a Custom API.
/// </summary>
public static class SetPluginCommand
{
    public static Command Create()
    {
        var uniqueNameOrIdArgument = new Argument<string>("unique-name-or-id")
        {
            Description = "Custom API unique name or GUID"
        };

        var pluginOption = new Option<string?>("--plugin")
        {
            Description = "Plugin type name to set as the implementing type"
        };

        var assemblyOption = new Option<string?>("--assembly")
        {
            Description = "Assembly name for disambiguation (optional)"
        };

        var noneOption = new Option<bool>("--none")
        {
            Description = "Clear the plugin type (mutually exclusive with --plugin)"
        };

        var command = new Command("set-plugin", "Set or clear the implementing plugin type on a Custom API")
        {
            uniqueNameOrIdArgument,
            pluginOption,
            assemblyOption,
            noneOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueNameOrId = parseResult.GetValue(uniqueNameOrIdArgument)!;
            var pluginName = parseResult.GetValue(pluginOption);
            var assemblyName = parseResult.GetValue(assemblyOption);
            var none = parseResult.GetValue(noneOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueNameOrId, pluginName, assemblyName, none, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueNameOrId,
        string? pluginName,
        string? assemblyName,
        bool none,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate mutually exclusive options
        if (none && pluginName is not null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "--none and --plugin are mutually exclusive. Use one or the other.",
                Target: uniqueNameOrId));
            return ExitCodes.InvalidArguments;
        }

        if (!none && pluginName is null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "Specify --plugin <type-name> to set a plugin type, or --none to clear it.",
                Target: uniqueNameOrId));
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

            var customApiService = serviceProvider.GetRequiredService<ICustomApiService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve to the Custom API record
            var api = await customApiService.GetAsync(uniqueNameOrId, cancellationToken);
            if (api == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Custom API '{uniqueNameOrId}' not found.",
                    Target: uniqueNameOrId));
                return ExitCodes.NotFoundError;
            }

            // Set or clear the plugin type
            var pluginTypeName = none ? null : pluginName;
            await customApiService.SetPluginTypeAsync(api.Id, pluginTypeName, assemblyName, cancellationToken);

            var result = new SetPluginResult
            {
                Type = "custom-api",
                UniqueName = api.UniqueName,
                Id = api.Id,
                PluginTypeName = pluginTypeName,
                Action = none ? "cleared" : "set"
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                if (none)
                {
                    Console.Error.WriteLine($"Plugin type cleared on Custom API: {api.UniqueName}");
                }
                else
                {
                    Console.Error.WriteLine($"Plugin type set on Custom API: {api.UniqueName}");
                    Console.Error.WriteLine($"  Plugin type: {pluginName}");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"setting plugin type on Custom API '{uniqueNameOrId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class SetPluginResult
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("pluginTypeName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? PluginTypeName { get; init; }

        [JsonPropertyName("action")]
        public string Action { get; init; } = string.Empty;
    }

    #endregion
}
