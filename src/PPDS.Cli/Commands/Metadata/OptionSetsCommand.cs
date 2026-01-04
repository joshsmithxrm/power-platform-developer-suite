using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Lists global option sets.
/// </summary>
public static class OptionSetsCommand
{
    /// <summary>
    /// Creates the 'optionsets' command.
    /// </summary>
    public static Command Create()
    {
        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter option sets by name pattern (supports * wildcard, e.g., 'new_*')"
        };

        var command = new Command("optionsets", "List global option sets")
        {
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption,
            filterOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var filter = parseResult.GetValue(filterOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, filter, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        string? filter,
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
                Console.Error.WriteLine("Retrieving global option sets...");
            }

            var optionSets = await metadataService.GetGlobalOptionSetsAsync(filter, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(optionSets);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"{"Name",-45} {"Type",-15} {"Display Name",-30} {"Options"}");
                Console.Error.WriteLine(new string('-', 100));

                foreach (var os in optionSets)
                {
                    var markers = new List<string>();
                    if (os.IsCustomOptionSet) markers.Add("custom");
                    if (os.IsManaged) markers.Add("managed");

                    var markerText = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
                    Console.Error.WriteLine($"  {os.Name,-45} {os.OptionSetType,-15} {os.DisplayName,-30} {os.OptionCount}{markerText}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {optionSets.Count} option sets");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "retrieving option sets", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
