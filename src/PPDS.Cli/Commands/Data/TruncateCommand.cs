using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Truncate (delete ALL records from) a Dataverse entity.
/// This is a dangerous operation intended for dev/test scenarios like resetting environments.
/// </summary>
public static class TruncateCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create()
    {
        var entityOption = new Option<string>("--entity", "-e")
        {
            Description = "Entity logical name to truncate (delete ALL records)",
            Required = true
        };
        entityOption.Validators.Add(result =>
        {
            var value = result.GetValue(entityOption);
            if (string.IsNullOrWhiteSpace(value))
            {
                result.AddError("Entity name is required");
            }
            else if (value.Contains(' '))
            {
                result.AddError("Entity name must be a valid logical name (no spaces)");
            }
        });

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview record count without deleting",
            DefaultValueFactory = _ => false
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt (required for non-interactive execution)",
            DefaultValueFactory = _ => false
        };

        var batchSizeOption = new Option<int>("--batch-size")
        {
            Description = "Records per delete batch (default: 1000)",
            DefaultValueFactory = _ => 1000
        };
        batchSizeOption.Validators.Add(result =>
        {
            var value = result.GetValue(batchSizeOption);
            if (value < 1 || value > 1000)
            {
                result.AddError("--batch-size must be between 1 and 1000");
            }
        });

        var bypassPluginsOption = new Option<string?>("--bypass-plugins")
        {
            Description = "Bypass custom plugin execution: sync, async, or all (requires prvBypassCustomBusinessLogic privilege)"
        };
        bypassPluginsOption.AcceptOnlyFromAmong("sync", "async", "all");

        var bypassFlowsOption = new Option<bool>("--bypass-flows")
        {
            Description = "Bypass Power Automate flow triggers",
            DefaultValueFactory = _ => false
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Continue deleting on individual record failures",
            DefaultValueFactory = _ => true
        };

        var command = new Command("truncate", "Delete ALL records from an entity (DANGEROUS - use with caution)")
        {
            entityOption,
            dryRunOption,
            forceOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var dryRun = parseResult.GetValue(dryRunOption);
            var force = parseResult.GetValue(forceOption);
            var batchSize = parseResult.GetValue(batchSizeOption);
            var bypassPluginsValue = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var bypassPlugins = DataCommandGroup.ParseBypassPlugins(bypassPluginsValue);

            return await ExecuteAsync(
                entity, dryRun, force, batchSize,
                bypassPlugins, bypassFlows, continueOnError,
                profile, environment, globalOptions,
                cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        bool dryRun,
        bool force,
        int batchSize,
        CustomLogicBypass bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        string? profileName,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        try
        {
            // Connect to Dataverse
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine("Connecting to Dataverse...");
            }

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profileName,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            var bulkExecutor = serviceProvider.GetRequiredService<IBulkOperationExecutor>();

            // Count records
            var count = await CountRecordsAsync(pool, entity, cancellationToken);

            if (count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    WriteJsonResult(new TruncateResult { Success = true, DeletedCount = 0, Entity = entity });
                }
                else
                {
                    Console.Error.WriteLine($"Entity '{entity}' has no records to delete.");
                }
                return ExitCodes.Success;
            }

            // Always show environment context for dangerous operations
            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                var envDisplay = !string.IsNullOrEmpty(connectionInfo.EnvironmentDisplayName)
                    ? $"{connectionInfo.EnvironmentDisplayName} ({connectionInfo.EnvironmentUrl})"
                    : connectionInfo.EnvironmentUrl;
                Console.Error.WriteLine($"Environment: {envDisplay}");
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Entity: {entity}");
                Console.Error.WriteLine($"Records to delete: {count:N0}");
                Console.Error.WriteLine();
            }

            // Dry-run mode: show what would be deleted and exit
            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    WriteJsonResult(new TruncateResult
                    {
                        Success = true,
                        DryRun = true,
                        Entity = entity,
                        RecordCount = count
                    });
                }
                else
                {
                    Console.Error.WriteLine("[Dry-Run] No records deleted.");
                }
                return ExitCodes.Success;
            }

            // Confirmation prompt (unless --force)
            if (!force)
            {
                if (!Console.IsInputRedirected)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Error.WriteLine($"WARNING: This will permanently delete ALL {count:N0} records from '{entity}'.");
                    Console.Error.WriteLine("         This operation cannot be undone.");
                    Console.ResetColor();
                    Console.Error.WriteLine();

                    var expectedConfirmation = $"TRUNCATE {entity} {count}";
                    Console.Error.Write($"Type '{expectedConfirmation}' to confirm, or Ctrl+C to cancel: ");
                    var confirmation = Console.ReadLine();

                    if (confirmation != expectedConfirmation)
                    {
                        Console.Error.WriteLine("Cancelled.");
                        return ExitCodes.Success;
                    }
                    Console.Error.WriteLine();
                }
                else
                {
                    WriteError(globalOptions, "CONFIRMATION_REQUIRED",
                        "Use --force to skip confirmation in non-interactive mode");
                    return ExitCodes.InvalidArguments;
                }
            }

            // Execute truncate
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Deleting all records from '{entity}'...");
                Console.Error.WriteLine();
            }

            var result = await ExecuteTruncateAsync(
                pool, bulkExecutor, entity, batchSize,
                bypassPlugins, bypassFlows, continueOnError,
                globalOptions, cancellationToken);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine();
            }

            // Output results
            if (globalOptions.IsJsonMode)
            {
                WriteJsonResult(new TruncateResult
                {
                    Success = result.IsSuccess,
                    Entity = entity,
                    DeletedCount = result.SuccessCount,
                    FailedCount = result.FailureCount,
                    DurationMs = result.Duration.TotalMilliseconds
                });
            }
            else
            {
                WriteTextResult(entity, result);
            }

            return result.IsSuccess ? ExitCodes.Success : ExitCodes.PartialSuccess;
        }
        catch (OperationCanceledException)
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Truncate cancelled.");
            }
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "truncating entity", debug: globalOptions.Debug);
            if (globalOptions.IsJsonMode)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { success = false, error }, JsonOptions));
            }
            else
            {
                Console.Error.WriteLine($"Error: {error.Message}");
                if (globalOptions.Debug && !string.IsNullOrEmpty(error.Details))
                {
                    Console.Error.WriteLine(error.Details);
                }
            }
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> CountRecordsAsync(
        IDataverseConnectionPool pool,
        string entity,
        CancellationToken cancellationToken)
    {
        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        // Use aggregate query to count records efficiently
        var fetchXml = $@"
            <fetch aggregate='true'>
                <entity name='{entity}'>
                    <attribute name='{entity}id' alias='count' aggregate='count' />
                </entity>
            </fetch>";

        var result = await client.RetrieveMultipleAsync(new FetchExpression(fetchXml), cancellationToken);

        if (result.Entities.Count > 0 && result.Entities[0].Contains("count"))
        {
            var countValue = result.Entities[0]["count"];
            if (countValue is Microsoft.Xrm.Sdk.AliasedValue aliased)
            {
                return Convert.ToInt32(aliased.Value);
            }
        }

        return 0;
    }

    private static async Task<BulkOperationResult> ExecuteTruncateAsync(
        IDataverseConnectionPool pool,
        IBulkOperationExecutor bulkExecutor,
        string entity,
        int batchSize,
        CustomLogicBypass bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var primaryKey = $"{entity}id";
        var allResults = new List<BulkOperationResult>();
        var totalDeleted = 0;
        var totalFailed = 0;
        var startTime = DateTime.UtcNow;

        // Delete in batches until no records remain
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Fetch a batch of IDs
            var ids = await FetchRecordIdsAsync(pool, entity, primaryKey, batchSize, cancellationToken);

            if (ids.Count == 0)
                break;

            // Delete this batch
            var options = new BulkOperationOptions
            {
                BatchSize = batchSize,
                ContinueOnError = continueOnError,
                BypassCustomLogic = bypassPlugins,
                BypassPowerAutomateFlows = bypassFlows
            };

            // Progress reporting
            var batchProgress = new Progress<ProgressSnapshot>(snapshot =>
            {
                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.Write($"\r  Deleted: {totalDeleted + snapshot.Processed:N0} | {snapshot.InstantRatePerSecond:F0}/s");
                }
            });

            var result = await bulkExecutor.DeleteMultipleAsync(
                entity,
                ids,
                options,
                batchProgress,
                cancellationToken);

            totalDeleted += result.SuccessCount;
            totalFailed += result.FailureCount;
            allResults.Add(result);

            // If batch had failures and we're not continuing on error, stop
            if (result.FailureCount > 0 && !continueOnError)
                break;
        }

        // Aggregate results
        var duration = DateTime.UtcNow - startTime;
        var allErrors = allResults.SelectMany(r => r.Errors).ToList();

        return new BulkOperationResult
        {
            SuccessCount = totalDeleted,
            FailureCount = totalFailed,
            Duration = duration,
            Errors = allErrors
        };
    }

    private static async Task<List<Guid>> FetchRecordIdsAsync(
        IDataverseConnectionPool pool,
        string entity,
        string primaryKey,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(primaryKey),
            TopCount = batchSize
        };

        var result = await client.RetrieveMultipleAsync(query, cancellationToken);
        return result.Entities.Select(e => e.Id).ToList();
    }

    private static void WriteTextResult(string entity, BulkOperationResult result)
    {
        Console.Error.WriteLine("Truncate complete");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Entity: {entity}");
        Console.Error.WriteLine($"  Deleted: {result.SuccessCount:N0}");

        if (result.FailureCount > 0)
        {
            Console.Error.WriteLine($"  Failed: {result.FailureCount:N0}");
        }

        Console.Error.WriteLine($"  Duration: {result.Duration:mm\\:ss\\.fff}");

        if (result.Errors.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Errors:");

            var groupedErrors = result.Errors
                .GroupBy(e => e.ErrorCode)
                .OrderByDescending(g => g.Count());

            foreach (var group in groupedErrors.Take(5))
            {
                Console.Error.WriteLine($"  {group.Key}: {group.Count()} occurrence(s)");
                foreach (var error in group.Take(3))
                {
                    Console.Error.WriteLine($"    [{error.RecordId}] {error.Message}");
                }
                if (group.Count() > 3)
                {
                    Console.Error.WriteLine($"    ... and {group.Count() - 3} more");
                }
            }

            if (groupedErrors.Count() > 5)
            {
                Console.Error.WriteLine($"  ... and {groupedErrors.Count() - 5} more error types");
            }
        }
    }

    private static void WriteJsonResult(TruncateResult result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
    }

    private static void WriteError(GlobalOptionValues globalOptions, string code, string message)
    {
        if (globalOptions.IsJsonMode)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                success = false,
                error = new { code, message }
            }, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"Error: {message}");
        }
    }

    private sealed class TruncateResult
    {
        public bool Success { get; init; }
        public bool DryRun { get; init; }
        public string? Entity { get; init; }
        public int RecordCount { get; init; }
        public int DeletedCount { get; init; }
        public int FailedCount { get; init; }
        public double DurationMs { get; init; }
    }
}
