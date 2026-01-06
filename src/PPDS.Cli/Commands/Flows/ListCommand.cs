using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Flows;

/// <summary>
/// List cloud flows.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var stateOption = new Option<string?>("--state")
        {
            Description = "Filter by state (Draft, Activated, Suspended)"
        };

        var command = new Command("list", "List cloud flows")
        {
            FlowsCommandGroup.SolutionOption,
            stateOption,
            FlowsCommandGroup.ProfileOption,
            FlowsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(FlowsCommandGroup.SolutionOption);
            var stateStr = parseResult.GetValue(stateOption);
            var profile = parseResult.GetValue(FlowsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FlowsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, stateStr, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? solution,
        string? stateStr,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Parse state filter if provided
            FlowState? state = null;
            if (!string.IsNullOrEmpty(stateStr))
            {
                if (!Enum.TryParse<FlowState>(stateStr, ignoreCase: true, out var parsedState))
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Validation.InvalidValue,
                        $"Invalid state '{stateStr}'. Valid values: Draft, Activated, Suspended"));
                    return ExitCodes.InvalidArguments;
                }
                state = parsedState;
            }

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

            var flows = await flowService.ListAsync(solution, state, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = flows.Select(f => new FlowListItem
                {
                    Id = f.Id,
                    UniqueName = f.UniqueName,
                    DisplayName = f.DisplayName,
                    State = f.State.ToString(),
                    Category = f.Category.ToString(),
                    IsManaged = f.IsManaged,
                    ConnectionReferenceCount = f.ConnectionReferenceLogicalNames.Count,
                    ModifiedOn = f.ModifiedOn
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (flows.Count == 0)
                {
                    Console.Error.WriteLine("No cloud flows found.");
                }
                else
                {
                    Console.Error.WriteLine($"Found {flows.Count} cloud flow(s):");
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
                        Console.WriteLine($"    Name: {f.UniqueName}  State: {stateDisplay}  Type: {f.Category}");
                        if (f.ConnectionReferenceLogicalNames.Count > 0)
                        {
                            Console.WriteLine($"    Connection References: {f.ConnectionReferenceLogicalNames.Count}");
                        }
                        if (f.IsManaged)
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
            var error = ExceptionMapper.Map(ex, context: "listing cloud flows", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class FlowListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("category")]
        public string Category { get; set; } = string.Empty;

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("connectionReferenceCount")]
        public int ConnectionReferenceCount { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
