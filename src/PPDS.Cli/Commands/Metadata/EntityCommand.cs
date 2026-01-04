using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Gets full metadata for a specific entity.
/// </summary>
public static class EntityCommand
{
    /// <summary>
    /// Creates the 'entity' command.
    /// </summary>
    public static Command Create()
    {
        var entityArgument = new Argument<string>("entity")
        {
            Description = "The entity logical name (e.g., 'account')"
        };

        var includeOption = new Option<string[]?>("--include")
        {
            Description = "Include specific metadata sections: attributes, relationships, keys, privileges (comma-separated or multiple flags)",
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("entity", "Get full metadata for a specific entity")
        {
            entityArgument,
            MetadataCommandGroup.ProfileOption,
            MetadataCommandGroup.EnvironmentOption,
            includeOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityArgument);
            var profile = parseResult.GetValue(MetadataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(MetadataCommandGroup.EnvironmentOption);
            var include = parseResult.GetValue(includeOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(entity!, profile, environment, include, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        string? profile,
        string? environment,
        string[]? include,
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
                Console.Error.WriteLine($"Retrieving metadata for '{entity}'...");
            }

            var (includeAttrs, includeRels, includeKeys, includePrivs) = ParseIncludeOptions(include);

            var metadata = await metadataService.GetEntityAsync(
                entity,
                includeAttrs,
                includeRels,
                includeKeys,
                includePrivs,
                cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(metadata);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Entity: {metadata.LogicalName}");
                Console.Error.WriteLine($"  Display Name:     {metadata.DisplayName}");
                Console.Error.WriteLine($"  Schema Name:      {metadata.SchemaName}");
                Console.Error.WriteLine($"  Entity Set Name:  {metadata.EntitySetName}");
                Console.Error.WriteLine($"  Primary ID:       {metadata.PrimaryIdAttribute}");
                Console.Error.WriteLine($"  Primary Name:     {metadata.PrimaryNameAttribute}");
                Console.Error.WriteLine($"  Object Type Code: {metadata.ObjectTypeCode}");
                Console.Error.WriteLine($"  Ownership Type:   {metadata.OwnershipType}");
                Console.Error.WriteLine($"  Custom Entity:    {metadata.IsCustomEntity}");
                Console.Error.WriteLine($"  Managed:          {metadata.IsManaged}");

                if (metadata.Attributes.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Attributes ({metadata.Attributes.Count}):");
                    foreach (var attr in metadata.Attributes.Take(20))
                    {
                        var markers = new List<string>();
                        if (attr.IsPrimaryId) markers.Add("PK");
                        if (attr.IsPrimaryName) markers.Add("name");
                        if (attr.IsCustomAttribute) markers.Add("custom");

                        var markerText = markers.Count > 0 ? $" [{string.Join(", ", markers)}]" : "";
                        Console.Error.WriteLine($"  {attr.LogicalName,-35} {attr.AttributeType,-15} {attr.DisplayName}{markerText}");
                    }

                    if (metadata.Attributes.Count > 20)
                    {
                        Console.Error.WriteLine($"  ... and {metadata.Attributes.Count - 20} more attributes");
                    }
                }

                var totalRels = metadata.OneToManyRelationships.Count +
                                metadata.ManyToOneRelationships.Count +
                                metadata.ManyToManyRelationships.Count;

                if (totalRels > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Relationships ({totalRels}):");
                    Console.Error.WriteLine($"  1:N: {metadata.OneToManyRelationships.Count}");
                    Console.Error.WriteLine($"  N:1: {metadata.ManyToOneRelationships.Count}");
                    Console.Error.WriteLine($"  N:N: {metadata.ManyToManyRelationships.Count}");
                }

                if (metadata.Keys.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Alternate Keys ({metadata.Keys.Count}):");
                    foreach (var key in metadata.Keys)
                    {
                        Console.Error.WriteLine($"  {key.LogicalName}: {string.Join(", ", key.KeyAttributes)}");
                    }
                }

                if (metadata.Privileges.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Privileges ({metadata.Privileges.Count}):");
                    foreach (var priv in metadata.Privileges)
                    {
                        Console.Error.WriteLine($"  {priv.PrivilegeType}: {priv.Name}");
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"retrieving entity '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static (bool attrs, bool rels, bool keys, bool privs) ParseIncludeOptions(string[]? include)
    {
        // Default: include all
        if (include == null || include.Length == 0)
        {
            return (true, true, true, true);
        }

        var sections = include
            .SelectMany(i => i.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(s => s.Trim().ToLowerInvariant())
            .ToHashSet();

        return (
            sections.Contains("attributes") || sections.Contains("attrs"),
            sections.Contains("relationships") || sections.Contains("rels"),
            sections.Contains("keys"),
            sections.Contains("privileges") || sections.Contains("privs")
        );
    }
}
