using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Lists all entities with basic metadata.
/// </summary>
public static class EntitiesCommand
{
    /// <summary>
    /// Creates the 'entities' command.
    /// </summary>
    public static Command Create()
    {
        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter entities by name pattern (supports * wildcard, e.g., 'account*')"
        };

        var customOnlyOption = new Option<bool>("--custom-only")
        {
            Description = "Only show custom entities",
            DefaultValueFactory = _ => false
        };

        var command = new Command("entities", "List all entities with basic information")
        {
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption,
            filterOption,
            customOnlyOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var filter = parseResult.GetValue(filterOption);
            var customOnly = parseResult.GetValue(customOnlyOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, filter, customOnly, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        string? filter,
        bool customOnly,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var metadataService = serviceProvider.GetRequiredService<IMetadataService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Retrieving entities...");
            }

            var entities = await metadataService.GetEntitiesAsync(customOnly, filter, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(entities);
            }
            else
            {
                Console.Error.WriteLine();
                foreach (var entity in entities)
                {
                    var markers = new List<string>();
                    if (entity.IsCustomEntity) markers.Add("custom");
                    if (entity.IsManaged) markers.Add("managed");

                    var markerText = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
                    Console.Error.WriteLine($"  {entity.LogicalName,-40} {entity.DisplayName}{markerText}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {entities.Count} entities");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "retrieving entities", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
