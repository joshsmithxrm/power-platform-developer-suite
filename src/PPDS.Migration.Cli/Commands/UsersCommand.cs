using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Resilience;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.DependencyInjection;
using PPDS.Migration.UserMapping;

namespace PPDS.Migration.Cli.Commands;

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
        var sourceUrlOption = new Option<string>("--source-url")
        {
            Description = "Source environment URL (e.g., https://dev.crm.dynamics.com)",
            Required = true
        };

        var targetUrlOption = new Option<string>("--target-url")
        {
            Description = "Target environment URL (e.g., https://qa.crm.dynamics.com)",
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
            sourceUrlOption,
            targetUrlOption,
            outputOption,
            analyzeOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sourceUrl = parseResult.GetValue(sourceUrlOption)!;
            var targetUrl = parseResult.GetValue(targetUrlOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var analyze = parseResult.GetValue(analyzeOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteGenerateAsync(
                sourceUrl, targetUrl, output, analyze,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteGenerateAsync(
        string sourceUrl,
        string targetUrl,
        FileInfo output,
        bool analyzeOnly,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!json)
            {
                Console.WriteLine("Generate User Mapping");
                Console.WriteLine(new string('=', 50));
                Console.WriteLine();
            }

            // Create connection pools for both environments
            if (!json)
            {
                Console.WriteLine($"  Source: {sourceUrl}");
                Console.WriteLine($"  Target: {targetUrl}");
                Console.WriteLine();
                Console.WriteLine("  Connecting to environments (interactive auth)...");
            }

            await using var sourceProvider = CreateProviderForUrl(sourceUrl, verbose, debug);
            await using var targetProvider = CreateProviderForUrl(targetUrl, verbose, debug);

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
                    Console.WriteLine($"    ppds-migrate import --data <file> --user-mapping \"{output.FullName}\"");
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

    private static ServiceProvider CreateProviderForUrl(string url, bool verbose, bool debug)
    {
        var services = new ServiceCollection();

        // Configure logging
        services.AddLogging(builder =>
        {
            if (debug)
            {
                builder.SetMinimumLevel(LogLevel.Debug);
            }
            else if (verbose)
            {
                builder.SetMinimumLevel(LogLevel.Information);
            }
            else
            {
                builder.SetMinimumLevel(LogLevel.Warning);
            }

            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "[HH:mm:ss] ";
            });
        });

        // Create device code token provider for interactive authentication
        var tokenProvider = new DeviceCodeTokenProvider(url);

        // Create ServiceClient with device code authentication
        var serviceClient = new ServiceClient(
            new Uri(url),
            tokenProvider.GetTokenAsync,
            useUniqueInstance: true);

        if (!serviceClient.IsReady)
        {
            var error = serviceClient.LastError ?? "Unknown error";
            serviceClient.Dispose();
            throw new InvalidOperationException($"Failed to establish connection. Error: {error}");
        }

        // Wrap in ServiceClientSource for the connection pool
        var source = new ServiceClientSource(
            serviceClient,
            "Interactive",
            maxPoolSize: Math.Max(Environment.ProcessorCount * 4, 16));

        // Create pool options
        var poolOptions = new ConnectionPoolOptions
        {
            Enabled = true,
            MinPoolSize = 0,
            MaxConnectionsPerUser = Math.Max(Environment.ProcessorCount * 4, 16),
            DisableAffinityCookie = true
        };

        // Register services that are normally registered by AddDataverseConnectionPool
        services.AddSingleton<IThrottleTracker, ThrottleTracker>();
        services.AddSingleton<IAdaptiveRateController, AdaptiveRateController>();

        // Register the connection pool with the source
        services.AddSingleton<IDataverseConnectionPool>(sp =>
            new DataverseConnectionPool(
                new[] { source },
                sp.GetRequiredService<IThrottleTracker>(),
                sp.GetRequiredService<IAdaptiveRateController>(),
                poolOptions,
                sp.GetRequiredService<ILogger<DataverseConnectionPool>>()));

        services.AddTransient<IBulkOperationExecutor, BulkOperationExecutor>();

        services.AddDataverseMigration();
        return services.BuildServiceProvider();
    }
}
