using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Progress;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;

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
            Description = "Continue deleting on individual record failures (a record that fails is not retried in the same run)",
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
            var dataQueryService = serviceProvider.GetRequiredService<IDataQueryService>();
            var truncateService = serviceProvider.GetRequiredService<IDataTruncateService>();

            // Count records
            var count = await dataQueryService.CountRecordsAsync(entity, cancellationToken);

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

            // Each record is attempted at most once: the run ends when no record is left that it has not attempted, or
            // when a pass deletes nothing (#1922).
            var result = await truncateService.TruncateAsync(
                entity,
                new DataTruncateOptions
                {
                    BatchSize = batchSize,
                    ContinueOnError = continueOnError,
                    BypassCustomLogic = bypassPlugins,
                    BypassPowerAutomateFlows = bypassFlows,
                    ExpectedCount = count
                },
                globalOptions.IsJsonMode ? null : new ConsoleTruncateProgress(),
                cancellationToken);

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
                    DeletedCount = result.DeletedCount,
                    FailedCount = result.FailedCount,
                    Passes = result.Passes,
                    StopReason = result.StopReason.ToString(),
                    DurationMs = result.Duration.TotalMilliseconds
                });
            }
            else
            {
                WriteTextResult(entity, result);
            }

            return ToExitCode(result);
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

    /// <summary>
    /// 0 when the table was emptied with no failure; 2 when the counts are not certain; otherwise 1 (records failed or
    /// were not attempted).
    /// </summary>
    internal static int ToExitCode(DataTruncateResult result) => result switch
    {
        { IsSuccess: true } => ExitCodes.Success,
        { StopReason: DataTruncateStopReason.DeletedRecordReadAgain } => ExitCodes.Failure,
        _ => ExitCodes.PartialSuccess
    };

    private static string? DescribeStop(DataTruncateResult result) => result.StopReason switch
    {
        DataTruncateStopReason.Completed when result.FailedCount == 0 => null,
        DataTruncateStopReason.Completed => "No record is left to read, although some deletes failed.",
        DataTruncateStopReason.OnlyFailedRecordsRemain =>
            "Stopped: every record left is one that could not be deleted in this run; none was retried.",
        DataTruncateStopReason.TooManyFailedRecords =>
            "Stopped: so many records could not be deleted that the next read held only those; records that were " +
            "not attempted may remain. Fix the cause of the failures and run the command again.",
        DataTruncateStopReason.PassDeletedNothing =>
            "Stopped: a pass deleted no record. Records remain; fix the cause of the failures and run the command again.",
        DataTruncateStopReason.StoppedOnError =>
            "Stopped at the first pass with a failure (--continue-on-error false). Records remain.",
        DataTruncateStopReason.DeletedRecordReadAgain =>
            "Stopped: a record reported as deleted was read again, so the counts above are not certain. Records remain.",
        _ => null
    };

    /// <summary>Renders the truncate progress on stderr.</summary>
    private sealed class ConsoleTruncateProgress : IProgressReporter
    {
        public void ReportProgress(ProgressSnapshot snapshot)
            => Console.Error.Write(
                $"\r  Processed: {snapshot.CurrentItem:N0} of {snapshot.TotalItems:N0} | {snapshot.RecordsPerSecond ?? 0:F0}/s");

        public void ReportPhase(string phase, string? detail = null) { }

        public void ReportWarning(string message) => Console.Error.WriteLine(message);

        public void ReportInfo(string message) => Console.Error.WriteLine(message);
    }

    private static void WriteTextResult(string entity, DataTruncateResult result)
    {
        Console.Error.WriteLine(result.IsSuccess ? "Truncate complete" : "Truncate incomplete");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Entity: {entity}");
        Console.Error.WriteLine($"  Deleted: {result.DeletedCount:N0}");
        Console.Error.WriteLine($"  Failed: {result.FailedCount:N0}");

        Console.Error.WriteLine($"  Duration: {result.Duration:mm\\:ss\\.fff}");

        var stop = DescribeStop(result);
        if (stop != null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(stop);
        }

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
        public int Passes { get; init; }
        public string? StopReason { get; init; }
        public double DurationMs { get; init; }
    }
}
