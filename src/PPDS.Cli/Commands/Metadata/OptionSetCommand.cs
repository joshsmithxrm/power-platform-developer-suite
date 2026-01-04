using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Gets details for a specific option set.
/// </summary>
public static class OptionSetCommand
{
    /// <summary>
    /// Creates the 'optionset' command.
    /// </summary>
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The option set name (e.g., 'new_customstatus')"
        };

        var command = new Command("optionset", "Get details for a specific global option set")
        {
            nameArgument,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name!, profile, environment, globalOptions, cancellationToken);
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
                Console.Error.WriteLine($"Retrieving option set '{name}'...");
            }

            var optionSet = await metadataService.GetOptionSetAsync(name, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(optionSet);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Option Set: {optionSet.Name}");
                Console.Error.WriteLine($"  Display Name: {optionSet.DisplayName}");
                Console.Error.WriteLine($"  Type:         {optionSet.OptionSetType}");
                Console.Error.WriteLine($"  Is Global:    {optionSet.IsGlobal}");
                Console.Error.WriteLine($"  Is Custom:    {optionSet.IsCustomOptionSet}");
                Console.Error.WriteLine($"  Is Managed:   {optionSet.IsManaged}");

                if (!string.IsNullOrEmpty(optionSet.Description))
                {
                    Console.Error.WriteLine($"  Description:  {optionSet.Description}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Options ({optionSet.Options.Count}):");
                Console.Error.WriteLine($"  {"Value",-10} {"Label",-40} {"Flags"}");
                Console.Error.WriteLine($"  {new string('-', 70)}");

                foreach (var option in optionSet.Options)
                {
                    var flags = new List<string>();
                    if (option.IsDefault) flags.Add("default");
                    if (option.State.HasValue) flags.Add($"state={option.State}");
                    if (!string.IsNullOrEmpty(option.Color)) flags.Add($"color={option.Color}");

                    var flagText = flags.Count > 0 ? string.Join(", ", flags) : "";
                    Console.Error.WriteLine($"  {option.Value,-10} {option.Label,-40} {flagText}");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"retrieving option set '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
