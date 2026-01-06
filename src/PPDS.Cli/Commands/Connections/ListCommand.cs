using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.Connections;

/// <summary>
/// List Power Platform connections from the Power Apps Admin API.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var connectorOption = new Option<string?>("--connector")
        {
            Description = "Filter by connector ID (e.g., shared_commondataserviceforapps)"
        };

        var command = new Command("list", "List connections from Power Apps Admin API")
        {
            connectorOption,
            ConnectionsCommandGroup.ProfileOption,
            ConnectionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var connector = parseResult.GetValue(connectorOption);
            var profile = parseResult.GetValue(ConnectionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ConnectionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(connector, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? connector,
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

            var connectionService = serviceProvider.GetRequiredService<IConnectionService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var connections = await connectionService.ListAsync(connector, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = connections.Select(c => new ConnectionListItem
                {
                    ConnectionId = c.ConnectionId,
                    DisplayName = c.DisplayName,
                    ConnectorId = c.ConnectorId,
                    ConnectorDisplayName = c.ConnectorDisplayName,
                    Status = c.Status.ToString(),
                    IsShared = c.IsShared,
                    CreatedBy = c.CreatedBy,
                    ModifiedOn = c.ModifiedOn
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (connections.Count == 0)
                {
                    Console.Error.WriteLine("No connections found.");
                }
                else
                {
                    Console.Error.WriteLine($"Found {connections.Count} connection(s):");
                    Console.Error.WriteLine();

                    foreach (var c in connections)
                    {
                        var statusDisplay = c.Status switch
                        {
                            ConnectionStatus.Connected => "Connected",
                            ConnectionStatus.Error => "Error",
                            _ => "Unknown"
                        };

                        Console.WriteLine($"  {c.DisplayName ?? c.ConnectionId}");
                        Console.WriteLine($"    ID: {c.ConnectionId}");
                        Console.WriteLine($"    Connector: {c.ConnectorDisplayName ?? c.ConnectorId}");
                        Console.WriteLine($"    Status: {statusDisplay}  Shared: {(c.IsShared ? "Yes" : "No")}");
                        if (c.CreatedBy != null)
                        {
                            Console.WriteLine($"    Created by: {c.CreatedBy}");
                        }
                        Console.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Service principals"))
        {
            // Special handling for SPN limitation
            writer.WriteError(new StructuredError(
                ErrorCodes.Auth.InsufficientPermissions,
                ex.Message));
            return ExitCodes.AuthError;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing connections", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ConnectionListItem
    {
        [JsonPropertyName("connectionId")]
        public string ConnectionId { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("connectorId")]
        public string ConnectorId { get; set; } = string.Empty;

        [JsonPropertyName("connectorDisplayName")]
        public string? ConnectorDisplayName { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("isShared")]
        public bool IsShared { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
