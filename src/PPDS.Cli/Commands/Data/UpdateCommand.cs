using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.Data;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Update records in a Dataverse entity.
/// Supports single-record update (by ID or alternate key), bulk update from file, and query-based update.
/// Unlike load (upsert), update fails if the record doesn't exist.
/// </summary>
public static class UpdateCommand
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
            Description = "Target entity logical name",
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

        var idOption = new Option<Guid?>("--id")
        {
            Description = "Record ID (GUID) to update, written 8-4-4-4-12 with hyphens (optionally in braces)",
            // The exact GUID rule of the value path, not the lenient parser, which reads a damaged id as another id.
            CustomParser = result =>
            {
                var token = result.Tokens.Count > 0 ? result.Tokens[^1].Value : null;
                if (ColumnValueConverter.TryReadExactGuid(token, out var id))
                {
                    return id;
                }

                result.AddError("--id takes a GUID written 8-4-4-4-12 with hyphens (optionally in braces), nothing else.");
                return null;
            }
        };

        var keyOption = new Option<string?>("--key", "-k")
        {
            Description = "Alternate key field(s) for lookup. Format: field=value or field1=value1,field2=value2 for composite keys."
        };

        var fileOption = new Option<FileInfo?>("--file")
        {
            Description = "Path to a UTF-8 CSV file of records to update: a header row, the table's primary id column (or --id-column) and the columns to update; RFC 4180 quoting. An empty cell clears its column. A file that is not UTF-8, a malformed row, a bad or repeated id or a value not in its column's form refuses the whole file before anything is written"
        };
        fileOption.Validators.Add(result =>
        {
            var file = result.GetValue(fileOption);
            if (file is { Exists: false })
            {
                result.AddError($"File not found: {file.FullName}");
            }
        });

        var idColumnOption = new Option<string?>("--id-column")
        {
            Description = "Column name containing record IDs in CSV file (default: entity primary key)"
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "SQL-like filter expression to match records for update (e.g., \"statecode eq 0\")"
        };

        var setOption = new Option<string?>("--set", "-s")
        {
            Description = "Field values to set: field=value pairs separated by commas. A value with no comma and no double quote is written as is; otherwise wrap the whole value in double quotes and double each quote in it (name=\"Smith, \"\"Jr\"\"\"). Unquoted null clears the column. Each value must be in its column's form (numbers as 1234.5, dates as 2024-01-15T09:30:00Z); anything else is refused before any record is read. Required for --id, --key, or --filter modes."
        };

        var mappingOption = new Option<FileInfo?>("--mapping", "-m")
        {
            Description = "Path to column mapping JSON file (for --file mode)"
        };
        mappingOption.Validators.Add(result =>
        {
            var file = result.GetValue(mappingOption);
            if (file is { Exists: false })
            {
                result.AddError($"Mapping file not found: {file.FullName}");
            }
        });

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt (required for non-interactive execution)",
            DefaultValueFactory = _ => false
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview records that would be updated without actually updating",
            DefaultValueFactory = _ => false
        };

        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum number of records to update (fails if query returns more)"
        };
        limitOption.Validators.Add(result =>
        {
            var value = result.GetValue(limitOption);
            if (value.HasValue && value.Value < 1)
            {
                result.AddError("--limit must be at least 1");
            }
        });

        var batchSizeOption = new Option<int>("--batch-size")
        {
            Description = "Records per batch (default: 100)",
            DefaultValueFactory = _ => 100
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
            Description = "Continue updating on individual record failures",
            DefaultValueFactory = _ => true
        };

        var command = new Command("update", "Update records in a Dataverse entity (fails if record doesn't exist)")
        {
            entityOption,
            idOption,
            keyOption,
            fileOption,
            idColumnOption,
            filterOption,
            setOption,
            mappingOption,
            forceOption,
            dryRunOption,
            limitOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        // Validate input modes and --set requirement
        command.Validators.Add(result =>
        {
            // Whether --id was given, not its value: reading the value of a refused id would throw here.
            var idGiven = result.GetResult(idOption) is not null;
            var key = result.GetValue(keyOption);
            var file = result.GetValue(fileOption);
            var filter = result.GetValue(filterOption);
            var set = result.GetValue(setOption);

            var modeCount = (idGiven ? 1 : 0) +
                           (!string.IsNullOrEmpty(key) ? 1 : 0) +
                           (file != null ? 1 : 0) +
                           (!string.IsNullOrEmpty(filter) ? 1 : 0);

            if (modeCount == 0)
            {
                result.AddError("Specify one of: --id, --key, --file, or --filter");
            }
            else if (modeCount > 1)
            {
                result.AddError("Only one input mode allowed: --id, --key, --file, or --filter");
            }

            // The file gives the values: --set beside --file would be ignored, so it is refused (#1917).
            if (file != null && !string.IsNullOrEmpty(set))
            {
                result.AddError("--set cannot be combined with --file: the file gives the values to write.");
            }

            // --set is required for --id, --key, and --filter modes (not for --file which has values in the file)
            if (file == null && string.IsNullOrEmpty(set) && modeCount == 1)
            {
                result.AddError("--set is required when using --id, --key, or --filter. Use --set \"field=value\" to specify values to update.");
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var id = parseResult.GetValue(idOption);
            var key = parseResult.GetValue(keyOption);
            var file = parseResult.GetValue(fileOption);
            var idColumn = parseResult.GetValue(idColumnOption);
            var filter = parseResult.GetValue(filterOption);
            var set = parseResult.GetValue(setOption);
            var mapping = parseResult.GetValue(mappingOption);
            var force = parseResult.GetValue(forceOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var limit = parseResult.GetValue(limitOption);
            var batchSize = parseResult.GetValue(batchSizeOption);
            var bypassPluginsValue = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            var bypassPlugins = DataCommandGroup.ParseBypassPlugins(bypassPluginsValue);

            return await ExecuteAsync(
                entity, id, key, file, idColumn, filter, set, mapping,
                force, dryRun, limit, batchSize,
                bypassPlugins, bypassFlows, continueOnError,
                profile, environment, globalOptions,
                cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        Guid? id,
        string? key,
        FileInfo? file,
        string? idColumn,
        string? filter,
        string? set,
        FileInfo? mapping,
        bool force,
        bool dryRun,
        int? limit,
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

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var dataQueryService = serviceProvider.GetRequiredService<IDataQueryService>();
            var bulkExecutor = serviceProvider.GetRequiredService<IBulkOperationExecutor>();
            var valueService = serviceProvider.GetRequiredService<IDataUpdateValueService>();

            // The --set values are converted by each column's type from metadata before any record is read (#1917).
            var setValues = file == null
                ? await valueService.ConvertSetValuesAsync(entity, set!, cancellationToken)
                : null;

            // Build entities to update based on input mode
            List<Entity> entitiesToUpdate;

            if (id.HasValue)
            {
                var updateEntity = new Entity(entity, id.Value);
                ApplySetValues(updateEntity, setValues!);
                entitiesToUpdate = [updateEntity];
            }
            else if (!string.IsNullOrEmpty(key))
            {
                var resolvedId = await dataQueryService.ResolveByAlternateKeyAsync(entity, key, cancellationToken);
                if (resolvedId == null)
                {
                    WriteError(globalOptions, "RECORD_NOT_FOUND", $"No record found with key: {key}");
                    return ExitCodes.NotFoundError;
                }
                var updateEntity = new Entity(entity, resolvedId.Value);
                ApplySetValues(updateEntity, setValues!);
                entitiesToUpdate = [updateEntity];
            }
            else if (file != null)
            {
                entitiesToUpdate = (await valueService.ReadUpdateFileAsync(
                    entity, file.FullName, idColumn, mapping?.FullName, cancellationToken)).ToList();
                if (entitiesToUpdate.Count == 0)
                {
                    WriteError(globalOptions, "NO_RECORDS", "No records found in file");
                    return ExitCodes.ValidationError;
                }
            }
            else if (!string.IsNullOrEmpty(filter))
            {
                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine("Querying records to update...");
                }
                var ids = await dataQueryService.QueryIdsByFilterAsync(entity, filter, limit, cancellationToken);
                if (ids.Count == 0)
                {
                    if (!globalOptions.IsJsonMode)
                    {
                        Console.Error.WriteLine("No records match the filter.");
                    }
                    else
                    {
                        WriteJsonResult(new UpdateResult { Success = true, UpdatedCount = 0 });
                    }
                    return ExitCodes.Success;
                }

                // Build update entities with the --set values
                entitiesToUpdate = ids.Select(recordId =>
                {
                    var updateEntity = new Entity(entity, recordId);
                    ApplySetValues(updateEntity, setValues!);
                    return updateEntity;
                }).ToList();
            }
            else
            {
                WriteError(globalOptions, "INVALID_INPUT", "No input mode specified");
                return ExitCodes.InvalidArguments;
            }

            // Check limit
            if (limit.HasValue && entitiesToUpdate.Count > limit.Value)
            {
                WriteError(globalOptions, "LIMIT_EXCEEDED",
                    $"Query returned {entitiesToUpdate.Count} records, exceeds --limit {limit.Value}");
                return ExitCodes.ValidationError;
            }

            // Show preview and get confirmation
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Records to update: {entitiesToUpdate.Count}");

                // Show fields being updated
                var sampleEntity = entitiesToUpdate[0];
                var fieldsToUpdate = sampleEntity.Attributes.Keys.ToList();
                Console.Error.WriteLine($"Fields to update: {string.Join(", ", fieldsToUpdate)}");

                if (entitiesToUpdate.Count <= 10)
                {
                    Console.Error.WriteLine();
                    foreach (var updateEntity in entitiesToUpdate)
                    {
                        Console.Error.WriteLine($"  {updateEntity.Id}");
                    }
                }
                else
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"  (showing first 5 of {entitiesToUpdate.Count})");
                    foreach (var updateEntity in entitiesToUpdate.Take(5))
                    {
                        Console.Error.WriteLine($"  {updateEntity.Id}");
                    }
                    Console.Error.WriteLine("  ...");
                }

                Console.Error.WriteLine();
            }

            // Dry-run mode: show what would be updated and exit
            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    WriteJsonResult(new UpdateResult
                    {
                        Success = true,
                        DryRun = true,
                        RecordCount = entitiesToUpdate.Count,
                        RecordIds = entitiesToUpdate.Select(e => e.Id).ToList()
                    });
                }
                else
                {
                    Console.Error.WriteLine("[Dry-Run] No records updated.");
                }
                return ExitCodes.Success;
            }

            // Confirmation prompt (unless --force)
            if (!force)
            {
                if (!Console.IsInputRedirected)
                {
                    Console.Error.Write($"Type 'update {entitiesToUpdate.Count}' to confirm, or Ctrl+C to cancel: ");
                    var confirmation = Console.ReadLine();

                    if (confirmation != $"update {entitiesToUpdate.Count}")
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

            // Execute update
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Updating {entitiesToUpdate.Count} record(s) in '{entity}'...");
                Console.Error.WriteLine();
            }

            var options = new BulkOperationOptions
            {
                BatchSize = batchSize,
                ContinueOnError = continueOnError,
                BypassCustomLogic = bypassPlugins,
                BypassPowerAutomateFlows = bypassFlows
            };

            // Progress reporting
            var progress = new Progress<ProgressSnapshot>(snapshot =>
            {
                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.Write($"\r  Progress: {snapshot.Processed:N0}/{snapshot.Total:N0} " +
                        $"({snapshot.PercentComplete:F1}%) | {snapshot.InstantRatePerSecond:F0}/s");
                }
            });

            var result = await bulkExecutor.UpdateMultipleAsync(
                entity,
                entitiesToUpdate,
                options,
                progress: progress,
                cancellationToken: cancellationToken);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine();
            }

            // Output results
            if (globalOptions.IsJsonMode)
            {
                WriteJsonResult(new UpdateResult
                {
                    Success = result.IsSuccess,
                    UpdatedCount = result.SuccessCount,
                    FailedCount = result.FailureCount,
                    DurationMs = result.Duration.TotalMilliseconds,
                    Errors = result.Errors.Select(e => new UpdateErrorInfo
                    {
                        RecordId = e.RecordId,
                        ErrorCode = e.ErrorCode.ToString(),
                        Message = e.Message
                    }).ToList()
                });
            }
            else
            {
                WriteTextResult(result);
            }

            return result.IsSuccess ? ExitCodes.Success : ExitCodes.PartialSuccess;
        }
        catch (OperationCanceledException)
        {
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Update cancelled.");
            }
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating records", debug: globalOptions.Debug);
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

    private static void ApplySetValues(Entity entity, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var (field, value) in values)
        {
            entity[field] = value;
        }
    }

    private static void WriteTextResult(BulkOperationResult result)
    {
        Console.Error.WriteLine("Update complete");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Updated: {result.SuccessCount:N0}");

        if (result.FailureCount > 0)
        {
            Console.Error.WriteLine($"  Failed: {result.FailureCount:N0}");
        }

        Console.Error.WriteLine($"  Duration: {result.Duration:mm\\:ss\\.fff}");

        if (result.Errors.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Errors:");

            // Group errors by error code
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

    private static void WriteJsonResult(UpdateResult result)
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

    private sealed class UpdateResult
    {
        public bool Success { get; init; }
        public bool DryRun { get; init; }
        public int RecordCount { get; init; }
        public int UpdatedCount { get; init; }
        public int FailedCount { get; init; }
        public double DurationMs { get; init; }
        public List<Guid>? RecordIds { get; init; }
        public List<UpdateErrorInfo>? Errors { get; init; }
    }

    private sealed class UpdateErrorInfo
    {
        public Guid? RecordId { get; init; }
        public string? ErrorCode { get; init; }
        public string? Message { get; init; }
    }
}
