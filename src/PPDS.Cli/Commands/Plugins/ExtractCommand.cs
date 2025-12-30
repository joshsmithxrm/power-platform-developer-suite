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

        var command = new Command("extract", "Extract plugin step/image attributes from assembly to JSON configuration")
        {
            inputOption,
            outputOption,
            PluginsCommandGroup.JsonOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputOption)!;
            var output = parseResult.GetValue(outputOption);
            var json = parseResult.GetValue(PluginsCommandGroup.JsonOption);

            return await ExecuteAsync(input, output, json, cancellationToken);
        });

        return command;
    }

    private static Task<int> ExecuteAsync(
        FileInfo input,
        FileInfo? output,
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

            var config = new PluginRegistrationConfig
            {
                Schema = "https://raw.githubusercontent.com/joshsmithxrm/ppds-sdk/main/schemas/plugin-registration.schema.json",
                Version = "1.0",
                GeneratedAt = DateTimeOffset.UtcNow,
                Assemblies = [assemblyConfig]
            };

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
}
