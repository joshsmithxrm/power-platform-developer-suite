using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Formats;
using PPDS.Migration.Progress;
using PPDS.Migration.Schema;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Schema generation and management commands.
/// </summary>
public static class SchemaCommand
{
    public static Command Create()
    {
        var command = new Command("schema", "Generate and manage migration schemas");

        command.Subcommands.Add(CreateGenerateCommand());
        command.Subcommands.Add(CreateListCommand());

        return command;
    }

    private static Command CreateGenerateCommand()
    {
        var entitiesOption = new Option<string[]>("--entities", "-e")
        {
            Description = "Entity logical names to include (comma-separated or multiple -e flags)",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };

        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output schema file path",
            Required = true
        }.AcceptLegalFileNamesOnly();
        outputOption.Validators.Add(result =>
        {
            var file = result.GetValue(outputOption);
            if (file?.Directory is { Exists: false })
                result.AddError($"Output directory does not exist: {file.Directory.FullName}");
        });

        var includeSystemFieldsOption = new Option<bool>("--include-system-fields")
        {
            Description = "Include system fields (createdon, modifiedon, etc.)",
            DefaultValueFactory = _ => false
        };

        var includeRelationshipsOption = new Option<bool>("--include-relationships")
        {
            Description = "Include relationship definitions",
            DefaultValueFactory = _ => true
        };

        var disablePluginsOption = new Option<bool>("--disable-plugins")
        {
            Description = "Set disableplugins=true on all entities",
            DefaultValueFactory = _ => false
        };

        var includeAttributesOption = new Option<string[]?>("--include-attributes", "-a")
        {
            Description = "Only include these attributes (whitelist, comma-separated or multiple flags)",
            AllowMultipleArgumentsPerToken = true
        };

        var excludeAttributesOption = new Option<string[]?>("--exclude-attributes")
        {
            Description = "Exclude these attributes (blacklist, comma-separated)",
            AllowMultipleArgumentsPerToken = true
        };

        var excludePatternsOption = new Option<string[]?>("--exclude-patterns")
        {
            Description = "Exclude attributes matching patterns (e.g., 'new_*', '*_base')",
            AllowMultipleArgumentsPerToken = true
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output progress as JSON",
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

        var command = new Command("generate", "Generate a migration schema from Dataverse metadata")
        {
            entitiesOption,
            outputOption,
            includeSystemFieldsOption,
            includeRelationshipsOption,
            disablePluginsOption,
            includeAttributesOption,
            excludeAttributesOption,
            excludePatternsOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entities = parseResult.GetValue(entitiesOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var url = parseResult.GetValue(Program.UrlOption);
            var authMode = parseResult.GetValue(Program.AuthOption);
            var includeSystemFields = parseResult.GetValue(includeSystemFieldsOption);
            var includeRelationships = parseResult.GetValue(includeRelationshipsOption);
            var disablePlugins = parseResult.GetValue(disablePluginsOption);
            var includeAttributes = parseResult.GetValue(includeAttributesOption);
            var excludeAttributes = parseResult.GetValue(excludeAttributesOption);
            var excludePatterns = parseResult.GetValue(excludePatternsOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            // Resolve authentication
            AuthResolver.AuthResult authResult;
            try
            {
                authResult = AuthResolver.Resolve(authMode, url);
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                return ExitCodes.InvalidArguments;
            }

            // Parse entities (handle comma-separated and multiple flags)
            var entityList = entities
                .SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entityList.Count == 0)
            {
                ConsoleOutput.WriteError("No entities specified.", json);
                return ExitCodes.InvalidArguments;
            }

            // Parse attribute lists (handle comma-separated)
            var includeAttrList = ParseAttributeList(includeAttributes);
            var excludeAttrList = ParseAttributeList(excludeAttributes);
            var excludePatternList = ParseAttributeList(excludePatterns);

            return await ExecuteGenerateAsync(
                authResult, entityList, output,
                includeSystemFields, includeRelationships, disablePlugins,
                includeAttrList, excludeAttrList, excludePatternList,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static Command CreateListCommand()
    {
        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Filter entities by name pattern (e.g., 'account*' or '*custom*')"
        };

        var customOnlyOption = new Option<bool>("--custom-only")
        {
            Description = "Show only custom entities",
            DefaultValueFactory = _ => false
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output as JSON",
            DefaultValueFactory = _ => false
        };

        var command = new Command("list", "List available entities in Dataverse")
        {
            filterOption,
            customOnlyOption,
            jsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filter = parseResult.GetValue(filterOption);
            var url = parseResult.GetValue(Program.UrlOption);
            var authMode = parseResult.GetValue(Program.AuthOption);
            var customOnly = parseResult.GetValue(customOnlyOption);
            var json = parseResult.GetValue(jsonOption);

            // Resolve authentication
            AuthResolver.AuthResult authResult;
            try
            {
                authResult = AuthResolver.Resolve(authMode, url);
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                return ExitCodes.InvalidArguments;
            }

            return await ExecuteListAsync(authResult, filter, customOnly, json, cancellationToken);
        });

        return command;
    }

    private static List<string>? ParseAttributeList(string[]? input)
    {
        if (input == null || input.Length == 0)
            return null;

        return input
            .SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<int> ExecuteGenerateAsync(
        AuthResolver.AuthResult authResult,
        List<string> entities,
        FileInfo output,
        bool includeSystemFields,
        bool includeRelationships,
        bool disablePlugins,
        List<string>? includeAttributes,
        List<string>? excludeAttributes,
        List<string>? excludePatterns,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            // Report what we're doing
            var optionsMsg = new List<string>();
            if (includeAttributes != null) optionsMsg.Add($"include: {string.Join(",", includeAttributes)}");
            if (excludeAttributes != null) optionsMsg.Add($"exclude: {string.Join(",", excludeAttributes)}");
            if (excludePatterns != null) optionsMsg.Add($"patterns: {string.Join(",", excludePatterns)}");

            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Generating schema for {entities.Count} entities..." +
                          (optionsMsg.Count > 0 ? $" ({string.Join(", ", optionsMsg)})" : "")
            });

            // Report connecting status with auth mode info
            var authModeInfo = authResult.Mode switch
            {
                AuthMode.Interactive => " (interactive login)",
                AuthMode.Managed => " (managed identity)",
                AuthMode.Env => " (environment variables)",
                _ => ""
            };
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Connecting to Dataverse ({authResult.Url}){authModeInfo}..."
            });

