using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
using PPDS.Cli.Commands.Data;
using PPDS.Cli.Infrastructure;
using PPDS.Dataverse.Pooling;
using PPDS.Migration.Formats;
using PPDS.Migration.Progress;
using PPDS.Migration.Schema;

namespace PPDS.Cli.Commands;

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

        var jsonOption = new Option<bool>("--json", "-j")
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
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            includeAuditFieldsOption,
            disablePluginsOption,
            includeAttributesOption,
            excludeAttributesOption,
            jsonOption,
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
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

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

            var includeAttrList = ParseAttributeList(includeAttributes);
            var excludeAttrList = ParseAttributeList(excludeAttributes);

            return await ExecuteGenerateAsync(
                profile, environment, entityList, output,
                includeAuditFields, disablePlugins,
                includeAttrList, excludeAttrList,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static Command CreateListCommand()
    {
        var entityOption = new Option<string?>("--entity")
        {
            Description = "Show detailed field metadata for a specific entity"
        };

        var filterOption = new Option<string?>("--filter", "-f")
        {
            Description = "Filter entities by name pattern (e.g., 'account*' or '*custom*')"
        };

        var customOnlyOption = new Option<bool>("--custom-only")
        {
            Description = "Show only custom entities",
            DefaultValueFactory = _ => false
        };

        var includeAuditFieldsOption = new Option<bool>("--include-audit-fields")
        {
            Description = "Show audit fields as 'Include' in filter results (matches schema generate behavior)",
            DefaultValueFactory = _ => false
        };

        var jsonOption = new Option<bool>("--json", "-j")
        {
            Description = "Output as JSON",
            DefaultValueFactory = _ => false
        };

        var command = new Command("list", "List available entities in Dataverse")
        {
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            entityOption,
            filterOption,
            customOnlyOption,
            includeAuditFieldsOption,
            jsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var entity = parseResult.GetValue(entityOption);
            var filter = parseResult.GetValue(filterOption);
            var customOnly = parseResult.GetValue(customOnlyOption);
            var includeAuditFields = parseResult.GetValue(includeAuditFieldsOption);
            var json = parseResult.GetValue(jsonOption);

            if (!string.IsNullOrEmpty(entity))
            {
                return await ExecuteEntityDetailAsync(
                    profile, environment, entity, includeAuditFields, json, cancellationToken);
            }

            return await ExecuteListAsync(profile, environment, filter, customOnly, json, cancellationToken);
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
        string? profile,
        string? environment,
        List<string> entities,
        FileInfo output,
        bool includeAuditFields,
        bool disablePlugins,
        List<string>? includeAttributes,
        List<string>? excludeAttributes,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        var progressReporter = ServiceFactory.CreateProgressReporter(json, "Schema generation");

        try
        {
            var optionsMsg = new List<string>();
            if (includeAttributes != null) optionsMsg.Add($"include: {string.Join(",", includeAttributes)}");
            if (excludeAttributes != null) optionsMsg.Add($"exclude: {string.Join(",", excludeAttributes)}");

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            if (!json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.WriteLine();
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
        string? profile,
        string? environment,
        string? filter,
        bool customOnly,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            if (!json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.WriteLine();
                Console.WriteLine("Retrieving available entities...");
            }

            var generator = serviceProvider.GetRequiredService<ISchemaGenerator>();

            var entities = await generator.GetAvailableEntitiesAsync(cancellationToken);

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

    private static async Task<int> ExecuteEntityDetailAsync(
        string? profile,
        string? environment,
        string entityName,
        bool includeAuditFields,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                deviceCodeCallback: ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            if (!json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.WriteLine();
                Console.WriteLine($"Retrieving metadata for entity '{entityName}'...");
            }

            var connectionPool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await connectionPool.GetClientAsync(cancellationToken: cancellationToken);

            var request = new RetrieveEntityRequest
            {
                LogicalName = entityName,
                EntityFilters = EntityFilters.Attributes,
                RetrieveAsIfPublished = false
            };

            var response = (RetrieveEntityResponse)await client.ExecuteAsync(request, cancellationToken);
            var metadata = response.EntityMetadata;

            var primaryIdField = metadata.PrimaryIdAttribute ?? $"{metadata.LogicalName}id";
            var fields = new List<FieldDetailInfo>();
            var skippedCount = 0;

            if (metadata.Attributes != null)
            {
                foreach (var attr in metadata.Attributes.OrderBy(a => a.LogicalName))
                {
                    // Skip if not valid for read
                    if (attr.IsValidForRead != true)
                    {
                        skippedCount++;
                        continue;
                    }

                    var isValidForCreate = attr.IsValidForCreate ?? false;
                    var isValidForUpdate = attr.IsValidForUpdate ?? false;

                    // Skip fields that are never writable (matches schema generation)
                    if (!isValidForCreate && !isValidForUpdate)
                    {
                        skippedCount++;
                        continue;
                    }

                    var isPrimaryKey = attr.LogicalName == primaryIdField;
                    var (filterResult, filterReason) = GetFilterResult(attr, isPrimaryKey, includeAuditFields);

                    fields.Add(new FieldDetailInfo
                    {
                        LogicalName = attr.LogicalName,
                        DisplayName = attr.DisplayName?.UserLocalizedLabel?.Label ?? attr.LogicalName,
                        Type = GetFieldTypeName(attr),
                        IsCustomAttribute = attr.IsCustomAttribute ?? false,
                        IsCustomizable = attr.IsCustomizable?.Value ?? false,
                        IsValidForCreate = isValidForCreate,
                        IsValidForUpdate = isValidForUpdate,
                        FilterResult = filterResult,
                        FilterReason = filterReason
                    });
                }
            }

            var includeCount = fields.Count(f => f.FilterResult == FieldFilterResult.Include);
            var auditCount = fields.Count(f => f.FilterResult == FieldFilterResult.Audit);
            var excludeCount = fields.Count(f => f.FilterResult == FieldFilterResult.Exclude);

            if (json)
            {
                var output = new
                {
                    logicalName = metadata.LogicalName,
                    displayName = metadata.DisplayName?.UserLocalizedLabel?.Label ?? metadata.LogicalName,
                    isCustomEntity = metadata.IsCustomEntity ?? false,
                    primaryIdField,
                    fields = fields.Select(f => new
                    {
                        logicalName = f.LogicalName,
                        displayName = f.DisplayName,
                        type = f.Type,
                        isCustomAttribute = f.IsCustomAttribute,
                        isCustomizable = f.IsCustomizable,
                        isValidForCreate = f.IsValidForCreate,
                        isValidForUpdate = f.IsValidForUpdate,
                        filterResult = f.FilterResult.ToString(),
                        filterReason = f.FilterReason
                    }),
                    summary = new
                    {
                        total = fields.Count,
                        include = includeCount,
                        audit = auditCount,
                        exclude = excludeCount,
                        skipped = skippedCount
                    }
                };

                var jsonOutput = System.Text.Json.JsonSerializer.Serialize(output, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                });
                Console.WriteLine(jsonOutput);
            }
            else
            {
                var isCustom = (metadata.IsCustomEntity ?? false) ? "Yes" : "No";
                var displayName = metadata.DisplayName?.UserLocalizedLabel?.Label ?? metadata.LogicalName;

                Console.WriteLine();
                Console.WriteLine($"Entity: {metadata.LogicalName} ({displayName}, Custom: {isCustom})");
                Console.WriteLine();

                // Table header
                Console.WriteLine($"{"Field",-35} {"Type",-15} {"Custom",-8} {"Cust'ble",-10} {"Create",-8} {"Update",-8} {"Filter Result"}");
                Console.WriteLine(new string('-', 110));

                foreach (var field in fields)
                {
                    var custom = field.IsCustomAttribute ? "Yes" : "";
                    var customizable = field.IsCustomizable ? "Yes" : "";
                    var create = field.IsValidForCreate ? "Yes" : "";
                    var update = field.IsValidForUpdate ? "Yes" : "";

                    var filterDisplay = field.FilterResult.ToString();
                    if (!string.IsNullOrEmpty(field.FilterReason))
                    {
                        filterDisplay += $" ({field.FilterReason})";
                    }

                    Console.WriteLine($"{field.LogicalName,-35} {field.Type,-15} {custom,-8} {customizable,-10} {create,-8} {update,-8} {filterDisplay}");
                }

                Console.WriteLine();
                Console.WriteLine($"Total: {fields.Count} fields ({includeCount} Include, {auditCount} Audit, {excludeCount} Exclude)");
                if (skippedCount > 0)
                {
                    Console.WriteLine($"Skipped: {skippedCount} fields (read-only or not valid for read)");
                }
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
            ConsoleOutput.WriteError($"Failed to get entity details: {ex.Message}", json);
            return ExitCodes.Failure;
        }
    }

    /// <summary>
    /// Determines the filter result for a field based on metadata-driven filtering.
    /// Logic mirrors DataverseSchemaGenerator.ShouldIncludeField for consistency.
    /// </summary>
    private static (FieldFilterResult Result, string? Reason) GetFilterResult(
        AttributeMetadata attr,
        bool isPrimaryKey,
        bool includeAuditFields)
    {
        if (isPrimaryKey)
            return (FieldFilterResult.Include, "PK");

        if (attr.IsCustomAttribute == true)
            return (FieldFilterResult.Include, "Custom");

        // Virtual attributes: only include Image and MultiSelectPicklist
        if (attr.AttributeType == AttributeTypeCode.Virtual)
        {
            if (attr is ImageAttributeMetadata)
                return (FieldFilterResult.Include, "Image");
            if (attr is MultiSelectPicklistAttributeMetadata)
                return (FieldFilterResult.Include, "MSP");
            return (FieldFilterResult.Exclude, "Virtual");
        }

        // System bookkeeping fields (customizable but not migration-relevant)
        if (IsNonMigratableSystemField(attr.LogicalName))
            return (FieldFilterResult.Exclude, "System");

        // Customizable system fields
        if (attr.IsCustomizable?.Value == true)
            return (FieldFilterResult.Include, null);

        // Audit fields
        if (IsAuditField(attr.LogicalName))
        {
            if (includeAuditFields)
                return (FieldFilterResult.Include, "Audit");
            return (FieldFilterResult.Audit, null);
        }

        // BPF and image reference fields
        if (IsBpfOrImageField(attr.LogicalName))
            return (FieldFilterResult.Include, "BPF");

        return (FieldFilterResult.Exclude, null);
    }

    /// <summary>
    /// System bookkeeping fields that are marked IsCustomizable=true but serve no purpose in data migration.
    /// </summary>
    private static bool IsNonMigratableSystemField(string fieldName)
    {
        return fieldName is
            "timezoneruleversionnumber" or
            "utcconversiontimezonecode" or
            "importsequencenumber";
    }

    /// <summary>
    /// Audit fields track who created/modified records and when.
    /// </summary>
    private static bool IsAuditField(string fieldName)
    {
        return fieldName is
            "createdon" or
            "createdby" or
            "createdonbehalfby" or
            "modifiedon" or
            "modifiedby" or
            "modifiedonbehalfby" or
            "overriddencreatedon";
    }

    /// <summary>
    /// BPF (Business Process Flow) and image reference fields.
    /// </summary>
    private static bool IsBpfOrImageField(string fieldName)
    {
        return fieldName is "processid" or "stageid" or "entityimageid";
    }

    /// <summary>
    /// Gets a human-readable type name for an attribute.
    /// </summary>
    private static string GetFieldTypeName(AttributeMetadata attr)
    {
        return attr.AttributeType switch
        {
            AttributeTypeCode.BigInt => "bigint",
            AttributeTypeCode.Boolean => "boolean",
            AttributeTypeCode.CalendarRules => "calendarrules",
            AttributeTypeCode.Customer => "customer",
            AttributeTypeCode.DateTime => "datetime",
            AttributeTypeCode.Decimal => "decimal",
            AttributeTypeCode.Double => "float",
            AttributeTypeCode.Integer => "integer",
            AttributeTypeCode.Lookup => "lookup",
            AttributeTypeCode.Memo => "memo",
            AttributeTypeCode.Money => "money",
            AttributeTypeCode.Owner => "owner",
            AttributeTypeCode.PartyList => "partylist",
            AttributeTypeCode.Picklist => "picklist",
            AttributeTypeCode.State => "state",
            AttributeTypeCode.Status => "status",
            AttributeTypeCode.String => "string",
            AttributeTypeCode.Uniqueidentifier => "guid",
            AttributeTypeCode.Virtual => attr switch
            {
                ImageAttributeMetadata => "image",
                MultiSelectPicklistAttributeMetadata => "multiselect",
                _ => "virtual"
            },
            AttributeTypeCode.ManagedProperty => "managedproperty",
            AttributeTypeCode.EntityName => "entityname",
            _ => "unknown"
        };
    }

    #region Internal Types for Entity Detail View

    private enum FieldFilterResult
    {
        Include,
        Audit,
        Exclude
    }

    private sealed class FieldDetailInfo
    {
        public string LogicalName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public bool IsCustomAttribute { get; init; }
        public bool IsCustomizable { get; init; }
        public bool IsValidForCreate { get; init; }
        public bool IsValidForUpdate { get; init; }
        public FieldFilterResult FilterResult { get; init; }
        public string? FilterReason { get; init; }
    }

    #endregion
}
