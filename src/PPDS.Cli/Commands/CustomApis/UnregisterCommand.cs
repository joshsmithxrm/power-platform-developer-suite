using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Unregister a Custom API from Dataverse.
/// </summary>
public static class UnregisterCommand
{
    public static Command Create()
    {
        var uniqueNameOrIdArgument = new Argument<string>("unique-name-or-id")
        {
            Description = "Custom API unique name or GUID"
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Force cascade delete of dependent parameters and response properties",
            DefaultValueFactory = _ => false
        };

        var command = new Command("unregister", "Unregister a Custom API from Dataverse")
        {
            uniqueNameOrIdArgument,
            forceOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueNameOrId = parseResult.GetValue(uniqueNameOrIdArgument)!;
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueNameOrId, force, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueNameOrId,
        bool force,
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

            var customApiService = serviceProvider.GetRequiredService<ICustomApiService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve unique-name-or-id to the API record
            var api = await customApiService.GetAsync(uniqueNameOrId, cancellationToken);
            if (api == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Custom API '{uniqueNameOrId}' not found.",
                    Target: uniqueNameOrId));
                return ExitCodes.NotFoundError;
            }

            await customApiService.UnregisterAsync(api.Id, force, cancellationToken: cancellationToken);

            var result = new UnregisterResult
            {
                Success = true,
                Operation = "unregister-custom-api",
                Id = api.Id,
                UniqueName = api.UniqueName
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Unregistered Custom API: {api.UniqueName}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "unregistering Custom API", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class UnregisterResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;
    }

    #endregion
}
