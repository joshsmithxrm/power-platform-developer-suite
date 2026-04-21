using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Solutions;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// List components in a solution.
/// </summary>
public static class ComponentsCommand
{
    public static Command Create()
    {
        var uniqueNameArgument = new Argument<string>("unique-name")
        {
            Description = "The solution unique name"
        };

        var typeOption = new Option<int?>("--type", "-t")
        {
            Description = "Filter by component type (e.g., 61 for WebResource, 69 for PluginAssembly)"
        };

        var command = new Command("components", "List components in a solution")
        {
            uniqueNameArgument,
            typeOption,
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueName = parseResult.GetValue(uniqueNameArgument)!;
            var componentType = parseResult.GetValue(typeOption);
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueName, componentType, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueName,
        int? componentType,
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

            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // First get the solution to find its ID
            var solution = await solutionService.GetAsync(uniqueName, cancellationToken);
            if (solution == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Solution '{uniqueName}' not found.",
                    null,
                    uniqueName);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            var components = await solutionService.GetComponentsAsync(solution.Id, componentType, cancellationToken);

            if (components.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ComponentsOutput { Components = [] });
                }
                else
                {
                    Console.Error.WriteLine($"No components found in solution '{uniqueName}'.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ComponentsOutput
                {
                    Components = components.Select(c => new ComponentOutput
                    {
                        Id = c.Id,
                        ObjectId = c.ObjectId,
                        ComponentType = c.ComponentType,
                        ComponentTypeName = c.ComponentTypeName,
                        RootComponentBehavior = c.RootComponentBehavior,
                        IsMetadata = c.IsMetadata
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                // Group by component type for display
                var grouped = components.GroupBy(c => c.ComponentTypeName).OrderBy(g => g.Key);

                foreach (var group in grouped)
                {
                    Console.Error.WriteLine($"{group.Key}: {group.Count()}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {components.Count} component(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"listing components for solution '{uniqueName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ComponentsOutput
    {
        [JsonPropertyName("components")]
        public List<ComponentOutput> Components { get; set; } = [];
    }

    private sealed class ComponentOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("objectId")]
        public Guid ObjectId { get; set; }

        [JsonPropertyName("componentType")]
        public int ComponentType { get; set; }

        [JsonPropertyName("componentTypeName")]
        public string ComponentTypeName { get; set; } = string.Empty;

        [JsonPropertyName("rootComponentBehavior")]
        public int RootComponentBehavior { get; set; }

        [JsonPropertyName("isMetadata")]
        public bool IsMetadata { get; set; }
    }

    #endregion
}
