using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ConnectionReferences;

/// <summary>
/// Show bound connection details for a connection reference.
/// </summary>
public static class ConnectionsCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The logical name of the connection reference"
        };

        var command = new Command("connections", "Show bound connection for a connection reference")
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

            var cr = await crService.GetAsync(name, cancellationToken);

            if (cr == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Connection reference '{name}' not found"));
                return ExitCodes.NotFoundError;
            }

            if (!cr.IsBound)
            {
                if (globalOptions.IsJsonMode)
                {
                    var output = new ConnectionBindingOutput
                    {
                        ConnectionReference = new ConnectionRefInfo
                        {
                            LogicalName = cr.LogicalName,
                            DisplayName = cr.DisplayName,
                            ConnectorId = cr.ConnectorId
                        },
                        IsBound = false,
                        Connection = null
                    };
                    writer.WriteSuccess(output);
                }
                else
                {
                    Console.Error.WriteLine($"Connection Reference: {cr.DisplayName ?? cr.LogicalName}");
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("This connection reference is not bound to a connection.");
                    Console.Error.WriteLine("Use the Power Apps portal or pac solution commands to bind a connection.");
                }
                return ExitCodes.Success;
            }

            // Try to get connection details from Power Apps API
            ConnectionInfo? connection = null;
            try
            {
                var connectionService = serviceProvider.GetService<IConnectionService>();
                if (connectionService != null && cr.ConnectionId != null)
                {
                    connection = await connectionService.GetAsync(cr.ConnectionId, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                // Connection service may not be available or may fail (e.g., SPN auth)
                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Warning: Could not retrieve connection details: {ex.Message}");
                    Console.Error.WriteLine();
                }
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ConnectionBindingOutput
                {
                    ConnectionReference = new ConnectionRefInfo
                    {
                        LogicalName = cr.LogicalName,
                        DisplayName = cr.DisplayName,
                        ConnectorId = cr.ConnectorId
                    },
                    IsBound = true,
                    Connection = connection != null ? new ConnectionDetails
                    {
                        ConnectionId = connection.ConnectionId,
                        DisplayName = connection.DisplayName,
                        ConnectorDisplayName = connection.ConnectorDisplayName,
                        Status = connection.Status.ToString(),
                        CreatedBy = connection.CreatedBy
                    } : new ConnectionDetails
                    {
                        ConnectionId = cr.ConnectionId!,
                        DisplayName = null,
                        ConnectorDisplayName = null,
                        Status = "Unknown",
                        CreatedBy = null
                    }
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Connection Reference: {cr.DisplayName ?? cr.LogicalName}");
                Console.WriteLine($"  Logical Name: {cr.LogicalName}");
                Console.WriteLine($"  Connector ID: {cr.ConnectorId}");
                Console.WriteLine();
                Console.WriteLine("Bound Connection:");
                Console.WriteLine($"  Connection ID: {cr.ConnectionId}");

                if (connection != null)
                {
                    if (connection.DisplayName != null)
                    {
                        Console.WriteLine($"  Display Name: {connection.DisplayName}");
                    }
                    if (connection.ConnectorDisplayName != null)
                    {
                        Console.WriteLine($"  Connector: {connection.ConnectorDisplayName}");
                    }
                    Console.WriteLine($"  Status: {connection.Status}");
                    if (connection.CreatedBy != null)
                    {
                        Console.WriteLine($"  Created by: {connection.CreatedBy}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting connection binding", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ConnectionBindingOutput
    {
        [JsonPropertyName("connectionReference")]
        public ConnectionRefInfo ConnectionReference { get; set; } = new();

        [JsonPropertyName("isBound")]
        public bool IsBound { get; set; }

        [JsonPropertyName("connection")]
        public ConnectionDetails? Connection { get; set; }
    }

    private sealed class ConnectionRefInfo
    {
        [JsonPropertyName("logicalName")]
        public string LogicalName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("connectorId")]
        public string? ConnectorId { get; set; }
    }

    private sealed class ConnectionDetails
    {
        [JsonPropertyName("connectionId")]
        public string ConnectionId { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("connectorDisplayName")]
        public string? ConnectorDisplayName { get; set; }

        [JsonPropertyName("status")]
        public string Status { get; set; } = string.Empty;

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }
    }

    #endregion
}
