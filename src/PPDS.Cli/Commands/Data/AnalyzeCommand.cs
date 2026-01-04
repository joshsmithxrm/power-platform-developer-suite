using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Migration.Analysis;
using PPDS.Migration.Formats;
using PPDS.Migration.Models;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Analyze a schema file and display dependency information.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create()
    {
        var schemaOption = new Option<FileInfo>("--schema", "-s")
        {
            Description = "Path to schema.xml file",
            Required = true
        }.AcceptExistingOnly();

        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format: text or json",
            DefaultValueFactory = _ => OutputFormat.Text
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose logging output",
            DefaultValueFactory = _ => false
        };

        var debugOption = new Option<bool>("--debug")
        {
            Description = "Enable diagnostic logging output",
            DefaultValueFactory = _ => false
        };

        var command = new Command("analyze", "Analyze schema and display dependency graph")
        {
            schemaOption,
            outputFormatOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schema = parseResult.GetValue(schemaOption)!;
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteAsync(schema, outputFormat, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        FileInfo schema,
        OutputFormat outputFormat,
        bool debug,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(outputFormat, debug);

        try
        {
            // File validation now handled by option validators (AcceptExistingOnly)

            // Create service provider for analysis (no Dataverse connection needed)
            await using var serviceProvider = ServiceFactory.CreateAnalysisProvider();
            var schemaReader = serviceProvider.GetRequiredService<ICmtSchemaReader>();
            var graphBuilder = serviceProvider.GetRequiredService<IDependencyGraphBuilder>();

            // Parse schema
            var migrationSchema = await schemaReader.ReadAsync(schema.FullName, cancellationToken);

            // Build dependency graph
            var graph = graphBuilder.Build(migrationSchema);

            // Convert to analysis result
            var analysis = BuildAnalysis(graph, migrationSchema);

            if (outputFormat == OutputFormat.Json)
            {
                WriteJsonOutput(analysis);
            }
            else
            {
                WriteTextOutput(analysis, schema.FullName);
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Operation.Cancelled,
                "Analysis cancelled by user."));
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "analyzing schema", debug: debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static SchemaAnalysis BuildAnalysis(DependencyGraph graph, MigrationSchema schema)
    {
        // Build tier info
        var tierInfos = new List<TierInfo>();
        for (int i = 0; i < graph.Tiers.Count; i++)
        {
            var tierEntities = graph.Tiers[i];
            var hasCircular = graph.CircularReferences.Any(cr =>
                cr.Entities.Any(e => tierEntities.Contains(e)));

            tierInfos.Add(new TierInfo
            {
                Tier = i + 1,
                Entities = tierEntities.ToArray(),
                HasCircular = hasCircular
            });
        }

        // Extract deferred fields from circular references
        var deferredFields = new Dictionary<string, string[]>();
        foreach (var circular in graph.CircularReferences)
        {
            foreach (var edge in circular.Edges)
            {
                if (!deferredFields.ContainsKey(edge.FromEntity))
                {
                    deferredFields[edge.FromEntity] = [];
                }

                var existing = deferredFields[edge.FromEntity].ToList();
                if (!existing.Contains(edge.FieldName))
                {
                    existing.Add(edge.FieldName);
                    deferredFields[edge.FromEntity] = existing.ToArray();
                }
            }
        }

        // Extract M2M relationships from schema
        var m2mRelationships = schema.Entities
            .SelectMany(e => e.Relationships ?? [])
            .Where(r => r.IsManyToMany)
            .Select(r => r.IntersectEntity ?? r.Name)
            .Distinct()
            .ToArray();

        return new SchemaAnalysis
        {
            EntityCount = graph.Entities.Count,
            DependencyCount = graph.Dependencies.Count,
            CircularReferenceCount = graph.CircularReferences.Count,
            Tiers = tierInfos.ToArray(),
            DeferredFields = deferredFields,
            ManyToManyRelationships = m2mRelationships
        };
    }

    private static void WriteTextOutput(SchemaAnalysis analysis, string schemaPath)
    {
        Console.Error.WriteLine("[Schema Analysis]");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"Schema: {schemaPath}");
        Console.Error.WriteLine();

        if (analysis.EntityCount == 0)
        {
            Console.Error.WriteLine("No entities found in schema.");
            return;
        }

        Console.Error.WriteLine($"Entities: {analysis.EntityCount}");
        Console.Error.WriteLine($"Dependencies: {analysis.DependencyCount}");
        Console.Error.WriteLine($"Circular References: {analysis.CircularReferenceCount}");
        Console.Error.WriteLine();

        Console.Error.WriteLine("Import Tiers:");
        foreach (var tier in analysis.Tiers)
        {
            var entities = string.Join(", ", tier.Entities);
            var suffix = tier.HasCircular ? " (circular)" : "";
            Console.Error.WriteLine($"  Tier {tier.Tier}: {entities}{suffix}");
        }
        Console.Error.WriteLine();

        if (analysis.DeferredFields.Count > 0)
        {
            Console.Error.WriteLine("Deferred Fields:");
            foreach (var (entity, fields) in analysis.DeferredFields)
            {
                foreach (var field in fields)
                {
                    Console.Error.WriteLine($"  {entity}.{field}");
                }
            }
            Console.Error.WriteLine();
        }

        if (analysis.ManyToManyRelationships.Length > 0)
        {
            Console.Error.WriteLine("Many-to-Many Relationships:");
            foreach (var relationship in analysis.ManyToManyRelationships)
            {
                Console.Error.WriteLine($"  {relationship}");
            }
        }
    }

    private static void WriteJsonOutput(SchemaAnalysis analysis)
    {
        var output = new
        {
            entityCount = analysis.EntityCount,
            dependencyCount = analysis.DependencyCount,
            circularReferenceCount = analysis.CircularReferenceCount,
            tiers = analysis.Tiers.Select(t => new
            {
                tier = t.Tier,
                entities = t.Entities,
                hasCircular = t.HasCircular
            }),
            deferredFields = analysis.DeferredFields,
            manyToManyRelationships = analysis.ManyToManyRelationships
        };

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        Console.WriteLine(JsonSerializer.Serialize(output, options));
    }
}

/// <summary>
/// Schema analysis results.
/// </summary>
internal class SchemaAnalysis
{
    public int EntityCount { get; set; }
    public int DependencyCount { get; set; }
    public int CircularReferenceCount { get; set; }
    public TierInfo[] Tiers { get; set; } = [];
    public Dictionary<string, string[]> DeferredFields { get; set; } = new();
    public string[] ManyToManyRelationships { get; set; } = [];
}

/// <summary>
/// Import tier information.
/// </summary>
internal class TierInfo
{
    public int Tier { get; set; }
    public string[] Entities { get; set; } = [];
    public bool HasCircular { get; set; }
}
