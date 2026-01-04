using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Remove orphaned plugin registrations not in configuration.
/// </summary>
public static class CleanCommand
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create()
    {
        var configOption = new Option<FileInfo>("--config", "-c")
        {
            Description = "Path to registrations.json",
            Required = true
        }.AcceptExistingOnly();

        var whatIfOption = new Option<bool>("--what-if")
        {
            Description = "Preview deletions without applying",
            DefaultValueFactory = _ => false
        };

        var command = new Command("clean", "Remove orphaned registrations not in configuration")
        {
            configOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            whatIfOption,
            PluginsCommandGroup.OutputFormatOption
        };

        // Add global options for verbosity and correlation
        GlobalOptions.AddToCommand(command, includeOutputFormat: false);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var config = parseResult.GetValue(configOption)!;
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var whatIf = parseResult.GetValue(whatIfOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(config, profile, environment, whatIf, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        FileInfo configFile,
        string? profile,
        string? environment,
        bool whatIf,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Load configuration
            var configJson = await File.ReadAllTextAsync(configFile.FullName, cancellationToken);
            var config = JsonSerializer.Deserialize<PluginRegistrationConfig>(configJson, JsonReadOptions);

            if (config?.Assemblies == null || config.Assemblies.Count == 0)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    "No assemblies found in configuration file.",
                    Target: configFile.Name));
                return ExitCodes.InvalidArguments;
            }

            // Validate configuration
            config.Validate();

            // Connect to Dataverse
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            var logger = serviceProvider.GetRequiredService<ILogger<PluginRegistrationService>>();
            var registrationService = new PluginRegistrationService(pool, logger);

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();

                if (whatIf)
                {
                    Console.Error.WriteLine("[What-If Mode] No changes will be applied.");
                    Console.Error.WriteLine();
                }
            }

            var results = new List<CleanResult>();

            foreach (var assemblyConfig in config.Assemblies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var result = await CleanAssemblyAsync(
                    registrationService,
                    assemblyConfig,
                    whatIf,
                    globalOptions,
                    cancellationToken);

                results.Add(result);
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(results);
            }
            else
            {
                Console.Error.WriteLine();
                var totalOrphans = results.Sum(r => r.OrphanedSteps.Count);
                var totalDeleted = results.Sum(r => r.StepsDeleted);
                var totalTypesDeleted = results.Sum(r => r.TypesDeleted);

                if (totalOrphans == 0)
                {
                    Console.Error.WriteLine("No orphaned registrations found.");
                }
                else if (whatIf)
                {
                    Console.Error.WriteLine($"Would delete: {totalOrphans} step(s), {totalTypesDeleted} orphaned type(s)");
                }
                else
                {
                    Console.Error.WriteLine($"Deleted: {totalDeleted} step(s), {totalTypesDeleted} orphaned type(s)");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "cleaning plugins", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<CleanResult> CleanAssemblyAsync(
        PluginRegistrationService service,
        PluginAssemblyConfig assemblyConfig,
        bool whatIf,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var result = new CleanResult
        {
            AssemblyName = assemblyConfig.Name
        };

        // Check if assembly exists
        var assembly = await service.GetAssemblyByNameAsync(assemblyConfig.Name);
        if (assembly == null)
        {
            if (!globalOptions.IsJsonMode)
                Console.Error.WriteLine($"Assembly not found: {assemblyConfig.Name}");
            return result;
        }

        if (!globalOptions.IsJsonMode)
            Console.Error.WriteLine($"Checking assembly: {assemblyConfig.Name}");

        // Build set of configured step names
        var configuredStepNames = new HashSet<string>();
        foreach (var typeConfig in assemblyConfig.Types)
        {
            foreach (var stepConfig in typeConfig.Steps)
            {
                var stepName = stepConfig.Name ?? $"{typeConfig.TypeName}: {stepConfig.Message} of {stepConfig.Entity}";
                configuredStepNames.Add(stepName);
            }
        }

        // Get existing types and steps
        var existingTypes = await service.ListTypesForAssemblyAsync(assembly.Id);

        foreach (var existingType in existingTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var steps = await service.ListStepsForTypeAsync(existingType.Id);

            // Find orphaned steps and track count for type cleanup
            var orphanedStepsInType = steps.Where(s => !configuredStepNames.Contains(s.Name)).ToList();

            foreach (var step in orphanedStepsInType)
            {
                result.OrphanedSteps.Add(new OrphanedStep
                {
                    TypeName = existingType.TypeName,
                    StepName = step.Name,
                    StepId = step.Id
                });

                if (whatIf)
                {
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  [What-If] Would delete step: {step.Name}");
                }
                else
                {
                    await service.DeleteStepAsync(step.Id);
                    result.StepsDeleted++;
                    if (!globalOptions.IsJsonMode)
                        Console.Error.WriteLine($"  Deleted step: {step.Name}");
                }
            }

            // Check if type is orphaned (no configured steps and not in allTypeNames)
            var typeHasConfiguredSteps = assemblyConfig.Types
                .Any(t => t.TypeName == existingType.TypeName && t.Steps.Count > 0);

            var typeInAllTypeNames = assemblyConfig.AllTypeNames.Contains(existingType.TypeName);

            if (!typeHasConfiguredSteps && !typeInAllTypeNames)
            {
                // Calculate remaining steps in memory instead of re-querying
                var remainingStepsCount = steps.Count - orphanedStepsInType.Count;

                if (remainingStepsCount == 0)
                {
                    result.OrphanedTypes.Add(new OrphanedType
                    {
                        TypeName = existingType.TypeName,
                        TypeId = existingType.Id
                    });

                    if (whatIf)
                    {
                        if (!globalOptions.IsJsonMode)
                            Console.Error.WriteLine($"  [What-If] Would delete orphaned type: {existingType.TypeName}");
                    }
                    else
                    {
                        try
                        {
                            await service.DeletePluginTypeAsync(existingType.Id);
                            result.TypesDeleted++;
                            if (!globalOptions.IsJsonMode)
                                Console.Error.WriteLine($"  Deleted orphaned type: {existingType.TypeName}");
                        }
                        catch (Exception ex)
                        {
                            if (!globalOptions.IsJsonMode)
                                Console.Error.WriteLine($"  Warning: Could not delete type {existingType.TypeName}: {ex.Message}");
                        }
                    }
                }
            }
        }

        return result;
    }

    #region Result Models

    private sealed class CleanResult
    {
        [JsonPropertyName("assemblyName")]
        public string AssemblyName { get; set; } = string.Empty;

        [JsonPropertyName("orphanedSteps")]
        public List<OrphanedStep> OrphanedSteps { get; set; } = [];

        [JsonPropertyName("orphanedTypes")]
        public List<OrphanedType> OrphanedTypes { get; set; } = [];

        [JsonPropertyName("stepsDeleted")]
        public int StepsDeleted { get; set; }

        [JsonPropertyName("typesDeleted")]
        public int TypesDeleted { get; set; }
    }

    private sealed class OrphanedStep
    {
        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("stepName")]
        public string StepName { get; set; } = string.Empty;

        [JsonPropertyName("stepId")]
        public Guid StepId { get; set; }
    }

    private sealed class OrphanedType
    {
        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("typeId")]
        public Guid TypeId { get; set; }
    }

    #endregion
}
