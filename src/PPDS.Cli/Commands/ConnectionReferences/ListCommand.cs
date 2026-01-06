using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ConnectionReferences;

/// <summary>
/// List connection references.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var unboundOption = new Option<bool>("--unbound")
        {
            Description = "Only show connection references without a bound connection"
        };

        var command = new Command("list", "List connection references")
        {
            ConnectionReferencesCommandGroup.SolutionOption,
            unboundOption,
            ConnectionReferencesCommandGroup.ProfileOption,
            ConnectionReferencesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(ConnectionReferencesCommandGroup.SolutionOption);
            var unboundOnly = parseResult.GetValue(unboundOption);
            var profile = parseResult.GetValue(ConnectionReferencesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ConnectionReferencesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, unboundOnly, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? solution,
        bool unboundOnly,
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

            var connectionRefs = await crService.ListAsync(solution, unboundOnly, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = connectionRefs.Select(cr => new ConnectionReferenceListItem
                {
                    Id = cr.Id,
                    LogicalName = cr.LogicalName,
                    DisplayName = cr.DisplayName,
                    ConnectorId = cr.ConnectorId,
                    ConnectionId = cr.ConnectionId,
                    IsBound = cr.IsBound,
                    IsManaged = cr.IsManaged,
                    ModifiedOn = cr.ModifiedOn
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (connectionRefs.Count == 0)
                {
                    Console.Error.WriteLine("No connection references found.");
                }
                else
                {
                    Console.Error.WriteLine($"Found {connectionRefs.Count} connection reference(s):");
                    Console.Error.WriteLine();

                    foreach (var cr in connectionRefs)
                    {
                        var boundStatus = cr.IsBound ? "Bound" : "Unbound";

                        Console.WriteLine($"  {cr.DisplayName ?? cr.LogicalName}");
                        Console.WriteLine($"    Name: {cr.LogicalName}  Status: {boundStatus}");
                        if (cr.ConnectorId != null)
                        {
                            Console.WriteLine($"    Connector: {cr.ConnectorId}");
                        }
                        if (cr.IsManaged)
                        {
                            Console.WriteLine($"    Managed: Yes");
                        }
                        Console.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing connection references", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ConnectionReferenceListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("logicalName")]
        public string LogicalName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("connectorId")]
        public string? ConnectorId { get; set; }

        [JsonPropertyName("connectionId")]
        public string? ConnectionId { get; set; }

        [JsonPropertyName("isBound")]
        public bool IsBound { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
