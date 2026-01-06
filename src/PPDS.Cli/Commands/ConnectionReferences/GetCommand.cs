using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ConnectionReferences;

/// <summary>
/// Get a specific connection reference by logical name.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The logical name of the connection reference"
        };

        var command = new Command("get", "Get a connection reference by logical name")
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

            if (globalOptions.IsJsonMode)
            {
                var output = new ConnectionReferenceDetail
                {
                    Id = cr.Id,
                    LogicalName = cr.LogicalName,
                    DisplayName = cr.DisplayName,
                    Description = cr.Description,
                    ConnectorId = cr.ConnectorId,
                    ConnectionId = cr.ConnectionId,
                    IsBound = cr.IsBound,
                    IsManaged = cr.IsManaged,
                    CreatedOn = cr.CreatedOn,
                    ModifiedOn = cr.ModifiedOn
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Connection Reference: {cr.DisplayName ?? cr.LogicalName}");
                Console.WriteLine($"  Logical Name: {cr.LogicalName}");
                Console.WriteLine($"  ID: {cr.Id}");
                Console.WriteLine($"  Status: {(cr.IsBound ? "Bound" : "Unbound")}");
                Console.WriteLine($"  Managed: {(cr.IsManaged ? "Yes" : "No")}");

                if (!string.IsNullOrEmpty(cr.Description))
                {
                    Console.WriteLine($"  Description: {cr.Description}");
                }

                if (cr.ConnectorId != null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Connector ID: {cr.ConnectorId}");
                }

                if (cr.ConnectionId != null)
                {
                    Console.WriteLine($"  Connection ID: {cr.ConnectionId}");
                }

                Console.WriteLine();
                Console.WriteLine($"  Created: {cr.CreatedOn}");
                Console.WriteLine($"  Modified: {cr.ModifiedOn}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting connection reference", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ConnectionReferenceDetail
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("logicalName")]
        public string LogicalName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("connectorId")]
        public string? ConnectorId { get; set; }

        [JsonPropertyName("connectionId")]
        public string? ConnectionId { get; set; }

        [JsonPropertyName("isBound")]
        public bool IsBound { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
