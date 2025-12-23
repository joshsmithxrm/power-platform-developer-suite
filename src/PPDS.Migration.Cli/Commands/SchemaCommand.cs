using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Formats;
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

        command.AddCommand(CreateGenerateCommand());
        command.AddCommand(CreateListCommand());

        return command;
    }

    private static Command CreateGenerateCommand()
    {
        var connectionOption = new Option<string?>(
            aliases: ["--connection", "-c"],
            description: ConnectionResolver.GetHelpDescription(ConnectionResolver.ConnectionEnvVar));

        var entitiesOption = new Option<string[]>(
            aliases: ["--entities", "-e"],
            description: "Entity logical names to include (comma-separated or multiple -e flags)")
        {
            IsRequired = true,
            AllowMultipleArgumentsPerToken = true
        };

        var outputOption = new Option<FileInfo>(
            aliases: ["--output", "-o"],
            description: "Output schema file path")
        {
            IsRequired = true
        };

        var includeSystemFieldsOption = new Option<bool>(
            name: "--include-system-fields",
            getDefaultValue: () => false,
            description: "Include system fields (createdon, modifiedon, etc.)");

        var includeRelationshipsOption = new Option<bool>(
            name: "--include-relationships",
            getDefaultValue: () => true,
            description: "Include relationship definitions");

        var disablePluginsOption = new Option<bool>(
            name: "--disable-plugins",
            getDefaultValue: () => false,
            description: "Set disableplugins=true on all entities");

        var includeAttributesOption = new Option<string[]?>(
            aliases: ["--include-attributes", "-a"],
            description: "Only include these attributes (whitelist, comma-separated or multiple flags)")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var excludeAttributesOption = new Option<string[]?>(
            name: "--exclude-attributes",
            description: "Exclude these attributes (blacklist, comma-separated)")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var excludePatternsOption = new Option<string[]?>(
            name: "--exclude-patterns",
            description: "Exclude attributes matching patterns (e.g., 'new_*', '*_base')")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Output progress as JSON");

        var verboseOption = new Option<bool>(
            aliases: ["--verbose", "-v"],
            getDefaultValue: () => false,
            description: "Verbose output");

        var command = new Command("generate", "Generate a migration schema from Dataverse metadata")
        {
            connectionOption,
            entitiesOption,
            outputOption,
            includeSystemFieldsOption,
            includeRelationshipsOption,
            disablePluginsOption,
            includeAttributesOption,
            excludeAttributesOption,
            excludePatternsOption,
            jsonOption,
            verboseOption
        };

        command.SetHandler(async (context) =>
        {
            var connectionArg = context.ParseResult.GetValueForOption(connectionOption);
            var entities = context.ParseResult.GetValueForOption(entitiesOption)!;
            var output = context.ParseResult.GetValueForOption(outputOption)!;
            var includeSystemFields = context.ParseResult.GetValueForOption(includeSystemFieldsOption);
            var includeRelationships = context.ParseResult.GetValueForOption(includeRelationshipsOption);
            var disablePlugins = context.ParseResult.GetValueForOption(disablePluginsOption);
            var includeAttributes = context.ParseResult.GetValueForOption(includeAttributesOption);
            var excludeAttributes = context.ParseResult.GetValueForOption(excludeAttributesOption);
            var excludePatterns = context.ParseResult.GetValueForOption(excludePatternsOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);

            // Resolve connection string
            string connection;
            try
            {
                connection = ConnectionResolver.Resolve(
                    connectionArg,
                    ConnectionResolver.ConnectionEnvVar,
                    "connection");
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
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
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            // Parse attribute lists (handle comma-separated)
            var includeAttrList = ParseAttributeList(includeAttributes);
            var excludeAttrList = ParseAttributeList(excludeAttributes);
            var excludePatternList = ParseAttributeList(excludePatterns);

            context.ExitCode = await ExecuteGenerateAsync(
                connection, entityList, output,
                includeSystemFields, includeRelationships, disablePlugins,
                includeAttrList, excludeAttrList, excludePatternList,
                json, verbose, context.GetCancellationToken());
        });

        return command;
    }

    private static Command CreateListCommand()
    {
        var connectionOption = new Option<string?>(
            aliases: ["--connection", "-c"],
            description: ConnectionResolver.GetHelpDescription(ConnectionResolver.ConnectionEnvVar));

        var filterOption = new Option<string?>(
            aliases: ["--filter", "-f"],
            description: "Filter entities by name pattern (e.g., 'account*' or '*custom*')");

        var customOnlyOption = new Option<bool>(
            name: "--custom-only",
            getDefaultValue: () => false,
            description: "Show only custom entities");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Output as JSON");

        var command = new Command("list", "List available entities in Dataverse")
        {
            connectionOption,
            filterOption,
            customOnlyOption,
            jsonOption
        };

        command.SetHandler(async (context) =>
        {
            var connectionArg = context.ParseResult.GetValueForOption(connectionOption);
            var filter = context.ParseResult.GetValueForOption(filterOption);
            var customOnly = context.ParseResult.GetValueForOption(customOnlyOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            // Resolve connection string
            string connection;
            try
            {
                connection = ConnectionResolver.Resolve(
                    connectionArg,
                    ConnectionResolver.ConnectionEnvVar,
                    "connection");
            }
            catch (InvalidOperationException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
                return;
            }

            context.ExitCode = await ExecuteListAsync(
                connection, filter, customOnly, json, context.GetCancellationToken());
        });

        return command;
    }

    private static List<string>? ParseAttributeList(string[]? input)
    {
        if (input == null || input.Length == 0)
        {
            return null;
        }

        return input
            .SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<int> ExecuteGenerateAsync(
        string connection,
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
        CancellationToken cancellationToken)
    {
        try
        {
            if (!json)
            {
                Console.WriteLine($"Generating schema for {entities.Count} entities...");
                if (includeAttributes != null)
                {
                    Console.WriteLine($"  Including only: {string.Join(", ", includeAttributes)}");
                }
                if (excludeAttributes != null)
                {
                    Console.WriteLine($"  Excluding: {string.Join(", ", excludeAttributes)}");
                }
                if (excludePatterns != null)
                {
                    Console.WriteLine($"  Excluding patterns: {string.Join(", ", excludePatterns)}");
                }
            }

            await using var serviceProvider = ServiceFactory.CreateProvider(connection);
            var generator = serviceProvider.GetRequiredService<ISchemaGenerator>();
            var schemaWriter = serviceProvider.GetRequiredService<ICmtSchemaWriter>();
            var progressReporter = ServiceFactory.CreateProgressReporter(json);

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

            if (!json)
            {
                Console.WriteLine();
                Console.WriteLine("Schema generated successfully.");
                Console.WriteLine($"Output: {output.FullName}");
                Console.WriteLine($"Entities: {schema.Entities.Count}");

                var totalFields = schema.Entities.Sum(e => e.Fields.Count);
                var totalRelationships = schema.Entities.Sum(e => e.Relationships.Count);
                Console.WriteLine($"Fields: {totalFields}, Relationships: {totalRelationships}");
            }

            return ExitCodes.Success;
        }
        catch (OperationCanceledException)
        {
            ConsoleOutput.WriteError("Schema generation cancelled by user.", json);
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            ConsoleOutput.WriteError($"Schema generation failed: {ex.Message}", json);
            if (verbose)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }

    private static async Task<int> ExecuteListAsync(
        string connection,
        string? filter,
        bool customOnly,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!json)
            {
                Console.WriteLine("Retrieving available entities...");
            }

            await using var serviceProvider = ServiceFactory.CreateProvider(connection);
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
