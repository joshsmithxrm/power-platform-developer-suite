using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Migration.Formats;
using PPDS.Migration.Progress;
using PPDS.Migration.Schema;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Schema generation command for data migration.
/// </summary>
public static class SchemaCommand
{
    public static Command Create()
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
        };
        outputOption.Validators.Add(result =>
        {
            var file = result.GetValue(outputOption);
            if (file?.Directory is { Exists: false })
                result.AddError($"Output directory does not exist: {file.Directory.FullName}");
        });

        var includeAuditFieldsOption = new Option<bool>("--include-audit-fields")
        {
            Description = "Include audit fields (createdon, createdby, modifiedon, modifiedby, overriddencreatedon)",
            DefaultValueFactory = _ => false
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

        var outputFormatOption = new Option<OutputFormat>("--output-format", "-f")
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

        var command = new Command("schema", "Generate a migration schema from Dataverse metadata")
        {
            entitiesOption,
            outputOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            includeAuditFieldsOption,
            disablePluginsOption,
            includeAttributesOption,
            excludeAttributesOption,
            outputFormatOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var entities = parseResult.GetValue(entitiesOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var includeAuditFields = parseResult.GetValue(includeAuditFieldsOption);
            var disablePlugins = parseResult.GetValue(disablePluginsOption);
            var includeAttributes = parseResult.GetValue(includeAttributesOption);
            var excludeAttributes = parseResult.GetValue(excludeAttributesOption);
            var outputFormat = parseResult.GetValue(outputFormatOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            var entityList = entities
                .SelectMany(e => e.Split(',', StringSplitOptions.RemoveEmptyEntries))
                .Select(e => e.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entityList.Count == 0)
            {
                var writer = ServiceFactory.CreateOutputWriter(outputFormat, debug);
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.RequiredField,
                    "No entities specified.",
                    Target: "--entities"));
                return ExitCodes.InvalidArguments;
            }

            var includeAttrList = ParseAttributeList(includeAttributes);
            var excludeAttrList = ParseAttributeList(excludeAttributes);

            return await ExecuteAsync(
                profile, environment, entityList, output,
                includeAuditFields, disablePlugins,
                includeAttrList, excludeAttrList,
                outputFormat, verbose, debug, cancellationToken);
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

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        List<string> entities,
        FileInfo output,
        bool includeAuditFields,
        bool disablePlugins,
        List<string>? includeAttributes,
        List<string>? excludeAttributes,
        OutputFormat outputFormat,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        var progressReporter = ServiceFactory.CreateProgressReporter(outputFormat, "Schema generation");

        try
        {
            var optionsMsg = new List<string>();
            if (includeAttributes is { Count: > 0 }) optionsMsg.Add($"include: {string.Join(",", includeAttributes)}");
            if (excludeAttributes is { Count: > 0 }) optionsMsg.Add($"exclude: {string.Join(",", excludeAttributes)}");

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            if (outputFormat != OutputFormat.Json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Generating schema for {entities.Count} entities..." +
                          (optionsMsg.Count > 0 ? $" ({string.Join(", ", optionsMsg)})" : "")
            });

            var generator = serviceProvider.GetRequiredService<ISchemaGenerator>();
            var schemaWriter = serviceProvider.GetRequiredService<ICmtSchemaWriter>();

            var options = new SchemaGeneratorOptions
            {
                IncludeAuditFields = includeAuditFields,
                DisablePluginsByDefault = disablePlugins,
                IncludeAttributes = includeAttributes,
                ExcludeAttributes = excludeAttributes
            };

            var schema = await generator.GenerateAsync(
                entities, options, progressReporter, cancellationToken);

            // Fail if no entities were successfully processed
            if (schema.Entities.Count == 0)
            {
                var failedEntities = string.Join(", ", entities);
                progressReporter.Error(
                    new InvalidOperationException($"No valid entities found: {failedEntities}"),
                    $"None of the specified entities could be found or accessed: {failedEntities}");
                return ExitCodes.Failure;
            }

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
}
