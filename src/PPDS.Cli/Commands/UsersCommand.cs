using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.UserMapping;

namespace PPDS.Cli.Commands;

/// <summary>
/// User management commands for migration.
/// </summary>
public static class UsersCommand
{
    public static Command Create()
    {
        var command = new Command("users", "User mapping commands for cross-environment migration");

        command.Subcommands.Add(CreateGenerateCommand());

        return command;
    }

    private static Command CreateGenerateCommand()
    {
        // Profile options
        var profileOption = new Option<string?>("--profile", "-p")
        {
            Description = "Profile for both source and target environments"
        };

        var sourceProfileOption = new Option<string?>("--source-profile")
        {
            Description = "Profile for source environment (overrides --profile for source)"
        };

        var targetProfileOption = new Option<string?>("--target-profile")
        {
            Description = "Profile for target environment (overrides --profile for target)"
        };

        // Environment options
        var sourceEnvOption = new Option<string>("--source-env")
        {
            Description = "Source environment (URL, name, or ID)",
            Required = true
        };

        var targetEnvOption = new Option<string>("--target-env")
        {
            Description = "Target environment (URL, name, or ID)",
            Required = true
        };

        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output user mapping XML file path",
            Required = true
        };
        outputOption.Validators.Add(result =>
        {
            var file = result.GetValue(outputOption);
            if (file?.Directory is { Exists: false })
                result.AddError($"Output directory does not exist: {file.Directory.FullName}");
        });

