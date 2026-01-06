using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ConnectionReferences;

/// <summary>
/// List flows using a specific connection reference.
/// </summary>
public static class FlowsCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The logical name of the connection reference"
        };

        var command = new Command("flows", "List flows that use a connection reference")
        {
            nameArgument,
            ConnectionReferencesCommandGroup.ProfileOption,
            ConnectionReferencesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(ConnectionReferencesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ConnectionReferencesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
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

            var crService = serviceProvider.GetRequiredService<IConnectionReferenceService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // First verify the CR exists
            var cr = await crService.GetAsync(name, cancellationToken);
            if (cr == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Connection reference '{name}' not found"));
                return ExitCodes.NotFoundError;
            }

            var flows = await crService.GetFlowsUsingAsync(name, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new FlowsUsingCrOutput
                {
                    ConnectionReference = new ConnectionRefSummary
                    {
                        LogicalName = cr.LogicalName,
                        DisplayName = cr.DisplayName,
                        IsBound = cr.IsBound
                    },
                    Flows = flows.Select(f => new FlowSummary
                    {
                        UniqueName = f.UniqueName,
                        DisplayName = f.DisplayName,
                        State = f.State.ToString()
                    }).ToList()
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Connection Reference: {cr.DisplayName ?? cr.LogicalName}");
                Console.Error.WriteLine();

                if (flows.Count == 0)
                {
                    Console.Error.WriteLine("No flows use this connection reference.");
                }
                else
                {
                    Console.Error.WriteLine($"Flows using this connection reference ({flows.Count}):");
                    Console.Error.WriteLine();

                    foreach (var f in flows)
                    {
                        var stateDisplay = f.State switch
                        {
                            FlowState.Activated => "On",
                            FlowState.Suspended => "Suspended",
                            _ => "Off"
                        };

                        Console.WriteLine($"  {f.DisplayName ?? f.UniqueName}");
                        Console.WriteLine($"    Name: {f.UniqueName}  State: {stateDisplay}");
                        Console.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting flows for connection reference", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class FlowsUsingCrOutput
    {
        [JsonPropertyName("connectionReference")]
        public ConnectionRefSummary ConnectionReference { get; set; } = new();

        [JsonPropertyName("flows")]
        public List<FlowSummary> Flows { get; set; } = new();
    }

    private sealed class ConnectionRefSummary
    {
        [JsonPropertyName("logicalName")]
        public string LogicalName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("isBound")]
        public bool IsBound { get; set; }
    }

    private sealed class FlowSummary
    {
        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;
    }

    #endregion
}
