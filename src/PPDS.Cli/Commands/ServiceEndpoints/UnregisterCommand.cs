using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.ServiceEndpoints;

/// <summary>
/// Unregister a service endpoint from Dataverse.
/// </summary>
public static class UnregisterCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Service endpoint name or GUID"
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Force cascade delete of dependent step registrations",
            DefaultValueFactory = _ => false
        };

        var command = new Command("unregister", "Unregister a service endpoint from Dataverse")
        {
            nameOrIdArgument,
            forceOption,
            ServiceEndpointsCommandGroup.ProfileOption,
            ServiceEndpointsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(ServiceEndpointsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ServiceEndpointsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(nameOrId, force, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
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

            var endpointService = serviceProvider.GetRequiredService<IServiceEndpointService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve name-or-id to GUID
            Guid endpointId;
            string endpointName;

            if (Guid.TryParse(nameOrId, out var id))
            {
                var existing = await endpointService.GetByIdAsync(id, cancellationToken);
                if (existing == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Service endpoint '{nameOrId}' not found.",
                        Target: nameOrId));
                    return ExitCodes.NotFoundError;
                }
                endpointId = existing.Id;
                endpointName = existing.Name;
            }
            else
            {
                var existing = await endpointService.GetByNameAsync(nameOrId, cancellationToken);
                if (existing == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Service endpoint '{nameOrId}' not found.",
                        Target: nameOrId));
                    return ExitCodes.NotFoundError;
                }
                endpointId = existing.Id;
                endpointName = existing.Name;
            }

            await endpointService.UnregisterAsync(endpointId, force, cancellationToken: cancellationToken);

            var result = new UnregisterResult
            {
                Success = true,
                Operation = "unregister-service-endpoint",
                Id = endpointId,
                Name = endpointName
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Unregistered service endpoint: {endpointName}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "unregistering service endpoint", debug: globalOptions.Debug);
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

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    #endregion
}