        var analyzeOption = new Option<bool>("--analyze")
        {
            Description = "Analyze user differences without generating mapping file",
            DefaultValueFactory = _ => false
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output as JSON",
            DefaultValueFactory = _ => false
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

        var command = new Command("generate", "Generate user mapping file from source to target environment")
        {
            profileOption,
            sourceProfileOption,
            targetProfileOption,
            sourceEnvOption,
            targetEnvOption,
            outputOption,
            analyzeOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(profileOption);
            var sourceProfile = parseResult.GetValue(sourceProfileOption);
            var targetProfile = parseResult.GetValue(targetProfileOption);
            var sourceEnv = parseResult.GetValue(sourceEnvOption)!;
            var targetEnv = parseResult.GetValue(targetEnvOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var analyze = parseResult.GetValue(analyzeOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteGenerateAsync(
                profile, sourceProfile, targetProfile,
                sourceEnv, targetEnv, output, analyze,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteGenerateAsync(
        string? profile,
        string? sourceProfile,
        string? targetProfile,
        string sourceEnv,
        string targetEnv,
        FileInfo output,
        bool analyzeOnly,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        try
        {
            // Determine which profiles to use
            var effectiveSourceProfile = sourceProfile ?? profile;
            var effectiveTargetProfile = targetProfile ?? profile;

            var sourceProfileInfo = string.IsNullOrEmpty(effectiveSourceProfile) ? "active profile" : $"profile '{effectiveSourceProfile}'";
            var targetProfileInfo = string.IsNullOrEmpty(effectiveTargetProfile) ? "active profile" : $"profile '{effectiveTargetProfile}'";

            if (!json)
            {
                Console.WriteLine("Generate User Mapping");
                Console.WriteLine(new string('=', 50));
                Console.WriteLine();
                Console.WriteLine($"  Source: {sourceEnv} ({sourceProfileInfo})");
                Console.WriteLine($"  Target: {targetEnv} ({targetProfileInfo})");
                Console.WriteLine();
                Console.WriteLine("  Connecting to environments...");
            }

            // Create providers for both environments
            await using var sourceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                effectiveSourceProfile,
                sourceEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            await using var targetProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                effectiveTargetProfile,
                targetEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var sourcePool = sourceProvider.GetRequiredService<IDataverseConnectionPool>();
            var targetPool = targetProvider.GetRequiredService<IDataverseConnectionPool>();

            // Create generator
            var logger = debug ? sourceProvider.GetService<ILogger<UserMappingGenerator>>() : null;
            var generator = logger != null
                ? new UserMappingGenerator(logger)
                : new UserMappingGenerator();

            if (!json)
            {
                Console.WriteLine("  Querying users from both environments...");
            }

            // Generate mappings
            var result = await generator.GenerateAsync(sourcePool, targetPool, cancellationToken: cancellationToken);

            if (json)
            {
                OutputJson(result, analyzeOnly, output.FullName);
            }
            else
            {
                OutputConsole(result, analyzeOnly, output.FullName);

                if (!analyzeOnly)
                {
                    Console.WriteLine($"  Writing mapping file: {output.FullName}");
                    await generator.WriteAsync(result, output.FullName, cancellationToken);
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"  Success: Generated {result.Mappings.Count} user mappings");
                    Console.ResetColor();
                    Console.WriteLine();
                    Console.WriteLine("  Usage:");
                    Console.WriteLine($"    ppds data import --data <file> --user-mapping \"{output.FullName}\"");
                }
                else
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("  [ANALYZE ONLY] No mapping file generated.");
                    Console.ResetColor();
                }
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.WriteError("Operation cancelled by user.", json);
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError($"Failed to generate user mapping: {ex.Message}", json);
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }

    private static void OutputConsole(UserMappingResult result, bool analyzeOnly, string outputPath)
    {
        Console.WriteLine();
        Console.WriteLine("  Results:");
        Console.WriteLine($"    Source users: {result.SourceUserCount}");
        Console.WriteLine($"    Target users: {result.TargetUserCount}");
        Console.WriteLine();

        Console.Write("    Matched: ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(result.Mappings.Count);
        Console.ResetColor();
        Console.WriteLine($"      By AAD Object ID: {result.MatchedByAadId}");
        Console.WriteLine($"      By Domain Name:   {result.MatchedByDomain}");

        if (result.UnmappedUsers.Count > 0)
        {
            Console.Write("    Unmapped: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(result.UnmappedUsers.Count);
            Console.ResetColor();
        }
        Console.WriteLine();

        // Show sample mappings
        if (result.Mappings.Count > 0)
        {
            Console.WriteLine("  Sample Mappings (first 5):");
            foreach (var mapping in result.Mappings.Take(5))
            {
                Console.WriteLine($"    {mapping.Source.FullName}");
                Console.WriteLine($"      Source: {mapping.Source.SystemUserId}");
                Console.WriteLine($"      Target: {mapping.Target.SystemUserId} (matched by {mapping.MatchedBy})");
            }
            Console.WriteLine();
        }

        // Show unmapped users
        if (result.UnmappedUsers.Count > 0)
        {
            Console.WriteLine("  Unmapped Users (first 10):");
            foreach (var user in result.UnmappedUsers.Take(10))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"    {user.FullName} ({user.DomainName ?? "no domain"})");
                Console.ResetColor();
            }
            Console.WriteLine();
        }
    }

    private static void OutputJson(UserMappingResult result, bool analyzeOnly, string outputPath)
    {
        var output = new
        {
            sourceUserCount = result.SourceUserCount,
            targetUserCount = result.TargetUserCount,
            matchedCount = result.Mappings.Count,
            matchedByAadId = result.MatchedByAadId,
            matchedByDomain = result.MatchedByDomain,
            unmappedCount = result.UnmappedUsers.Count,
            analyzeOnly,
            outputPath = analyzeOnly ? null : outputPath,
            mappings = result.Mappings.Select(m => new
            {
                sourceId = m.Source.SystemUserId,
                sourceName = m.Source.FullName,
                targetId = m.Target.SystemUserId,
                targetName = m.Target.FullName,
                matchedBy = m.MatchedBy
            }),
            unmappedUsers = result.UnmappedUsers.Select(u => new
            {
                id = u.SystemUserId,
                name = u.FullName,
                domain = u.DomainName
            })
        };

        var jsonOutput = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });
        Console.WriteLine(jsonOutput);
    }
}
