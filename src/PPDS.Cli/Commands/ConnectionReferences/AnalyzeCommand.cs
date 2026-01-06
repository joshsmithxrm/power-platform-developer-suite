using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ConnectionReferences;

/// <summary>
/// Analyze flow-connection reference relationships and detect orphans.
/// </summary>
public static class AnalyzeCommand
{
    public static Command Create()
    {
        var orphansOnlyOption = new Option<bool>("--orphans-only")
        {
            Description = "Only show orphaned relationships"
        };

        var command = new Command("analyze", "Analyze flow-connection reference relationships (orphan detection)")
        {
            ConnectionReferencesCommandGroup.SolutionOption,
            orphansOnlyOption,
            ConnectionReferencesCommandGroup.ProfileOption,
            ConnectionReferencesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(ConnectionReferencesCommandGroup.SolutionOption);
            var orphansOnly = parseResult.GetValue(orphansOnlyOption);
            var profile = parseResult.GetValue(ConnectionReferencesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ConnectionReferencesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, orphansOnly, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? solution,
        bool orphansOnly,
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

            var crService = serviceProvider.GetRequiredService<IConnectionReferenceService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var analysis = await crService.AnalyzeAsync(solution, cancellationToken);

            var relationships = orphansOnly
                ? analysis.Relationships.Where(r => r.Type != RelationshipType.FlowToConnectionReference).ToList()
                : analysis.Relationships;

            if (globalOptions.IsJsonMode)
            {
                var output = new AnalysisOutput
                {
                    Summary = new AnalysisSummary
                    {
                        ValidCount = analysis.ValidCount,
                        OrphanedFlowCount = analysis.OrphanedFlowCount,
                        OrphanedConnectionReferenceCount = analysis.OrphanedConnectionReferenceCount,
                        HasOrphans = analysis.HasOrphans
                    },
                    Relationships = relationships.Select(r => new RelationshipOutput
                    {
                        Type = r.Type.ToString(),
                        FlowUniqueName = r.FlowUniqueName,
                        FlowDisplayName = r.FlowDisplayName,
                        ConnectionReferenceLogicalName = r.ConnectionReferenceLogicalName,
                        ConnectionReferenceDisplayName = r.ConnectionReferenceDisplayName,
                        ConnectorId = r.ConnectorId,
                        IsBound = r.IsBound
                    }).ToList()
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine("Flow-Connection Reference Analysis");
                Console.Error.WriteLine("===================================");
                Console.Error.WriteLine();
                Console.Error.WriteLine($"  Valid relationships: {analysis.ValidCount}");
                Console.Error.WriteLine($"  Orphaned flows (missing CRs): {analysis.OrphanedFlowCount}");
                Console.Error.WriteLine($"  Orphaned connection references (unused): {analysis.OrphanedConnectionReferenceCount}");
                Console.Error.WriteLine();

                if (!analysis.HasOrphans && orphansOnly)
                {
                    Console.Error.WriteLine("No orphans detected.");
                    return ExitCodes.Success;
                }

                if (relationships.Count == 0)
                {
                    Console.Error.WriteLine("No relationships found.");
                    return ExitCodes.Success;
                }

                // Group by type for cleaner output
                var orphanedFlows = relationships.Where(r => r.Type == RelationshipType.OrphanedFlow).ToList();
                var orphanedCRs = relationships.Where(r => r.Type == RelationshipType.OrphanedConnectionReference).ToList();
                var validRelationships = relationships.Where(r => r.Type == RelationshipType.FlowToConnectionReference).ToList();

                if (orphanedFlows.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("ORPHANED FLOWS (referencing missing connection references):");
                    Console.Error.WriteLine("------------------------------------------------------------");
                    foreach (var r in orphanedFlows)
                    {
                        Console.Error.WriteLine($"  Flow: {r.FlowDisplayName ?? r.FlowUniqueName}");
                        Console.Error.WriteLine($"    References missing CR: {r.ConnectionReferenceLogicalName}");
                        Console.Error.WriteLine();
                    }
                }

                if (orphanedCRs.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("ORPHANED CONNECTION REFERENCES (not used by any flow):");
                    Console.Error.WriteLine("------------------------------------------------------");
                    foreach (var r in orphanedCRs)
                    {
                        var boundStatus = r.IsBound == true ? "Bound" : "Unbound";
                        Console.Error.WriteLine($"  {r.ConnectionReferenceDisplayName ?? r.ConnectionReferenceLogicalName}");
                        Console.Error.WriteLine($"    Name: {r.ConnectionReferenceLogicalName}  Status: {boundStatus}");
                        if (r.ConnectorId != null)
                        {
                            Console.Error.WriteLine($"    Connector: {r.ConnectorId}");
                        }
                        Console.Error.WriteLine();
                    }
                }

                if (!orphansOnly && validRelationships.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("VALID RELATIONSHIPS:");
                    Console.Error.WriteLine("--------------------");
                    foreach (var r in validRelationships)
                    {
                        var boundStatus = r.IsBound == true ? "Bound" : "Unbound";
                        Console.Error.WriteLine($"  Flow: {r.FlowDisplayName ?? r.FlowUniqueName}");
                        Console.Error.WriteLine($"    -> CR: {r.ConnectionReferenceLogicalName} ({boundStatus})");
                        Console.Error.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "analyzing connection references", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class AnalysisOutput
    {
        [JsonPropertyName("summary")]
        public AnalysisSummary Summary { get; set; } = new();

        [JsonPropertyName("relationships")]
        public List<RelationshipOutput> Relationships { get; set; } = new();
    }

    private sealed class AnalysisSummary
    {
        [JsonPropertyName("validCount")]
        public int ValidCount { get; set; }

        [JsonPropertyName("orphanedFlowCount")]
        public int OrphanedFlowCount { get; set; }

        [JsonPropertyName("orphanedConnectionReferenceCount")]
        public int OrphanedConnectionReferenceCount { get; set; }

        [JsonPropertyName("hasOrphans")]
        public bool HasOrphans { get; set; }
    }

    private sealed class RelationshipOutput
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("flowUniqueName")]
        public string? FlowUniqueName { get; set; }

        [JsonPropertyName("flowDisplayName")]
        public string? FlowDisplayName { get; set; }

        [JsonPropertyName("connectionReferenceLogicalName")]
        public string? ConnectionReferenceLogicalName { get; set; }

        [JsonPropertyName("connectionReferenceDisplayName")]
        public string? ConnectionReferenceDisplayName { get; set; }

        [JsonPropertyName("connectorId")]
        public string? ConnectorId { get; set; }

        [JsonPropertyName("isBound")]
        public bool? IsBound { get; set; }
    }

    #endregion
}
