using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Flows;

/// <summary>
/// Get a specific cloud flow by unique name.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The unique name of the flow"
        };

        var command = new Command("get", "Get a cloud flow by unique name")
        {
            nameArgument,
            FlowsCommandGroup.ProfileOption,
            FlowsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(FlowsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FlowsCommandGroup.EnvironmentOption);
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

            var flowService = serviceProvider.GetRequiredService<IFlowService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var flow = await flowService.GetAsync(name, cancellationToken);

            if (flow == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Cloud flow '{name}' not found"));
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new FlowDetail
                {
                    Id = flow.Id,
                    UniqueName = flow.UniqueName,
                    DisplayName = flow.DisplayName,
                    Description = flow.Description,
                    State = flow.State.ToString(),
                    Category = flow.Category.ToString(),
                    IsManaged = flow.IsManaged,
                    ConnectionReferenceLogicalNames = flow.ConnectionReferenceLogicalNames,
                    OwnerId = flow.OwnerId,
                    OwnerName = flow.OwnerName,
                    CreatedOn = flow.CreatedOn,
                    ModifiedOn = flow.ModifiedOn
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine($"Flow: {flow.DisplayName ?? flow.UniqueName}");
                Console.WriteLine($"  Unique Name: {flow.UniqueName}");
                Console.WriteLine($"  ID: {flow.Id}");
                Console.WriteLine($"  State: {flow.State}");
                Console.WriteLine($"  Category: {flow.Category}");
                Console.WriteLine($"  Managed: {(flow.IsManaged ? "Yes" : "No")}");

                if (!string.IsNullOrEmpty(flow.Description))
                {
                    Console.WriteLine($"  Description: {flow.Description}");
                }

                if (flow.ConnectionReferenceLogicalNames.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Connection References ({flow.ConnectionReferenceLogicalNames.Count}):");
                    foreach (var cr in flow.ConnectionReferenceLogicalNames)
                    {
                        Console.WriteLine($"    - {cr}");
                    }
                }

                if (flow.OwnerName != null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"  Owner: {flow.OwnerName}");
                }

                Console.WriteLine();
                Console.WriteLine($"  Created: {flow.CreatedOn}");
                Console.WriteLine($"  Modified: {flow.ModifiedOn}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting cloud flow", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class FlowDetail
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("connectionReferenceLogicalNames")]
        public List<string> ConnectionReferenceLogicalNames { get; set; } = new();

        [JsonPropertyName("ownerId")]
        public Guid? OwnerId { get; set; }

        [JsonPropertyName("ownerName")]
        public string? OwnerName { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
