using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Metadata;

namespace PPDS.Cli.Commands.Metadata;

/// <summary>
/// Lists relationships for an entity.
/// </summary>
public static class RelationshipsCommand
{
    /// <summary>
    /// Creates the 'relationships' command.
    /// </summary>
    public static Command Create()
    {
        var entityArgument = new Argument<string>("entity")
        {
            Description = "The entity logical name (e.g., 'account')"
        };

        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Filter by relationship type: OneToMany, ManyToOne, ManyToMany"
        };

        var command = new Command("relationships", "List relationships for an entity")
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

            var metadataService = serviceProvider.GetRequiredService<IMetadataQueryService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Retrieving relationships for '{entity}'...");
            }

            var relationships = await metadataService.GetRelationshipsAsync(entity, type, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(relationships);
            }
            else
            {
                Console.WriteLine();

                if (relationships.OneToMany.Count > 0)
                {
                    Console.WriteLine("One-to-Many (1:N) Relationships:");
                    Console.WriteLine($"  {"Schema Name",-45} {"Related Entity",-30} {"Lookup Field"}");
                    Console.WriteLine($"  {new string('-', 90)}");

                    foreach (var rel in relationships.OneToMany)
                    {
                        var customMarker = rel.IsCustomRelationship ? " [custom]" : "";
                        Console.WriteLine($"  {rel.SchemaName,-45} {rel.ReferencingEntity,-30} {rel.ReferencingAttribute}{customMarker}");
                    }

                    Console.WriteLine();
                }

                if (relationships.ManyToOne.Count > 0)
                {
                    Console.WriteLine("Many-to-One (N:1) Relationships:");
                    Console.WriteLine($"  {"Schema Name",-45} {"Referenced Entity",-30} {"Lookup Field"}");
                    Console.WriteLine($"  {new string('-', 90)}");

                    foreach (var rel in relationships.ManyToOne)
                    {
                        var customMarker = rel.IsCustomRelationship ? " [custom]" : "";
                        Console.WriteLine($"  {rel.SchemaName,-45} {rel.ReferencedEntity,-30} {rel.ReferencingAttribute}{customMarker}");
                    }

                    Console.WriteLine();
                }

                if (relationships.ManyToMany.Count > 0)
                {
                    Console.WriteLine("Many-to-Many (N:N) Relationships:");
                    Console.WriteLine($"  {"Schema Name",-45} {"Entity 1",-20} {"Entity 2",-20} {"Intersect Entity"}");
                    Console.WriteLine($"  {new string('-', 100)}");

                    foreach (var rel in relationships.ManyToMany)
                    {
                        var customMarker = rel.IsCustomRelationship ? " [custom]" : "";
                        var reflexiveMarker = rel.IsReflexive ? " [reflexive]" : "";
                        Console.WriteLine($"  {rel.SchemaName,-45} {rel.Entity1LogicalName,-20} {rel.Entity2LogicalName,-20} {rel.IntersectEntityName}{customMarker}{reflexiveMarker}");
                    }

                    Console.WriteLine();
                }

                var total = relationships.OneToMany.Count + relationships.ManyToOne.Count + relationships.ManyToMany.Count;
                Console.WriteLine($"Total: {total} relationships (1:N: {relationships.OneToMany.Count}, N:1: {relationships.ManyToOne.Count}, N:N: {relationships.ManyToMany.Count})");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"retrieving relationships for '{entity}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
