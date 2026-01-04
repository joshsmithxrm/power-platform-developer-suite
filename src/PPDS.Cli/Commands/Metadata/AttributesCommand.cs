using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Lists attributes for an entity.
/// </summary>
public static class AttributesCommand
{
    /// <summary>
    /// Creates the 'attributes' command.
    /// </summary>
    public static Command Create()
    {
        var entityArgument = new Argument<string>("entity")
        {
            Description = "The entity logical name (e.g., 'account')"
        };

        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Filter by attribute type (e.g., 'Lookup', 'String', 'DateTime', 'Picklist')"
        };

        var command = new Command("attributes", "List attributes for an entity")
        {
            entityArgument,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption,
            typeOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityArgument);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var type = parseResult.GetValue(typeOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity!, profile, environment, type, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string? profile,
        string? environment,
        string? type,
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
                Console.Error.WriteLine($"Retrieving attributes for '{entity}'...");
            }

            var attributes = await metadataService.GetAttributesAsync(entity, type, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(attributes);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"{"Logical Name",-35} {"Type",-20} {"Display Name",-30} {"Flags"}");
                Console.Error.WriteLine(new string('-', 100));

                foreach (var attr in attributes)
                {
                    var flags = new List<string>();
                    if (attr.IsPrimaryId) flags.Add("PK");
                    if (attr.IsPrimaryName) flags.Add("name");
                    if (attr.IsCustomAttribute) flags.Add("custom");
                    if (!attr.IsValidForCreate && !attr.IsValidForUpdate) flags.Add("readonly");
                    if (attr.RequiredLevel == "ApplicationRequired" || attr.RequiredLevel == "SystemRequired")
                        flags.Add("required");

                    var flagText = flags.Count > 0 ? string.Join(", ", flags) : "";
                    Console.Error.WriteLine($"  {attr.LogicalName,-35} {attr.AttributeType,-20} {attr.DisplayName,-30} {flagText}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {attributes.Count} attributes");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"retrieving attributes for '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
