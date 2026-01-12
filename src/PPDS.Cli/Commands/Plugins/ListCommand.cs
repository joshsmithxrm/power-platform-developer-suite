using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;

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
            Description = "Filter by assembly name (classic DLL plugins)"
        };

        var packageOption = new Option<string?>("--package", "-pkg")
        {
            Description = "Filter by package name or unique name (NuGet plugin packages)"
        };

        var includeHiddenOption = new Option<bool>("--include-hidden")
        {
            Description = "Include hidden steps (excluded by default)"
        };

        var includeMicrosoftOption = new Option<bool>("--include-microsoft")
        {
            Description = "Include Microsoft.* assemblies (excluded by default, except Microsoft.Crm.ServiceBus)"
        };

        var allOption = new Option<bool>("--all")
        {
            Description = "Show all plugins (equivalent to --include-hidden --include-microsoft)"
        };

        var command = new Command("list", "List registered plugins in the environment")
        {
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption,
            assemblyOption,
            packageOption,
            includeHiddenOption,
            includeMicrosoftOption,
            allOption
        };

        // Add global options including output format
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var assembly = parseResult.GetValue(assemblyOption);
            var package = parseResult.GetValue(packageOption);
            var includeHidden = parseResult.GetValue(includeHiddenOption);
            var includeMicrosoft = parseResult.GetValue(includeMicrosoftOption);
            var all = parseResult.GetValue(allOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            // --all is equivalent to --include-hidden --include-microsoft
            var listOptions = new PluginListOptions(
                IncludeHidden: all || includeHidden,
                IncludeMicrosoft: all || includeMicrosoft
            );

            return await ExecuteAsync(profile, environment, assembly, package, listOptions, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        string? assemblyFilter,
        string? packageFilter,
        PluginListOptions listOptions,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var output = new ListOutput();

            // Get assemblies (unless package filter is specified, which means we only want packages)
            if (string.IsNullOrEmpty(packageFilter))
            {
                var assemblies = await registrationService.ListAssembliesAsync(assemblyFilter, listOptions);

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
                    await PopulateTypesAsync(registrationService, types, assemblyOutput.Types, listOptions);

                    output.Assemblies.Add(assemblyOutput);
                }
            }

            // Get packages (unless assembly filter is specified, which means we only want assemblies)
            if (string.IsNullOrEmpty(assemblyFilter))
            {
                var packages = await registrationService.ListPackagesAsync(packageFilter, listOptions);

                foreach (var package in packages)
                {
                    var packageOutput = new PackageOutput
                    {
                        Name = package.Name,
                        UniqueName = package.UniqueName,
                        Version = package.Version,
                        Assemblies = []
                    };

                    var assemblies = await registrationService.ListAssembliesForPackageAsync(package.Id);
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
                        await PopulateTypesAsync(registrationService, types, assemblyOutput.Types, listOptions);

                        packageOutput.Assemblies.Add(assemblyOutput);
                    }

                    output.Packages.Add(packageOutput);
                }
            }

            var totalAssemblies = output.Assemblies.Count;
            var totalPackages = output.Packages.Count;

            if (totalAssemblies == 0 && totalPackages == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(output);
                }
                else
                {
                    Console.Error.WriteLine("No plugin assemblies or packages found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(output);
            }
            else
            {
                // Print assemblies
                foreach (var assembly in output.Assemblies)
                {
                    Console.Error.WriteLine($"Assembly: {assembly.Name} (v{assembly.Version})");
                    PrintTypes(assembly.Types);
                    Console.Error.WriteLine();
                }

                // Print packages
                foreach (var package in output.Packages)
                {
                    var uniqueName = package.UniqueName != package.Name ? $" [{package.UniqueName}]" : "";
                    Console.Error.WriteLine($"Package: {package.Name}{uniqueName} (v{package.Version})");
                    PrintPackageAssemblies(package.Assemblies);
                    Console.Error.WriteLine();
                }

                var totalPackageAssemblies = output.Packages.Sum(p => p.Assemblies.Count);
                var totalTypes = output.Assemblies.Sum(a => a.Types.Count) +
                                 output.Packages.Sum(p => p.Assemblies.Sum(a => a.Types.Count));
                var totalSteps = output.Assemblies.Sum(a => a.Types.Sum(t => t.Steps.Count)) +
                                 output.Packages.Sum(p => p.Assemblies.Sum(a => a.Types.Sum(t => t.Steps.Count)));
                var totalImages = output.Assemblies.Sum(a => a.Types.Sum(t => t.Steps.Sum(s => s.Images.Count))) +
                                  output.Packages.Sum(p => p.Assemblies.Sum(a => a.Types.Sum(t => t.Steps.Sum(s => s.Images.Count))));

                // Build summary parts based on what's present
                var summaryParts = new List<string>();
                if (totalAssemblies > 0)
                {
                    summaryParts.Add(Pluralize(totalAssemblies, "assembly", "assemblies"));
                }
                if (totalPackages > 0)
                {
                    summaryParts.Add($"{Pluralize(totalPackages, "package", "packages")} ({Pluralize(totalPackageAssemblies, "assembly", "assemblies")})");
                }
                summaryParts.Add(Pluralize(totalTypes, "type", "types"));
                summaryParts.Add(Pluralize(totalSteps, "step", "steps"));
                if (totalImages > 0)
                {
                    summaryParts.Add(Pluralize(totalImages, "image", "images"));
                }

                Console.Error.WriteLine($"Total: {string.Join(", ", summaryParts)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing plugins", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task PopulateTypesAsync(
        IPluginRegistrationService registrationService,
        List<PluginTypeInfo> types,
        List<TypeOutput> typeOutputs,
        PluginListOptions listOptions)
    {
        foreach (var type in types)
        {
            var typeOutput = new TypeOutput
            {
                TypeName = type.TypeName,
                Steps = []
            };

            var steps = await registrationService.ListStepsForTypeAsync(type.Id, listOptions);

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
                    Description = step.Description,
                    Deployment = step.Deployment,
                    RunAsUser = step.ImpersonatingUserName,
                    AsyncAutoDelete = step.AsyncAutoDelete,
                    UnsecureConfiguration = step.Configuration,
                    Images = []
                };

                var images = await registrationService.ListImagesForStepAsync(step.Id);

                foreach (var image in images)
                {
                    stepOutput.Images.Add(new ImageOutput
                    {
                        Name = image.Name,
                        EntityAlias = image.EntityAlias ?? image.Name,
                        ImageType = image.ImageType,
                        Attributes = image.Attributes
                    });
                }

                typeOutput.Steps.Add(stepOutput);
            }

            typeOutputs.Add(typeOutput);
        }
    }

    private static void PrintTypes(List<TypeOutput> types, string indent = "  ")
    {
        foreach (var type in types)
        {
            Console.Error.WriteLine($"{indent}Type: {type.TypeName}");

            foreach (var step in type.Steps)
            {
                var status = step.IsEnabled ? "" : " [DISABLED]";
                Console.Error.WriteLine($"{indent}  Step: {step.Name}{status}");
                Console.Error.WriteLine($"{indent}    {step.Message} on {step.Entity} ({step.Stage}, {step.Mode})");

                // Show non-default deployment/user/async settings on one line if any are set
                var stepOptions = new List<string>();
                if (step.Deployment != "ServerOnly")
                {
                    stepOptions.Add($"Deployment: {step.Deployment}");
                }
                if (!string.IsNullOrEmpty(step.RunAsUser))
                {
                    stepOptions.Add($"Run as: {step.RunAsUser}");
                }
                if (step.AsyncAutoDelete && step.Mode == "Asynchronous")
                {
                    stepOptions.Add("Auto-delete: Yes");
                }
                if (stepOptions.Count > 0)
                {
                    Console.Error.WriteLine($"{indent}    {string.Join(" | ", stepOptions)}");
                }

                if (!string.IsNullOrEmpty(step.FilteringAttributes))
                {
                    Console.Error.WriteLine($"{indent}    Filtering: {step.FilteringAttributes}");
                }

                if (!string.IsNullOrEmpty(step.Description))
                {
                    Console.Error.WriteLine($"{indent}    Description: {step.Description}");
                }

                foreach (var image in step.Images)
                {
                    var attrs = string.IsNullOrEmpty(image.Attributes) ? "all" : image.Attributes;
                    Console.Error.WriteLine($"{indent}    Image: {image.Name} ({image.ImageType}) - {attrs}");
                }
            }
        }
    }

    private static void PrintPackageAssemblies(List<AssemblyOutput> assemblies)
    {
        foreach (var assembly in assemblies)
        {
            Console.Error.WriteLine($"  Assembly: {assembly.Name} (v{assembly.Version})");
            PrintTypes(assembly.Types, "    ");
        }
    }

    private static string Pluralize(int count, string singular, string plural) =>
        count == 1 ? $"{count} {singular}" : $"{count} {plural}";

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("assemblies")]
        public List<AssemblyOutput> Assemblies { get; set; } = [];

        [JsonPropertyName("packages")]
        public List<PackageOutput> Packages { get; set; } = [];
    }

    private sealed class PackageOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("uniqueName")]
        public string? UniqueName { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("assemblies")]
        public List<AssemblyOutput> Assemblies { get; set; } = [];
    }

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

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("deployment")]
        public string Deployment { get; set; } = "ServerOnly";

        [JsonPropertyName("runAsUser")]
        public string? RunAsUser { get; set; }

        [JsonPropertyName("asyncAutoDelete")]
        public bool AsyncAutoDelete { get; set; }

        [JsonPropertyName("unsecureConfiguration")]
        public string? UnsecureConfiguration { get; set; }

        [JsonPropertyName("images")]
        public List<ImageOutput> Images { get; set; } = [];
    }

    private sealed class ImageOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("entityAlias")]
        public string EntityAlias { get; set; } = string.Empty;

        [JsonPropertyName("imageType")]
        public string ImageType { get; set; } = string.Empty;

        [JsonPropertyName("attributes")]
        public string? Attributes { get; set; }
    }

    #endregion
}
