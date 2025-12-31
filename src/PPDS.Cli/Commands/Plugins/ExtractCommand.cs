using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Extract plugin registrations from assembly or NuGet package to JSON configuration.
/// </summary>
public static class ExtractCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Command Create()
    {
        var inputOption = new Option<FileInfo>("--input", "-i")
        {
            Description = "Path to assembly (.dll) or plugin package (.nupkg)",
            Required = true
        }.AcceptExistingOnly();

        var outputOption = new Option<FileInfo?>("--output", "-o")
        {
            Description = "Output file path (default: registrations.json in input directory)"
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Solution unique name to add components to"
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Overwrite existing file without merging"
        };

        var command = new Command("extract", "Extract plugin step/image attributes from assembly to JSON configuration")
        {
            inputOption,
            outputOption,
            solutionOption,
            forceOption,
            PluginsCommandGroup.JsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputOption)!;
            var output = parseResult.GetValue(outputOption);
            var solution = parseResult.GetValue(solutionOption);
            var force = parseResult.GetValue(forceOption);
            var json = parseResult.GetValue(PluginsCommandGroup.JsonOption);

            return await ExecuteAsync(input, output, solution, force, json, cancellationToken);
        });

        return command;
    }

    private static Task<int> ExecuteAsync(
        FileInfo input,
        FileInfo? output,
        string? solution,
        bool force,
        bool json,
        CancellationToken cancellationToken)
    {
        try
        {
            var extension = input.Extension.ToLowerInvariant();
            PluginAssemblyConfig assemblyConfig;

            if (extension == ".nupkg")
            {
                if (!json)
                    Console.WriteLine($"Extracting from NuGet package: {input.Name}");

                assemblyConfig = NupkgExtractor.Extract(input.FullName);
            }
            else if (extension == ".dll")
            {
                if (!json)
                    Console.WriteLine($"Extracting from assembly: {input.Name}");

                using var extractor = AssemblyExtractor.Create(input.FullName);
                assemblyConfig = extractor.Extract();
            }
            else
            {
                Console.Error.WriteLine($"Unsupported file type: {extension}. Expected .dll or .nupkg");
                return Task.FromResult(ExitCodes.Failure);
            }

            // Make path relative to output location
            var inputDir = input.DirectoryName ?? ".";
            var outputPath = output?.FullName ?? Path.Combine(inputDir, "registrations.json");
            var outputDir = Path.GetDirectoryName(outputPath) ?? ".";

            // Calculate relative path from output to input
            var relativePath = Path.GetRelativePath(outputDir, input.FullName);
            if (assemblyConfig.Type == "Assembly")
            {
                assemblyConfig.Path = relativePath;
            }
            else
            {
                assemblyConfig.PackagePath = relativePath;
                // For nupkg, path should point to the extracted DLL location (relative)
                assemblyConfig.Path = null;
            }

            // Apply solution from CLI if provided
            if (!string.IsNullOrEmpty(solution))
            {
                assemblyConfig.Solution = solution;
            }

            // Check for existing file and merge if not forced
            PluginRegistrationConfig config;
            var existingFile = new FileInfo(outputPath);

            if (existingFile.Exists && !force && !json)
            {
                Console.WriteLine($"Merging with existing configuration...");

                var existingContent = File.ReadAllText(outputPath);
                var existingConfig = JsonSerializer.Deserialize<PluginRegistrationConfig>(existingContent, JsonOptions);

                if (existingConfig != null)
                {
                    // Merge: preserve deployment settings from existing, update code-derived values from fresh
                    MergeAssemblyConfig(assemblyConfig, existingConfig);
                }

                config = new PluginRegistrationConfig
                {
                    Schema = "https://raw.githubusercontent.com/joshsmithxrm/ppds-sdk/main/schemas/plugin-registration.schema.json",
                    Version = existingConfig?.Version ?? "1.0",
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Assemblies = [assemblyConfig],
                    ExtensionData = existingConfig?.ExtensionData
                };
            }
            else
            {
                config = new PluginRegistrationConfig
                {
                    Schema = "https://raw.githubusercontent.com/joshsmithxrm/ppds-sdk/main/schemas/plugin-registration.schema.json",
                    Version = "1.0",
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Assemblies = [assemblyConfig]
                };
            }

            var jsonContent = JsonSerializer.Serialize(config, JsonOptions);

            if (json)
            {
                // Output to stdout for tool integration
                Console.WriteLine(jsonContent);
            }
            else
            {
                // Write to file
                File.WriteAllText(outputPath, jsonContent);
                Console.WriteLine();
                Console.WriteLine($"Found {assemblyConfig.Types.Count} plugin type(s) with {assemblyConfig.Types.Sum(t => t.Steps.Count)} step(s)");
                Console.WriteLine($"Output: {outputPath}");
            }

            return Task.FromResult(ExitCodes.Success);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error extracting plugin registrations: {ex.Message}");
            return Task.FromResult(ExitCodes.Failure);
        }
    }

    /// <summary>
    /// Merges deployment settings from existing config into fresh extracted config.
    /// Preserves: solution, runAsUser, description, deployment, asyncAutoDelete, and any unknown fields.
    /// </summary>
    private static void MergeAssemblyConfig(PluginAssemblyConfig fresh, PluginRegistrationConfig existing)
    {
        var existingAssembly = existing.Assemblies.FirstOrDefault(a =>
            a.Name.Equals(fresh.Name, StringComparison.OrdinalIgnoreCase));

        if (existingAssembly == null)
            return;

        // Preserve assembly-level deployment settings (only if not set via CLI)
        if (string.IsNullOrEmpty(fresh.Solution))
            fresh.Solution = existingAssembly.Solution;

        // Preserve unknown fields at assembly level
        fresh.ExtensionData = existingAssembly.ExtensionData;

        // Merge types
        foreach (var freshType in fresh.Types)
        {
            var existingType = existingAssembly.Types.FirstOrDefault(t =>
                t.TypeName.Equals(freshType.TypeName, StringComparison.OrdinalIgnoreCase));

            if (existingType == null)
                continue;

            // Preserve unknown fields at type level
            freshType.ExtensionData = existingType.ExtensionData;

            // Merge steps - match by message + entity + stage (functional identity)
            foreach (var freshStep in freshType.Steps)
            {
                var existingStep = existingType.Steps.FirstOrDefault(s =>
                    s.Message.Equals(freshStep.Message, StringComparison.OrdinalIgnoreCase) &&
                    s.Entity.Equals(freshStep.Entity, StringComparison.OrdinalIgnoreCase) &&
                    s.Stage.Equals(freshStep.Stage, StringComparison.OrdinalIgnoreCase));

                if (existingStep == null)
                    continue;

                // Preserve deployment settings from existing step
                freshStep.Deployment ??= existingStep.Deployment;
                freshStep.RunAsUser ??= existingStep.RunAsUser;
                freshStep.Description ??= existingStep.Description;
                freshStep.AsyncAutoDelete ??= existingStep.AsyncAutoDelete;

                // Preserve unknown fields at step level
                freshStep.ExtensionData = existingStep.ExtensionData;

                // Merge images - match by name
                foreach (var freshImage in freshStep.Images)
                {
                    var existingImage = existingStep.Images.FirstOrDefault(i =>
                        i.Name.Equals(freshImage.Name, StringComparison.OrdinalIgnoreCase));

                    if (existingImage == null)
                        continue;

                    // Preserve unknown fields at image level
                    freshImage.ExtensionData = existingImage.ExtensionData;
                }
            }
        }
    }
}
