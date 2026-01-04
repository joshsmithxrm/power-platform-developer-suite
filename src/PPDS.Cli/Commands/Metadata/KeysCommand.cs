using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Lists alternate keys for an entity.
/// </summary>
public static class KeysCommand
{
    /// <summary>
    /// Creates the 'keys' command.
    /// </summary>
    public static Command Create()
    {
        var entityArgument = new Argument<string>("entity")
        {
            Description = "The entity logical name (e.g., 'account')"
        };

        var command = new Command("keys", "List alternate keys for an entity")
        {
            entityArgument,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityArgument);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity!, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
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
                Console.Error.WriteLine($"Retrieving alternate keys for '{entity}'...");
            }

            var keys = await metadataService.GetKeysAsync(entity, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(keys);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"{"Logical Name",-30} {"Display Name",-30} {"Attributes",-30} {"Status"}");
                Console.Error.WriteLine(new string('-', 100));

                foreach (var key in keys)
                {
                    var attributes = string.Join(", ", key.KeyAttributes);
                    var flags = new List<string>();
                    if (key.IsManaged) flags.Add("managed");
                    if (key.EntityKeyIndexStatus != "Active") flags.Add(key.EntityKeyIndexStatus ?? "unknown");

                    var flagText = flags.Count > 0 ? string.Join(", ", flags) : "Active";
                    Console.Error.WriteLine($"  {key.LogicalName,-30} {key.DisplayName,-30} {attributes,-30} {flagText}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {keys.Count} alternate keys");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"retrieving alternate keys for '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
