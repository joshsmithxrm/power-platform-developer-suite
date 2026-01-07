using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.PluginTraces;

/// <summary>
/// Delete plugin trace logs.
/// </summary>
public static class DeleteCommand
{
    public static Command Create()
    {
        var traceIdArgument = new Argument<Guid?>("trace-id")
        {
            Description = "The plugin trace ID to delete",
            Arity = ArgumentArity.ZeroOrOne
        };

        var idsOption = new Option<string?>("--ids")
        {
            Description = "Comma-separated list of trace IDs to delete"
        };

        var olderThanOption = new Option<string?>("--older-than")
        {
            Description = "Delete traces older than this duration (e.g., 7d, 24h, 30m)"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Delete ALL plugin traces (requires --force)"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview count without deleting"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt"
        };

        var command = new Command("delete", "Delete plugin trace logs")
        {
            traceIdArgument,
            idsOption,
            olderThanOption,
            allOption,
            dryRunOption,
            forceOption,
            PluginTracesCommandGroup.ProfileOption,
            PluginTracesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var traceId = parseResult.GetValue(traceIdArgument);
            var ids = parseResult.GetValue(idsOption);
            var olderThan = parseResult.GetValue(olderThanOption);
            var all = parseResult.GetValue(allOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(PluginTracesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginTracesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                traceId, ids, olderThan, all, dryRun, force,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid? traceId,
        string? ids,
        string? olderThan,
        bool all,
        bool dryRun,
        bool force,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate: exactly one delete mode must be specified
        var modeCount = (traceId.HasValue ? 1 : 0) +
                        (!string.IsNullOrEmpty(ids) ? 1 : 0) +
                        (!string.IsNullOrEmpty(olderThan) ? 1 : 0) +
                        (all ? 1 : 0);

        if (modeCount == 0)
        {
            var message = "Specify one of: trace-id, --ids, --older-than, or --all";
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                message);
            if (globalOptions.IsJsonMode)
            {
                writer.WriteError(error);
            }
            else
            {
                Console.Error.WriteLine($"Error: {message}");
            }
            return ExitCodes.InvalidArguments;
        }

        if (modeCount > 1)
        {
            var message = "Only one delete mode can be specified at a time.";
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                message);
            if (globalOptions.IsJsonMode)
            {
                writer.WriteError(error);
            }
            else
            {
                Console.Error.WriteLine($"Error: {message}");
            }
            return ExitCodes.InvalidArguments;
        }

        // Validate --all requires --force
        if (all && !force && !dryRun)
        {
            var message = "--all requires --force to confirm deletion of ALL traces, or use --dry-run to preview.";
            var error = new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                message);
            if (globalOptions.IsJsonMode)
            {
                writer.WriteError(error);
            }
            else
            {
                Console.Error.WriteLine($"Error: {message}");
            }
            return ExitCodes.InvalidArguments;
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var traceService = serviceProvider.GetRequiredService<IPluginTraceService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Handle single trace deletion
            if (traceId.HasValue)
            {
                return await DeleteSingleAsync(traceService, traceId.Value, dryRun, globalOptions, writer, cancellationToken);
            }

            // Handle multiple IDs deletion
            if (!string.IsNullOrEmpty(ids))
            {
                var traceIds = ParseIds(ids);
                if (traceIds.Count == 0)
                {
                    var message = "No valid trace IDs provided.";
                    var error = new StructuredError(
                        ErrorCodes.Validation.InvalidArguments,
                        message);
                    if (globalOptions.IsJsonMode)
                    {
                        writer.WriteError(error);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Error: {message}");
                    }
                    return ExitCodes.InvalidArguments;
                }

                return await DeleteByIdsAsync(traceService, traceIds, dryRun, force, globalOptions, writer, cancellationToken);
            }

            // Handle older-than deletion
            if (!string.IsNullOrEmpty(olderThan))
            {
                var duration = ParseDuration(olderThan);
                if (duration == null)
                {
                    var message = $"Invalid duration format: '{olderThan}'. Use formats like 7d, 24h, 30m.";
                    var error = new StructuredError(
                        ErrorCodes.Validation.InvalidValue,
                        message,
                        null,
                        "older-than");
                    if (globalOptions.IsJsonMode)
                    {
                        writer.WriteError(error);
                    }
                    else
                    {
                        Console.Error.WriteLine($"Error: {message}");
                    }
                    return ExitCodes.InvalidArguments;
                }

                return await DeleteOlderThanAsync(traceService, duration.Value, dryRun, force, globalOptions, writer, cancellationToken);
            }

            // Handle delete all
            if (all)
            {
                return await DeleteAllAsync(traceService, dryRun, globalOptions, writer, cancellationToken);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "deleting plugin traces", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> DeleteSingleAsync(
        IPluginTraceService traceService,
        Guid traceId,
        bool dryRun,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            // Check if trace exists
            var trace = await traceService.GetAsync(traceId, cancellationToken);
            var exists = trace != null;

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DryRunOutput { WouldDelete = exists ? 1 : 0 });
            }
            else
            {
                Console.Error.WriteLine(exists
                    ? $"Would delete trace: {traceId}"
                    : $"Trace not found: {traceId}");
            }
            return ExitCodes.Success;
        }

        var deleted = await traceService.DeleteAsync(traceId, cancellationToken);

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new DeleteOutput { Deleted = deleted ? 1 : 0 });
        }
        else
        {
            Console.Error.WriteLine(deleted
                ? $"Deleted trace: {traceId}"
                : $"Trace not found: {traceId}");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> DeleteByIdsAsync(
        IPluginTraceService traceService,
        List<Guid> traceIds,
        bool dryRun,
        bool force,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        if (dryRun)
        {
            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DryRunOutput { WouldDelete = traceIds.Count });
            }
            else
            {
                Console.Error.WriteLine($"Would attempt to delete {traceIds.Count} trace(s).");
            }
            return ExitCodes.Success;
        }

        // Confirm if not forced and more than 1
        if (!force && traceIds.Count > 1 && !globalOptions.IsJsonMode)
        {
            Console.Error.Write($"Delete {traceIds.Count} traces? [y/N] ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response != "y" && response != "yes")
            {
                Console.Error.WriteLine("Cancelled.");
                return ExitCodes.Failure;
            }
        }

        // Create progress reporter
        var progress = new Progress<int>(count =>
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.Write($"\rDeleted {count} of {traceIds.Count}...");
            }
        });

        var deleted = await traceService.DeleteByIdsAsync(traceIds, progress, cancellationToken);

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new DeleteOutput { Deleted = deleted });
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Deleted {deleted} trace(s).");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> DeleteOlderThanAsync(
        IPluginTraceService traceService,
        TimeSpan duration,
        bool dryRun,
        bool force,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        // Get count first
        var cutoff = DateTime.UtcNow - duration;
        var filter = new PluginTraceFilter { CreatedBefore = cutoff };
        var count = await traceService.CountAsync(filter, cancellationToken);

        if (dryRun)
        {
            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DryRunOutput { WouldDelete = count });
            }
            else
            {
                Console.Error.WriteLine($"Would delete {count} trace(s) older than {FormatDuration(duration)}.");
            }
            return ExitCodes.Success;
        }

        if (count == 0)
        {
            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DeleteOutput { Deleted = 0 });
            }
            else
            {
                Console.Error.WriteLine($"No traces older than {FormatDuration(duration)} found.");
            }
            return ExitCodes.Success;
        }

        // Confirm if not forced
        if (!force && !globalOptions.IsJsonMode)
        {
            Console.Error.Write($"Delete {count} traces older than {FormatDuration(duration)}? [y/N] ");
            var response = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (response != "y" && response != "yes")
            {
                Console.Error.WriteLine("Cancelled.");
                return ExitCodes.Failure;
            }
        }

        // Create progress reporter
        var progress = new Progress<int>(current =>
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.Write($"\rDeleted {current}...");
            }
        });

        var deleted = await traceService.DeleteOlderThanAsync(duration, progress, cancellationToken);

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new DeleteOutput { Deleted = deleted });
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Deleted {deleted} trace(s).");
        }

        return ExitCodes.Success;
    }

    private static async Task<int> DeleteAllAsync(
        IPluginTraceService traceService,
        bool dryRun,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        // Get count first
        var count = await traceService.CountAsync(null, cancellationToken);

        if (dryRun)
        {
            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DryRunOutput { WouldDelete = count });
            }
            else
            {
                Console.Error.WriteLine($"Would delete ALL {count} trace(s).");
            }
            return ExitCodes.Success;
        }

        if (count == 0)
        {
            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DeleteOutput { Deleted = 0 });
            }
            else
            {
                Console.Error.WriteLine("No traces to delete.");
            }
            return ExitCodes.Success;
        }

        // Create progress reporter
        var progress = new Progress<int>(current =>
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.Write($"\rDeleted {current} of {count}...");
            }
        });

        var deleted = await traceService.DeleteAllAsync(progress, cancellationToken);

        if (globalOptions.IsJsonMode)
        {
            writer.WriteSuccess(new DeleteOutput { Deleted = deleted });
        }
        else
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Deleted {deleted} trace(s).");
        }

        return ExitCodes.Success;
    }

    private static List<Guid> ParseIds(string idsString)
    {
        var result = new List<Guid>();
        var parts = idsString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (Guid.TryParse(part, out var guid))
            {
                result.Add(guid);
            }
        }

        return result;
    }

    private static TimeSpan? ParseDuration(string durationString)
    {
        if (string.IsNullOrWhiteSpace(durationString))
            return null;

        durationString = durationString.Trim().ToLowerInvariant();

        // Try parsing with different suffixes
        if (durationString.EndsWith('d') &&
            int.TryParse(durationString[..^1], out var days))
        {
            return TimeSpan.FromDays(days);
        }

        if (durationString.EndsWith('h') &&
            int.TryParse(durationString[..^1], out var hours))
        {
            return TimeSpan.FromHours(hours);
        }

        if (durationString.EndsWith('m') &&
            int.TryParse(durationString[..^1], out var minutes))
        {
            return TimeSpan.FromMinutes(minutes);
        }

        // Try parsing as just a number (default to days)
        if (int.TryParse(durationString, out var defaultDays))
        {
            return TimeSpan.FromDays(defaultDays);
        }

        return null;
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
            return $"{(int)duration.TotalDays} day(s)";
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours} hour(s)";
        return $"{(int)duration.TotalMinutes} minute(s)";
    }

    #region Output Models

    private sealed class DeleteOutput
    {
        [JsonPropertyName("deleted")]
        public int Deleted { get; set; }
    }

    private sealed class DryRunOutput
    {
        [JsonPropertyName("wouldDelete")]
        public int WouldDelete { get; set; }
    }

    #endregion
}
