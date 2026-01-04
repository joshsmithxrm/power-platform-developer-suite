using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.UserMapping;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// User mapping command for cross-environment data migration.
/// </summary>
public static class UsersCommand
{
    public static Command Create()
    {
        var sourceProfileOption = new Option<string?>("--source-profile", "-sp")
        {
            Description = "Authentication profile for source environment (defaults to active profile)"
        };

        var targetProfileOption = new Option<string?>("--target-profile", "-tp")
        {
            Description = "Authentication profile for target environment (defaults to active profile)"
        };

        var sourceEnvOption = new Option<string>("--source-env", "-se")
        {
            Description = "Source environment - accepts URL, friendly name, unique name, or ID",
            Required = true
        };

        var targetEnvOption = new Option<string>("--target-env", "-te")
        {
            Description = "Target environment - accepts URL, friendly name, unique name, or ID",
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

        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
        {
            Description = "Output format",
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

        var command = new Command("users", "Generate user mapping file from source to target environment")
        {
            sourceProfileOption,
            targetProfileOption,
            sourceEnvOption,
            targetEnvOption,
            outputOption,
            analyzeOption,
            outputFormatOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceProfile = parseResult.GetValue(sourceProfileOption);
            var targetProfile = parseResult.GetValue(targetProfileOption);
            var sourceEnv = parseResult.GetValue(sourceEnvOption)!;
            var targetEnv = parseResult.GetValue(targetEnvOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var analyze = parseResult.GetValue(analyzeOption);
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteAsync(
                sourceProfile, targetProfile,
                sourceEnv, targetEnv, output, analyze,
                outputFormat, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? sourceProfileName,
        string? targetProfileName,
        string sourceEnv,
        string targetEnv,
        FileInfo output,
        bool analyzeOnly,
        OutputFormat outputFormat,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(outputFormat, debug);

        try
        {
            // Create service providers - factory handles environment resolution automatically
            await using var sourceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                sourceProfileName,
                sourceEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            await using var targetProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                targetProfileName,
                targetEnv,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            if (outputFormat != OutputFormat.Json)
            {
                var sourceConnectionInfo = sourceProvider.GetRequiredService<ResolvedConnectionInfo>();
                var targetConnectionInfo = targetProvider.GetRequiredService<ResolvedConnectionInfo>();

                ConsoleHeader.WriteConnectedAsLabeled("Source", sourceConnectionInfo);
                ConsoleHeader.WriteConnectedAsLabeled("Target", targetConnectionInfo);
                Console.Error.WriteLine();
            }

            var sourcePool = sourceProvider.GetRequiredService<IDataverseConnectionPool>();
            var targetPool = targetProvider.GetRequiredService<IDataverseConnectionPool>();

            var logger = debug ? sourceProvider.GetService<ILogger<UserMappingGenerator>>() : null;
            var generator = logger != null
                ? new UserMappingGenerator(logger)
                : new UserMappingGenerator();

            if (outputFormat != OutputFormat.Json)
            {
                Console.Error.WriteLine("  Querying users from both environments...");
            }

            var result = await generator.GenerateAsync(sourcePool, targetPool, cancellationToken: cancellationToken);

            if (outputFormat == OutputFormat.Json)
            {
                OutputJson(result, analyzeOnly, output.FullName);
            }
            else
            {
                OutputConsole(result, analyzeOnly, output.FullName);

                if (!analyzeOnly)
                {
                    Console.Error.WriteLine($"  Writing mapping file: {output.FullName}");
                    await generator.WriteAsync(result, output.FullName, cancellationToken);
                    Console.Error.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Error.WriteLine($"  Success: Generated {result.Mappings.Count} user mappings");
                    Console.ResetColor();
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("  Usage:");
                    Console.Error.WriteLine($"    ppds data import --data <file> --user-mapping \"{output.FullName}\"");
                }
                else
                {
                    Console.Error.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine("  [ANALYZE ONLY] No mapping file generated.");
                    Console.ResetColor();
                }
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Operation.Cancelled,
                "Operation cancelled by user."));
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "generating user mapping", debug: debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void OutputConsole(UserMappingResult result, bool analyzeOnly, string outputPath)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Results:");
        Console.Error.WriteLine($"    Source users: {result.SourceUserCount}");
        Console.Error.WriteLine($"    Target users: {result.TargetUserCount}");
        Console.Error.WriteLine();

        Console.Error.Write("    Matched: ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Error.WriteLine(result.Mappings.Count);
        Console.ResetColor();
        Console.Error.WriteLine($"      By AAD Object ID: {result.MatchedByAadId}");
        Console.Error.WriteLine($"      By Domain Name:   {result.MatchedByDomain}");

        if (result.UnmappedUsers.Count > 0)
        {
            Console.Error.Write("    Unmapped: ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Error.WriteLine(result.UnmappedUsers.Count);
            Console.ResetColor();
        }
        Console.Error.WriteLine();

        // Show sample mappings
        if (result.Mappings.Count > 0)
        {
            Console.Error.WriteLine("  Sample Mappings (first 5):");
            foreach (var mapping in result.Mappings.Take(5))
            {
                Console.Error.WriteLine($"    {mapping.Source.FullName}");
                Console.Error.WriteLine($"      Source: {mapping.Source.SystemUserId}");
                Console.Error.WriteLine($"      Target: {mapping.Target.SystemUserId} (matched by {mapping.MatchedBy})");
            }
            Console.Error.WriteLine();
        }

        // Show unmapped users
        if (result.UnmappedUsers.Count > 0)
        {
            Console.Error.WriteLine("  Unmapped Users (first 10):");
            foreach (var user in result.UnmappedUsers.Take(10))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Error.WriteLine($"    {user.FullName} ({user.DomainName ?? "no domain"})");
                Console.ResetColor();
            }
            Console.Error.WriteLine();
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
