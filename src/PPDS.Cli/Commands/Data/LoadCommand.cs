using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.CsvLoader;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.BulkOperations;
using PPDS.Dataverse.Pooling;
using PPDS.Dataverse.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Load CSV data into a Dataverse entity.
/// </summary>
public static class LoadCommand
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

        var fileOption = new Option<FileInfo>("--file", "-f")
        {
            Description = "Path to CSV file",
            Required = true
        }.AcceptExistingOnly();

        var keyOption = new Option<string?>("--key", "-k")
        {
            Description = "Alternate key field(s) for upsert. Comma-separated for composite keys."
        };

        var mappingOption = new Option<FileInfo?>("--mapping", "-m")
        {
            Description = "Path to column mapping JSON file"
        };
        mappingOption.Validators.Add(result =>
        {
            var file = result.GetValue(mappingOption);
            if (file is { Exists: false })
            {
                result.AddError($"Mapping file not found: {file.FullName}");
            }
        });

        var generateMappingOption = new Option<FileInfo?>("--generate-mapping")
        {
            Description = "Generate mapping template to specified file"
        };
        generateMappingOption.Validators.Add(result =>
        {
            var file = result.GetValue(generateMappingOption);
            if (file?.Directory is { Exists: false })
            {
                result.AddError($"Output directory does not exist: {file.Directory.FullName}");
            }
        });

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Validate without writing to Dataverse",
            DefaultValueFactory = _ => false
        };

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
            Description = "Continue loading on individual record failures",
            DefaultValueFactory = _ => true
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Force loading even when auto-mapping is incomplete (unmatched columns will be skipped)",
            DefaultValueFactory = _ => false
        };

        var analyzeOption = new Option<bool>("--analyze")
        {
            Description = "Analyze mapping without loading data (preview which columns match)",
            DefaultValueFactory = _ => false
        };

        var outputFormatOption = new Option<OutputFormat>("--output-format", "-o")
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

        var command = new Command("load", "Load CSV data into a Dataverse entity")
        {
            entityOption,
            fileOption,
            keyOption,
            mappingOption,
            generateMappingOption,
            dryRunOption,
            batchSizeOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            forceOption,
            analyzeOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            outputFormatOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entity = parseResult.GetValue(entityOption)!;
            var file = parseResult.GetValue(fileOption)!;
            var key = parseResult.GetValue(keyOption);
            var mappingFile = parseResult.GetValue(mappingOption);
            var generateMappingFile = parseResult.GetValue(generateMappingOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var batchSize = parseResult.GetValue(batchSizeOption);
            var bypassPluginsValue = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var force = parseResult.GetValue(forceOption);
            var analyze = parseResult.GetValue(analyzeOption);
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            var bypassPlugins = DataCommandGroup.ParseBypassPlugins(bypassPluginsValue);

            return await ExecuteAsync(
                entity, file, key, mappingFile, generateMappingFile,
                dryRun, batchSize, bypassPlugins, bypassFlows, continueOnError, force, analyze,
                profile, environment, outputFormat, verbose, debug,
                cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string entity,
        FileInfo file,
        string? key,
        FileInfo? mappingFile,
        FileInfo? generateMappingFile,
        bool dryRun,
        int batchSize,
        CustomLogicBypass bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        bool force,
        bool analyze,
        string? profileName,
        string? environment,
        OutputFormat outputFormat,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        try
        {
            // Connect to Dataverse
            Console.Error.WriteLine($"Connecting to Dataverse...");

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profileName,
                environment,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            if (outputFormat != OutputFormat.Json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();

            // Handle --generate-mapping mode
            if (generateMappingFile != null)
            {
                return await GenerateMappingAsync(
                    pool, entity, file.FullName, generateMappingFile.FullName,
                    outputFormat, cancellationToken);
            }

            // Handle --analyze mode
            if (analyze)
            {
                return await AnalyzeMappingAsync(
                    pool, entity, file.FullName, outputFormat, cancellationToken);
            }

            // Load mapping file if provided
            CsvMappingConfig? mapping = null;
            if (mappingFile != null)
            {
                Console.Error.WriteLine($"Loading mapping from {mappingFile.Name}...");
                var mappingJson = await File.ReadAllTextAsync(mappingFile.FullName, cancellationToken);
                mapping = JsonSerializer.Deserialize<CsvMappingConfig>(mappingJson, JsonOptions);
            }

            // Create load options
            var loadOptions = new CsvLoadOptions
            {
                EntityLogicalName = entity,
                AlternateKeyFields = key,
                Mapping = mapping,
                BatchSize = batchSize,
                BypassPlugins = bypassPlugins,
                BypassFlows = bypassFlows,
                ContinueOnError = continueOnError,
                DryRun = dryRun,
                Force = force
            };

            // Execute load
            var bulkExecutor = serviceProvider.GetRequiredService<IBulkOperationExecutor>();
            var logger = serviceProvider.GetService<ILogger<CsvDataLoader>>();
            var loader = new CsvDataLoader(pool, bulkExecutor, logger);

            Console.Error.WriteLine($"Loading {file.Name} into '{entity}'...");
            Console.Error.WriteLine();

            // Progress reporting
            var progress = new Progress<ProgressSnapshot>(snapshot =>
            {
                if (outputFormat != OutputFormat.Json)
                {
                    Console.Error.Write($"\r  Progress: {snapshot.Processed:N0}/{snapshot.Total:N0} " +
                        $"({snapshot.PercentComplete:F1}%) | {snapshot.InstantRatePerSecond:F0}/s");
                }
            });

            var result = await loader.LoadAsync(file.FullName, loadOptions, progress, cancellationToken);

            Console.Error.WriteLine();
            Console.Error.WriteLine();

            // Output results
            if (outputFormat == OutputFormat.Json)
            {
                WriteJsonResult(result);
            }
            else
            {
                WriteTextResult(result, dryRun);
            }

            return result.Success ? ExitCodes.Success : ExitCodes.PartialSuccess;
        }
        catch (MappingIncompleteException ex)
        {
            Console.Error.WriteLine();
            if (outputFormat == OutputFormat.Json)
            {
                WriteJsonMappingError(ex);
            }
            else
            {
                WriteTextMappingError(ex);
            }
            return ExitCodes.MappingRequired;
        }
        catch (MappingValidationException ex)
        {
            Console.Error.WriteLine();
            if (outputFormat == OutputFormat.Json)
            {
                WriteJsonValidationError(ex);
            }
            else
            {
                WriteTextValidationError(ex);
            }
            return ExitCodes.ValidationError;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Load cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }

    private static async Task<int> GenerateMappingAsync(
        IDataverseConnectionPool pool,
        string entityName,
        string csvPath,
        string outputPath,
        OutputFormat outputFormat,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"Retrieving metadata for '{entityName}'...");

        await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);

        var request = new RetrieveEntityRequest
        {
            LogicalName = entityName,
            EntityFilters = EntityFilters.Attributes
        };

        var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken);
        var entityMetadata = response.EntityMetadata;

        Console.Error.WriteLine($"Generating mapping from {Path.GetFileName(csvPath)}...");

        var generator = new MappingGenerator();
        var config = await generator.GenerateAsync(csvPath, entityMetadata, cancellationToken);

        var json = JsonSerializer.Serialize(config, JsonOptions);
        await File.WriteAllTextAsync(outputPath, json, cancellationToken);

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Mapping file generated: {outputPath}");
        Console.Error.WriteLine();

        // Show summary
        var autoMatched = config.Columns.Count(c => c.Value.Status == "auto-matched");
        var needsConfig = config.Columns.Count(c => c.Value.Status == "needs-configuration");
        var noMatch = config.Columns.Count(c => c.Value.Status == "no-match");

        Console.Error.WriteLine($"  Columns: {config.Columns.Count}");
        Console.Error.WriteLine($"    Auto-matched: {autoMatched}");
        if (needsConfig > 0)
        {
            Console.Error.WriteLine($"    Needs configuration: {needsConfig} (lookup fields)");
        }
        if (noMatch > 0)
        {
            Console.Error.WriteLine($"    No match (will skip): {noMatch}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Next steps:");
        Console.Error.WriteLine($"  1. Edit {Path.GetFileName(outputPath)} to configure lookup fields");
        Console.Error.WriteLine($"  2. Run: ppds data load --entity {entityName} --file {Path.GetFileName(csvPath)} --mapping {Path.GetFileName(outputPath)}");

        return ExitCodes.Success;
    }

    private static async Task<int> AnalyzeMappingAsync(
        IDataverseConnectionPool pool,
        string entityName,
        string csvPath,
        OutputFormat outputFormat,
        CancellationToken cancellationToken)
    {
        Console.Error.WriteLine($"Analyzing mapping for '{entityName}'...");

        var loader = new CsvDataLoader(pool, null!, null);
        var analysis = await loader.AnalyzeAsync(csvPath, entityName, cancellationToken);

        Console.Error.WriteLine();

        if (outputFormat == OutputFormat.Json)
        {
            WriteJsonAnalysis(analysis);
        }
        else
        {
            WriteTextAnalysis(analysis);
        }

        return ExitCodes.Success;
    }

    private static void WriteTextAnalysis(MappingAnalysis analysis)
    {
        Console.Error.WriteLine($"Mapping Analysis for '{analysis.Entity}'");
        Console.Error.WriteLine($"  Match rate: {analysis.MatchRate:P0} ({analysis.MatchedColumns}/{analysis.TotalColumns} columns)");
        if (analysis.Prefix != null)
        {
            Console.Error.WriteLine($"  Publisher prefix: {analysis.Prefix}");
        }
        Console.Error.WriteLine();

        // Show matched columns
        var matched = analysis.Columns.Where(c => c.IsMatched).ToList();
        if (matched.Count > 0)
        {
            Console.Error.WriteLine("Matched columns:");
            foreach (var col in matched)
            {
                var lookupIndicator = col.IsLookup ? " [Lookup]" : "";
                Console.Error.WriteLine($"  + {col.CsvColumn} → {col.TargetAttribute} ({col.MatchType}){lookupIndicator}");
            }
            Console.Error.WriteLine();
        }

        // Show unmatched columns
        var unmatched = analysis.Columns.Where(c => !c.IsMatched).ToList();
        if (unmatched.Count > 0)
        {
            Console.Error.WriteLine("Unmatched columns:");
            foreach (var col in unmatched)
            {
                var suggestions = col.Suggestions != null && col.Suggestions.Count > 0
                    ? $" (did you mean: {string.Join(", ", col.Suggestions)}?)"
                    : "";
                Console.Error.WriteLine($"  - {col.CsvColumn}{suggestions}");
            }
            Console.Error.WriteLine();
        }

        // Show recommendations
        if (analysis.Recommendations.Count > 0)
        {
            Console.Error.WriteLine("Recommendations:");
            foreach (var rec in analysis.Recommendations)
            {
                Console.Error.WriteLine($"  * {rec}");
            }
        }
    }

    private static void WriteJsonAnalysis(MappingAnalysis analysis)
    {
        var output = new
        {
            entity = analysis.Entity,
            matchRate = analysis.MatchRate,
            totalColumns = analysis.TotalColumns,
            matchedColumns = analysis.MatchedColumns,
            isComplete = analysis.IsComplete,
            prefix = analysis.Prefix,
            columns = analysis.Columns.Select(c => new
            {
                csvColumn = c.CsvColumn,
                isMatched = c.IsMatched,
                targetAttribute = c.TargetAttribute,
                matchType = c.MatchType,
                attributeType = c.AttributeType,
                isLookup = c.IsLookup,
                suggestions = c.Suggestions,
                sampleValues = c.SampleValues
            }),
            recommendations = analysis.Recommendations
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void WriteTextResult(LoadResult result, bool dryRun)
    {
        if (dryRun)
        {
            Console.Error.WriteLine("Dry-run complete (no records written)");
        }
        else
        {
            Console.Error.WriteLine("Load complete");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  Total rows: {result.TotalRows:N0}");
        Console.Error.WriteLine($"  Successful: {result.SuccessCount:N0}");

        if (result.CreatedCount.HasValue && result.UpdatedCount.HasValue)
        {
            Console.Error.WriteLine($"    Created: {result.CreatedCount:N0}");
            Console.Error.WriteLine($"    Updated: {result.UpdatedCount:N0}");
        }

        if (result.FailureCount > 0)
        {
            Console.Error.WriteLine($"  Failed: {result.FailureCount:N0}");
        }

        if (result.SkippedCount > 0)
        {
            Console.Error.WriteLine($"  Skipped: {result.SkippedCount:N0}");
        }

        Console.Error.WriteLine($"  Duration: {result.Duration:mm\\:ss\\.fff}");

        if (result.Warnings.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Warnings:");
            foreach (var warning in result.Warnings.Take(10))
            {
                Console.Error.WriteLine($"  {warning}");
            }
            if (result.Warnings.Count > 10)
            {
                Console.Error.WriteLine($"  ... and {result.Warnings.Count - 10} more");
            }
        }

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
                    var rowInfo = error.RowNumber > 0 ? $"Row {error.RowNumber}" : "";
                    var colInfo = !string.IsNullOrEmpty(error.Column) ? $", Column '{error.Column}'" : "";
                    Console.Error.WriteLine($"    [{rowInfo}{colInfo}] {error.Message}");
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

    private static void WriteJsonResult(LoadResult result)
    {
        var output = new
        {
            success = result.Success,
            totalRows = result.TotalRows,
            successCount = result.SuccessCount,
            failureCount = result.FailureCount,
            createdCount = result.CreatedCount,
            updatedCount = result.UpdatedCount,
            skippedCount = result.SkippedCount,
            durationMs = result.Duration.TotalMilliseconds,
            warnings = result.Warnings,
            errors = result.Errors.Select(e => new
            {
                rowNumber = e.RowNumber,
                column = e.Column,
                errorCode = e.ErrorCode,
                message = e.Message,
                value = e.Value
            })
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void WriteTextMappingError(MappingIncompleteException ex)
    {
        Console.Error.WriteLine($"Auto-mapping incomplete: {ex.MatchedColumns}/{ex.TotalColumns} columns matched");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Unmatched columns:");
        foreach (var col in ex.UnmatchedColumns)
        {
            var suggestions = col.Suggestions != null && col.Suggestions.Count > 0
                ? $" → did you mean: {string.Join(", ", col.Suggestions)}?"
                : " → no similar attributes found";
            Console.Error.WriteLine($"  • {col.ColumnName}{suggestions}");
        }
        Console.Error.WriteLine();
        Console.Error.WriteLine("Options:");
        Console.Error.WriteLine("  1. Run with --generate-mapping to create a mapping file for review");
        Console.Error.WriteLine("  2. Run with --force to skip unmatched columns");
    }

    private static void WriteJsonMappingError(MappingIncompleteException ex)
    {
        var output = new
        {
            success = false,
            error = new
            {
                code = "MAPPING_INCOMPLETE",
                message = $"{ex.UnmatchedColumns.Count} column(s) could not be auto-mapped",
                matchedColumns = ex.MatchedColumns,
                totalColumns = ex.TotalColumns,
                unmatchedColumns = ex.UnmatchedColumns.Select(c => new
                {
                    column = c.ColumnName,
                    suggestions = c.Suggestions ?? []
                }),
                suggestion = "Use --generate-mapping to create a mapping file, or --force to skip unmatched columns"
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }

    private static void WriteTextValidationError(MappingValidationException ex)
    {
        Console.Error.WriteLine("Mapping file validation failed");
        Console.Error.WriteLine();

        if (ex.UnconfiguredColumns.Count > 0)
        {
            Console.Error.WriteLine("Columns with no field configured (set 'field' or 'skip: true'):");
            foreach (var col in ex.UnconfiguredColumns)
            {
                Console.Error.WriteLine($"  • {col}");
            }
            Console.Error.WriteLine();
        }

        if (ex.MissingMappings.Count > 0)
        {
            Console.Error.WriteLine("CSV columns not found in mapping file:");
            foreach (var col in ex.MissingMappings)
            {
                Console.Error.WriteLine($"  • {col}");
            }
            Console.Error.WriteLine();
        }

        if (ex.StaleMappings.Count > 0)
        {
            Console.Error.WriteLine("Warning: Mapping entries not found in CSV (stale entries):");
            foreach (var col in ex.StaleMappings)
            {
                Console.Error.WriteLine($"  • {col}");
            }
            Console.Error.WriteLine();
        }

        Console.Error.WriteLine("Update the mapping file to configure all columns, then retry.");
    }

    private static void WriteJsonValidationError(MappingValidationException ex)
    {
        var output = new
        {
            success = false,
            error = new
            {
                code = "MAPPING_VALIDATION_FAILED",
                message = ex.Message,
                unconfiguredColumns = ex.UnconfiguredColumns,
                missingMappings = ex.MissingMappings,
                staleMappings = ex.StaleMappings,
                suggestion = "Update the mapping file to configure all columns (set 'field' or 'skip: true')"
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
    }
}