            // Create service provider based on auth mode
            await using var serviceProvider = ServiceFactory.CreateProviderForAuthMode(authResult, verbose, debug);
            var generator = serviceProvider.GetRequiredService<ISchemaGenerator>();
            var schemaWriter = serviceProvider.GetRequiredService<ICmtSchemaWriter>();

            var options = new SchemaGeneratorOptions
            {
                IncludeSystemFields = includeSystemFields,
                IncludeRelationships = includeRelationships,
                DisablePluginsByDefault = disablePlugins,
                IncludeAttributes = includeAttributes,
                ExcludeAttributes = excludeAttributes,
                ExcludeAttributePatterns = excludePatterns
            };

            var schema = await generator.GenerateAsync(
                entities, options, progressReporter, cancellationToken);

            await schemaWriter.WriteAsync(schema, output.FullName, cancellationToken);

            var totalFields = schema.Entities.Sum(e => e.Fields.Count);
            var totalRelationships = schema.Entities.Sum(e => e.Relationships.Count);

            progressReporter.Complete(new MigrationResult
            {
                Success = true,
                RecordsProcessed = schema.Entities.Count,
                SuccessCount = schema.Entities.Count,
                FailureCount = 0,
                Duration = TimeSpan.Zero
            });

            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Complete,
                Message = $"Output: {output.FullName} ({schema.Entities.Count} entities, {totalFields} fields, {totalRelationships} relationships)"
            });

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Schema generation cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            progressReporter.Error(ex, "Schema generation failed");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }

    private static async Task<int> ExecuteListAsync(
        AuthResolver.AuthResult authResult,
        string? filter,
        bool customOnly,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build auth mode info for status messages
            var authModeInfo = authResult.Mode switch
            {
                AuthMode.Interactive => " (interactive login)",
                AuthMode.Managed => " (managed identity)",
                AuthMode.Env => " (environment variables)",
                _ => ""
            };

            if (!json)
            {
                Console.WriteLine($"Connecting to Dataverse ({authResult.Url}){authModeInfo}...");
                Console.WriteLine("Retrieving available entities...");
            }

            // Create service provider based on auth mode
            await using var serviceProvider = ServiceFactory.CreateProviderForAuthMode(authResult);
            var generator = serviceProvider.GetRequiredService<ISchemaGenerator>();

            var entities = await generator.GetAvailableEntitiesAsync(cancellationToken);

            // Apply filters
            var filtered = entities.AsEnumerable();

            if (customOnly)
            {
                filtered = filtered.Where(e => e.IsCustomEntity);
            }

            if (!string.IsNullOrEmpty(filter))
            {
                var pattern = filter.Replace("*", "");
                if (filter.StartsWith('*') && filter.EndsWith('*'))
                {
                    filtered = filtered.Where(e => e.LogicalName.Contains(pattern, StringComparison.OrdinalIgnoreCase));
                }
                else if (filter.StartsWith('*'))
                {
                    filtered = filtered.Where(e => e.LogicalName.EndsWith(pattern, StringComparison.OrdinalIgnoreCase));
                }
                else if (filter.EndsWith('*'))
                {
                    filtered = filtered.Where(e => e.LogicalName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase));
                }
                else
                {
                    filtered = filtered.Where(e => e.LogicalName.Equals(filter, StringComparison.OrdinalIgnoreCase));
                }
            }

            var result = filtered.ToList();

            if (json)
            {
                var jsonOutput = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                Console.WriteLine(jsonOutput);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine($"{"Logical Name",-40} {"Display Name",-40} {"Custom"}");
                Console.WriteLine(new string('-', 90));

                foreach (var entity in result)
                {
                    var customMarker = entity.IsCustomEntity ? "Yes" : "";
                    Console.WriteLine($"{entity.LogicalName,-40} {entity.DisplayName,-40} {customMarker}");
                }

                Console.WriteLine();
                Console.WriteLine($"Total: {result.Count} entities");
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
            ConsoleOutput.WriteError($"Failed to list entities: {ex.Message}", json);
            return ExitCodes.Failure;
        }
    }
}
