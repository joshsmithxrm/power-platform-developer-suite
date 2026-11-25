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

    public static Command Create() => Create(null);

    /// <summary>Creates the command; tests pass <paramref name="providerFactory"/> to supply the services.</summary>
    internal static Command Create(
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory)
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
                cancellationToken, providerFactory);
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
        CancellationToken cancellationToken,
        Func<string?, string?, GlobalOptionValues, CancellationToken, Task<ServiceProvider>>? providerFactory = null)
    {
        try
        {
            // Connect to Dataverse
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine("Connecting to Dataverse...");
            }

            await using var serviceProvider = providerFactory is null
                ? await ProfileServiceFactory.CreateFromProfilesAsync(
                    profileName,
                    environment,
                    globalOptions.Verbose,
                    globalOptions.Debug,
                    ProfileServiceFactory.DefaultDeviceCodeCallback,
                    cancellationToken)
                : await providerFactory(profileName, environment, globalOptions, cancellationToken);

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
            var truncateService = serviceProvider.GetRequiredService<IDataTruncateService>();

            // Count records
            var count = await truncateService.CountRecordsAsync(entity, cancellationToken);

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
                    FailedIds = result.FailedIds.Take(MaxFailedIdsInOutput).ToList(),
                    FailedIdsOmitted = Math.Max(0, result.FailedCount - Math.Min(result.FailedIds.Count, MaxFailedIdsInOutput)),
                    RemainingCount = result.RemainingCount,
                    ConfirmedCount = count,
                    Passes = result.Passes,
                    StopReason = result.StopReason.ToString(),
                    DurationMs = result.Duration.TotalMilliseconds
                });
            }
            else
            {
                WriteTextResult(entity, result, count);
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
    /// <summary>The most failed ids the JSON output lists; <c>failedIdsOmitted</c> counts the rest.</summary>
    internal const int MaxFailedIdsInOutput = 100;

    internal static int ToExitCode(DataTruncateResult result) => result switch
    {
        { IsSuccess: true } => ExitCodes.Success,
        { StopReason: DataTruncateStopReason.DeletedRecordReadAgain or DataTruncateStopReason.Cancelled
            or DataTruncateStopReason.Interrupted } => ExitCodes.Failure,
        _ => ExitCodes.PartialSuccess
    };

    internal static string? DescribeStop(DataTruncateResult result) => result.StopReason switch
    {
        DataTruncateStopReason.Completed when result.IsSuccess => null,
        DataTruncateStopReason.Completed when result.FailedCount > 0 =>
            "Every record of the table was attempted once; the records that failed were not retried and remain.",
        DataTruncateStopReason.Completed =>
            "Every record of the table was attempted once, but records remain (for example, created during the run). " +
            "Run the command again to delete them.",
        DataTruncateStopReason.Cancelled =>
            "Cancelled: the counts above are what was deleted before the cancellation; the records after it were not " +
            "attempted and remain.",
        DataTruncateStopReason.Interrupted =>
            $"Stopped: a read or a delete failed ({result.InterruptionCode}); the counts above are what was deleted " +
            "before it, and the records after it were not attempted and remain.",
        DataTruncateStopReason.StoppedOnError =>
            "Stopped at the first pass with a failure (--continue-on-error false); the records after it were not " +
            "attempted and remain.",
        DataTruncateStopReason.DeletedRecordReadAgain =>
            "Stopped: a record already attempted was read again, so the counts above are not certain. Records remain.",
        DataTruncateStopReason.PagingUnavailable =>
            "Stopped: Dataverse returned no way to read past the last page, so records that were not attempted may " +
            "remain. Run the command again.",
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

    /// <summary>
    /// A note when the run deleted more records than the count the user confirmed (records created during the run
    /// after the read position are deleted too), or <see langword="null"/>.
    /// </summary>
    internal static string? DescribeMoreThanConfirmed(DataTruncateResult result, int confirmedCount)
        => result.DeletedCount > confirmedCount
            ? $"Note: {result.DeletedCount:N0} records were deleted, more than the {confirmedCount:N0} confirmed: records " +
              "created while the command ran were deleted too."
            : null;

    private static void WriteTextResult(string entity, DataTruncateResult result, int confirmedCount)
    {
        Console.Error.WriteLine(result.IsSuccess ? "Truncate complete" : "Truncate incomplete");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Entity: {entity}");
        Console.Error.WriteLine($"  Deleted: {result.DeletedCount:N0}");
        Console.Error.WriteLine($"  Failed: {result.FailedCount:N0}");
        Console.Error.WriteLine(result.RemainingCount is { } remaining
            ? $"  Remaining: {remaining:N0}"
            : "  Remaining: could not be counted");

        Console.Error.WriteLine($"  Duration: {result.Duration:mm\\:ss\\.fff}");

        var moreThanConfirmed = DescribeMoreThanConfirmed(result, confirmedCount);
        if (moreThanConfirmed != null)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(moreThanConfirmed);
        }

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
        public List<Guid>? FailedIds { get; init; }
        public int FailedIdsOmitted { get; init; }
        public int? RemainingCount { get; init; }
        public int? ConfirmedCount { get; init; }
        public int Passes { get; init; }
        public string? StopReason { get; init; }
        public double DurationMs { get; init; }
    }
}
