using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Plugins.Registration;
using PPDS.Dataverse.Pooling;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// List registered plugins in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Create()
    {
        var assemblyOption = new Option<string?>("--assembly", "-a")
        {
            Description = "Filter by assembly name"
        };

        var command = new Command("list", "List registered plugins in the environment")
        {
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            assemblyOption,
            PluginsCommandGroup.JsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var assembly = parseResult.GetValue(assemblyOption);
            var json = parseResult.GetValue(PluginsCommandGroup.JsonOption);

            return await ExecuteAsync(profile, environment, assembly, json, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        string? assemblyFilter,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                verbose: false,
                debug: false,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var pool = serviceProvider.GetRequiredService<IDataverseConnectionPool>();
            await using var client = await pool.GetClientAsync(cancellationToken: cancellationToken);
            var registrationService = new PluginRegistrationService(client);

            if (!json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.WriteLine();
            }

            var assemblies = await registrationService.ListAssembliesAsync(assemblyFilter);

            if (assemblies.Count == 0)
            {
                if (json)
                {
                    Console.WriteLine("[]");
                }
                else
                {
                    Console.WriteLine("No plugin assemblies found.");
                }
                return ExitCodes.Success;
            }

            var output = new List<AssemblyOutput>();

            foreach (var assembly in assemblies)
            {
                var assemblyOutput = new AssemblyOutput
                {
                    Name = assembly.Name,
                    Version = assembly.Version,
                    PublicKeyToken = assembly.PublicKeyToken,
                    Types = []
                };

                var types = await registrationService.ListTypesForAssemblyAsync(assembly.Id);

                foreach (var type in types)
                {
                    var typeOutput = new TypeOutput
                    {
                        TypeName = type.TypeName,
                        Steps = []
                    };

                    var steps = await registrationService.ListStepsForTypeAsync(type.Id);

                    foreach (var step in steps)
                    {
                        var stepOutput = new StepOutput
                        {
                            Name = step.Name,
                            Message = step.Message,
                            Entity = step.PrimaryEntity,
                            Stage = step.Stage,
                            Mode = step.Mode,
                            ExecutionOrder = step.ExecutionOrder,
                            FilteringAttributes = step.FilteringAttributes,
                            IsEnabled = step.IsEnabled,
                            Images = []
                        };

                        var images = await registrationService.ListImagesForStepAsync(step.Id);

                        foreach (var image in images)
                        {
                            stepOutput.Images.Add(new ImageOutput
                            {
                                Name = image.Name,
                                ImageType = image.ImageType,
                                Attributes = image.Attributes
                            });
                        }

                        typeOutput.Steps.Add(stepOutput);
                    }

                    assemblyOutput.Types.Add(typeOutput);
                }

                output.Add(assemblyOutput);
            }

            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
            }
            else
            {
                foreach (var assembly in output)
                {
                    Console.WriteLine($"Assembly: {assembly.Name} (v{assembly.Version})");

                    foreach (var type in assembly.Types)
                    {
                        Console.WriteLine($"  Type: {type.TypeName}");

                        foreach (var step in type.Steps)
                        {
                            var status = step.IsEnabled ? "" : " [DISABLED]";
                            Console.WriteLine($"    Step: {step.Name}{status}");
                            Console.WriteLine($"      {step.Message} on {step.Entity} ({step.Stage}, {step.Mode})");

                            if (!string.IsNullOrEmpty(step.FilteringAttributes))
                            {
                                Console.WriteLine($"      Filtering: {step.FilteringAttributes}");
                            }

                            foreach (var image in step.Images)
                            {
                                var attrs = string.IsNullOrEmpty(image.Attributes) ? "all" : image.Attributes;
                                Console.WriteLine($"      Image: {image.Name} ({image.ImageType}) - {attrs}");
                            }
                        }
                    }

                    Console.WriteLine();
                }

                var totalSteps = output.Sum(a => a.Types.Sum(t => t.Steps.Count));
                Console.WriteLine($"Total: {output.Count} assembly(ies), {totalSteps} step(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error listing plugins: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    #region Output Models

    private sealed class AssemblyOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("publicKeyToken")]
        public string? PublicKeyToken { get; set; }

        [JsonPropertyName("types")]
        public List<TypeOutput> Types { get; set; } = [];
    }

    private sealed class TypeOutput
    {
        [JsonPropertyName("typeName")]
        public string TypeName { get; set; } = string.Empty;

        [JsonPropertyName("steps")]
        public List<StepOutput> Steps { get; set; } = [];
    }

    private sealed class StepOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;

        [JsonPropertyName("entity")]
        public string Entity { get; set; } = string.Empty;

        [JsonPropertyName("stage")]
        public string Stage { get; set; } = string.Empty;

        [JsonPropertyName("mode")]
        public string Mode { get; set; } = string.Empty;

        [JsonPropertyName("executionOrder")]
        public int ExecutionOrder { get; set; }

        [JsonPropertyName("filteringAttributes")]
        public string? FilteringAttributes { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("images")]
        public List<ImageOutput> Images { get; set; } = [];
    }

    private sealed class ImageOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("imageType")]
        public string ImageType { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        public string? Attributes { get; set; }
    }

    #endregion
}
