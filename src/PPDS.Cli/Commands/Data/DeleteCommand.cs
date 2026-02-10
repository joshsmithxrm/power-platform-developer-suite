using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;
using PPDS.Query.Parsing;
using PPDS.Query.Transpilation;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Delete records from a Dataverse entity.
/// Supports single-record delete (by ID or alternate key), bulk delete from file, and query-based delete.
/// </summary>
public static class DeleteCommand
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
            Description = "Record ID (GUID) to delete"
        };

        var keyOption = new Option<string?>("--key", "-k")
        {
            Description = "Alternate key field(s) for lookup. Format: field=value or field1=value1,field2=value2 for composite keys."
        };

        var fileOption = new Option<FileInfo?>("--file")
        {
            Description = "Path to file containing record IDs (JSON array or CSV with ID column)"
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
            Description = "SQL-like filter expression to match records for deletion (e.g., \"name like '%test%'\")"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip confirmation prompt (required for non-interactive execution)",
            DefaultValueFactory = _ => false
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview records that would be deleted without actually deleting",
            DefaultValueFactory = _ => false
        };

        var limitOption = new Option<int?>("--limit")
        {
            Description = "Maximum number of records to delete (fails if query returns more)"
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
            Description = "Continue deleting on individual record failures",
            DefaultValueFactory = _ => true
        };

        var command = new Command("delete", "Delete records from a Dataverse entity")
        {
            entityOption,
            idOption,
            keyOption,
            fileOption,
            idColumnOption,
            filterOption,
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

        // Validate that exactly one input mode is provided
        command.Validators.Add(result =>
        {
            var id = result.GetValue(idOption);
            var key = result.GetValue(keyOption);
            var file = result.GetValue(fileOption);
            var filter = result.GetValue(filterOption);

            var modeCount = (id.HasValue ? 1 : 0) +
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
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var id = parseResult.GetValue(idOption);
            var key = parseResult.GetValue(keyOption);
            var file = parseResult.GetValue(fileOption);
            var idColumn = parseResult.GetValue(idColumnOption);
            var filter = parseResult.GetValue(filterOption);
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
                entity, id, key, file, idColumn, filter,
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

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            var bulkExecutor = serviceProvider.GetRequiredService<IBulkOperationExecutor>();

            // Resolve IDs to delete based on input mode
            List<Guid> idsToDelete;

            if (id.HasValue)
            {
                idsToDelete = [id.Value];
            }
            else if (!string.IsNullOrEmpty(key))
            {
                var resolvedId = await ResolveAlternateKeyAsync(pool, entity, key, cancellationToken);
                if (resolvedId == null)
                {
                    WriteError(globalOptions, "RECORD_NOT_FOUND", $"No record found with key: {key}");
                    return ExitCodes.NotFoundError;
                }
                idsToDelete = [resolvedId.Value];
            }
            else if (file != null)
            {
                idsToDelete = await LoadIdsFromFileAsync(file, idColumn, entity, pool, cancellationToken);
                if (idsToDelete.Count == 0)
                {
                    WriteError(globalOptions, "NO_RECORDS", "No record IDs found in file");
                    return ExitCodes.ValidationError;
                }
            }
            else if (!string.IsNullOrEmpty(filter))
            {
                idsToDelete = await QueryIdsAsync(pool, entity, filter, limit, globalOptions, cancellationToken);
                if (idsToDelete.Count == 0)
                {
                    if (!globalOptions.IsJsonMode)
                    {
                        Console.Error.WriteLine("No records match the filter.");
                    }
                    else
                    {
                        WriteJsonResult(new DeleteResult { Success = true, DeletedCount = 0 });
                    }
                    return ExitCodes.Success;
                }
            }
            else
            {
                WriteError(globalOptions, "INVALID_INPUT", "No input mode specified");
                return ExitCodes.InvalidArguments;
            }

            // Check limit
            if (limit.HasValue && idsToDelete.Count > limit.Value)
            {
                WriteError(globalOptions, "LIMIT_EXCEEDED",
                    $"Query returned {idsToDelete.Count} records, exceeds --limit {limit.Value}");
                return ExitCodes.ValidationError;
            }

            // Show preview and get confirmation
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Records to delete: {idsToDelete.Count}");

                if (idsToDelete.Count <= 10)
                {
                    Console.Error.WriteLine();
                    foreach (var deleteId in idsToDelete)
                    {
                        Console.Error.WriteLine($"  {deleteId}");
                    }
                }
                else
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"  (showing first 5 of {idsToDelete.Count})");
                    foreach (var deleteId in idsToDelete.Take(5))
                    {
                        Console.Error.WriteLine($"  {deleteId}");
                    }
                    Console.Error.WriteLine("  ...");
                }

                Console.Error.WriteLine();
            }

            // Dry-run mode: show what would be deleted and exit
            if (dryRun)
            {
                if (globalOptions.IsJsonMode)
                {
                    WriteJsonResult(new DeleteResult
                    {
                        Success = true,
                        DryRun = true,
                        RecordCount = idsToDelete.Count,
                        RecordIds = idsToDelete
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
                    Console.Error.Write($"Type 'delete {idsToDelete.Count}' to confirm, or Ctrl+C to cancel: ");
                    var confirmation = Console.ReadLine();

                    if (confirmation != $"delete {idsToDelete.Count}")
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

            // Execute delete
            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Deleting {idsToDelete.Count} record(s) from '{entity}'...");
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

            var result = await bulkExecutor.DeleteMultipleAsync(
                entity,
                idsToDelete,
                options,
                progress,
                cancellationToken);

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine();
            }

            // Output results
            if (globalOptions.IsJsonMode)
            {
                WriteJsonResult(new DeleteResult
                {
                    Success = result.IsSuccess,
                    DeletedCount = result.SuccessCount,
                    FailedCount = result.FailureCount,
                    DurationMs = result.Duration.TotalMilliseconds,
                    Errors = result.Errors.Select(e => new DeleteErrorInfo
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
                Console.Error.WriteLine("Delete cancelled.");
            }
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "deleting records", debug: globalOptions.Debug);
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

    private static async Task<Guid?> ResolveAlternateKeyAsync(
        IDataverseConnectionPool pool,
        string entity,
        string keyString,
        CancellationToken cancellationToken)
    {
        // Parse key=value pairs and build a query
        var query = new QueryExpression(entity)
        {
            ColumnSet = new ColumnSet(false),
            TopCount = 1
        };

        foreach (var pair in keyString.Split(','))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2)
            {
                throw new ArgumentException($"Invalid key format: {pair}. Expected field=value.");
            }
            query.Criteria.AddCondition(parts[0].Trim(), ConditionOperator.Equal, parts[1].Trim());
        }

        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        var results = await client.RetrieveMultipleAsync(query, cancellationToken);
        return results.Entities.Count > 0 ? results.Entities[0].Id : null;
    }

    private static async Task<List<Guid>> LoadIdsFromFileAsync(
        FileInfo file,
        string? idColumn,
        string entity,
        IDataverseConnectionPool pool,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(file.FullName, cancellationToken);
        content = content.Trim();

        // Try JSON first
        if (content.StartsWith('[') || content.StartsWith('{'))
        {
            return ParseJsonIds(content);
        }

        // Assume CSV
        return ParseCsvIds(content, idColumn, entity);
    }

    private static List<Guid> ParseJsonIds(string content)
    {
        var ids = new List<Guid>();

        using var doc = JsonDocument.Parse(content);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String && Guid.TryParse(element.GetString(), out var id))
                {
                    ids.Add(id);
                }
                else if (element.ValueKind == JsonValueKind.Object)
                {
                    // Look for common ID property names
                    foreach (var prop in element.EnumerateObject())
                    {
                        if (prop.Name.EndsWith("id", StringComparison.OrdinalIgnoreCase) &&
                            prop.Value.ValueKind == JsonValueKind.String &&
                            Guid.TryParse(prop.Value.GetString(), out var objId))
                        {
                            ids.Add(objId);
                            break;
                        }
                    }
                }
            }
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                 doc.RootElement.TryGetProperty("records", out var records) &&
                 records.ValueKind == JsonValueKind.Array)
        {
            // Handle { "records": [...] } format
            return ParseJsonIds(records.GetRawText());
        }

        return ids;
    }

    private static List<Guid> ParseCsvIds(string content, string? idColumn, string entity)
    {
        var ids = new List<Guid>();
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        if (lines.Length == 0) return ids;

        // Parse header using same CSV parser as data rows for consistency
        var headers = ParseCsvLine(lines[0]).Select(h => h.Trim()).ToList();

        // Find ID column
        var idColIndex = -1;
        if (!string.IsNullOrEmpty(idColumn))
        {
            idColIndex = headers.FindIndex(h => h.Equals(idColumn, StringComparison.OrdinalIgnoreCase));
            if (idColIndex < 0)
            {
                throw new ArgumentException($"Column '{idColumn}' not found in CSV. Available: {string.Join(", ", headers)}");
            }
        }
        else
        {
            // Try to find primary key column (entityid or id)
            idColIndex = headers.FindIndex(h =>
                h.Equals($"{entity}id", StringComparison.OrdinalIgnoreCase) ||
                h.Equals("id", StringComparison.OrdinalIgnoreCase));

            if (idColIndex < 0)
            {
                throw new ArgumentException($"Could not find ID column. Specify --id-column. Available: {string.Join(", ", headers)}");
            }
        }

        // Parse data rows
        for (var i = 1; i < lines.Length; i++)
        {
            var values = ParseCsvLine(lines[i]).Select(v => v.Trim()).ToList();
            if (values.Count > idColIndex && Guid.TryParse(values[idColIndex], out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        foreach (var c in line)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                // Don't append quotes - just toggle state
            }
            else if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static async Task<List<Guid>> QueryIdsAsync(
        IDataverseConnectionPool pool,
        string entity,
        string filter,
        int? limit,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        if (!globalOptions.IsJsonMode)
        {
            Console.Error.WriteLine("Querying records to delete...");
        }

        // Build SQL query and transpile to FetchXML
        var sql = $"SELECT {entity}id FROM {entity} WHERE {filter}";
        if (limit.HasValue)
        {
            sql = $"SELECT TOP {limit.Value + 1} {entity}id FROM {entity} WHERE {filter}";
        }

        var parser = new QueryParser();
        var stmt = parser.ParseStatement(sql);
        var generator = new FetchXmlGenerator();
        var fetchXml = generator.Generate(stmt);

        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        var query = new FetchExpression(fetchXml);
        var results = await client.RetrieveMultipleAsync(query, cancellationToken);

        var ids = new List<Guid>();
        var primaryKeyAttribute = $"{entity}id";

        foreach (var record in results.Entities)
        {
            if (record.Contains(primaryKeyAttribute) && record[primaryKeyAttribute] is Guid id)
            {
                ids.Add(id);
            }
            else
            {
                ids.Add(record.Id);
            }
        }

        return ids;
    }

    private static void WriteTextResult(BulkOperationResult result)
    {
        Console.Error.WriteLine("Delete complete");
        Console.Error.WriteLine();
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

    private static void WriteJsonResult(DeleteResult result)
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

    private sealed class DeleteResult
    {
        public bool Success { get; init; }
        public bool DryRun { get; init; }
        public int RecordCount { get; init; }
        public int DeletedCount { get; init; }
        public int FailedCount { get; init; }
        public double DurationMs { get; init; }
        public List<Guid>? RecordIds { get; init; }
        public List<DeleteErrorInfo>? Errors { get; init; }
    }

    private sealed class DeleteErrorInfo
    {
        public Guid? RecordId { get; init; }
        public string? ErrorCode { get; init; }
        public string? Message { get; init; }
    }
}
