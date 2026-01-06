using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.Connections;

/// <summary>
/// Get a specific Power Platform connection by ID.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "The connection ID"
        };

        var command = new Command("get", "Get a connection by ID")
        {
            idArgument,
            ConnectionsCommandGroup.ProfileOption,
            ConnectionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument)!;
            var profile = parseResult.GetValue(ConnectionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ConnectionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(id, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string id,
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

            var connection = await connectionService.GetAsync(id, cancellationToken);

            if (connection == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Connection '{id}' not found"));
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ConnectionDetail
                {
                    ConnectionId = connection.ConnectionId,
                    DisplayName = connection.DisplayName,
                    ConnectorId = connection.ConnectorId,
                    ConnectorDisplayName = connection.ConnectorDisplayName,
                    EnvironmentId = connection.EnvironmentId,
                    Status = connection.Status.ToString(),
                    IsShared = connection.IsShared,
                    CreatedBy = connection.CreatedBy,
                    CreatedOn = connection.CreatedOn,
                    ModifiedOn = connection.ModifiedOn
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Connection: {connection.DisplayName ?? connection.ConnectionId}");
                Console.WriteLine($"  ID: {connection.ConnectionId}");
                Console.WriteLine($"  Connector: {connection.ConnectorDisplayName ?? connection.ConnectorId}");
                Console.WriteLine($"  Connector ID: {connection.ConnectorId}");
                Console.WriteLine($"  Status: {connection.Status}");
                Console.WriteLine($"  Shared: {(connection.IsShared ? "Yes" : "No")}");

                if (connection.EnvironmentId != null)
                {
                    Console.WriteLine($"  Environment: {connection.EnvironmentId}");
                }

                if (connection.CreatedBy != null)
                {
                    Console.WriteLine($"  Created by: {connection.CreatedBy}");
                }

                Console.WriteLine();
                Console.WriteLine($"  Created: {connection.CreatedOn}");
                Console.WriteLine($"  Modified: {connection.ModifiedOn}");
            }

            return ExitCodes.Success;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Service principals"))
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Auth.InsufficientPermissions,
                ex.Message));
            return ExitCodes.AuthError;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting connection", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ConnectionDetail
    {
        [JsonPropertyName("connectionId")]
        public string ConnectionId { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("connectorId")]
        public string ConnectorId { get; set; } = string.Empty;

        [JsonPropertyName("connectorDisplayName")]
        public string? ConnectorDisplayName { get; set; }

        [JsonPropertyName("environmentId")]
        public string? EnvironmentId { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("isShared")]
        public bool IsShared { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
