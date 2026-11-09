using System.CommandLine;
using Microsoft.Extensions.Logging;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Extraction;
using PPDS.Cli.Plugins.Models;
using PPDS.Cli.Services.Plugins;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Extract plugin registrations from assembly or NuGet package to JSON configuration.
/// </summary>
public static class ExtractCommand
{
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

        var registrationVersionOption = new Option<string?>("--registration-version")
        {
            Description = "Registration contract: 1.0 (legacy) or 2.0 (nonsecret reconciliation); defaults to the existing file version or 1.0"
        };

        var referenceDirOption = new Option<DirectoryInfo[]>("--reference-dir")
        {
            Description = "Additional directory to search for referenced assemblies (repeatable). " +
                          "Use when a plugin's dependencies live outside the input assembly's own folder.",
            AllowMultipleArgumentsPerToken = true
        }.AcceptExistingOnly();

        var command = new Command(
            "extract",
            "Extract plugin step/image attributes from an assembly (.dll) or plugin package (.nupkg) to " +
            "JSON configuration. Works against Dataverse-supported .NET Framework plugin assemblies and " +
            "net462/net471/net48 plugin package assets regardless of how the CLI is packaged (single-file, " +
            "self-contained, or framework-dependent).")
        {
            inputOption,
            outputOption,
            solutionOption,
            forceOption,
            referenceDirOption,
            registrationVersionOption
        };

        // Add global options including output format
        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputOption)!;
            var output = parseResult.GetValue(outputOption);
            var solution = parseResult.GetValue(solutionOption);
            var force = parseResult.GetValue(forceOption);
            var referenceDirs = parseResult.GetValue(referenceDirOption)
                ?.Select(d => d.FullName)
                .ToArray();
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteWithVersionAsync(input, output, solution, force, referenceDirs, globalOptions, cancellationToken,
                parseResult.GetValue(registrationVersionOption));
        });

        return command;
    }

    private static Task<int> ExecuteAsync(
        FileInfo input,
        FileInfo? output,
        string? solution,
        bool force,
        IReadOnlyList<string>? referenceDirs,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken) =>
        ExecuteWithVersionAsync(input, output, solution, force, referenceDirs, globalOptions, cancellationToken, null);

    private static Task<int> ExecuteWithVersionAsync(
        FileInfo input, FileInfo? output, string? solution, bool force,
        IReadOnlyList<string>? referenceDirs, GlobalOptionValues globalOptions,
        CancellationToken cancellationToken, string? registrationVersion)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (registrationVersion != null) PluginRegistrationContract.ResolveVersion(registrationVersion);
            var extension = input.Extension.ToLowerInvariant();
            PluginAssemblyConfig assemblyConfig;

            if (extension == ".nupkg")
            {
                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Extracting from NuGet package: {input.Name}");

                assemblyConfig = NupkgExtractor.Extract(input.FullName, referenceDirs);
            }
            else if (extension == ".dll")
            {
                if (!globalOptions.IsJsonMode)
                    Console.Error.WriteLine($"Extracting from assembly: {input.Name}");

                using var extractor = AssemblyExtractor.Create(input.FullName, referenceDirs);
                assemblyConfig = extractor.Extract();
            }
            else
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    $"Unsupported file type: {extension}. Expected .dll or .nupkg",
                    Target: input.Name));
                return Task.FromResult(ExitCodes.InvalidArguments);
            }

            // Make path relative to output location
            var inputDir = input.DirectoryName ?? ".";
            var outputPath = output?.FullName ?? Path.Combine(inputDir, "registrations.json");
            var outputDir = Path.GetDirectoryName(outputPath) ?? ".";

            // Calculate relative path from output to input
            // Normalize to forward slashes for cross-platform compatibility
            var relativePath = Path.GetRelativePath(outputDir, input.FullName).Replace('\\', '/');
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
            var excludedSecrets = 0;
            var existingFile = new FileInfo(outputPath);

            if (existingFile.Exists && !force && !globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine("Merging with existing configuration...");

                var existingContent = File.ReadAllText(outputPath);
                var existingConfig = PluginRegistrationJson.Deserialize(existingContent);
                excludedSecrets += PluginRegistrationJson.CountExcludedSecrets(existingConfig);

                if (existingConfig != null)
                {
                    // Merge: preserve deployment settings from existing, update code-derived values from fresh
                    PluginExtractionMergeService.MergeAssemblyConfig(assemblyConfig, existingConfig, registrationVersion);
                }

                config = new PluginRegistrationConfig
                {
                    Schema = "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/main/schemas/plugin-registration.schema.json",
                    Version = registrationVersion ?? existingConfig?.Version ?? "1.0",
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Assemblies = [assemblyConfig],
                    ExtensionData = existingConfig?.ExtensionData
                };
            }
            else
            {
                config = new PluginRegistrationConfig
                {
                    Schema = "https://raw.githubusercontent.com/joshsmithxrm/power-platform-developer-suite/main/schemas/plugin-registration.schema.json",
                    Version = registrationVersion ?? "1.0",
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Assemblies = [assemblyConfig]
                };
            }

            excludedSecrets += PluginRegistrationJson.CountExcludedSecrets(config);
            var jsonContent = PluginRegistrationJson.SerializeForOutput(config);

            if (globalOptions.IsJsonMode)
            {
                // Output to stdout for tool integration
                if (writer is JsonOutputWriter jsonWriter)
                    jsonWriter.WriteSuccess(PluginRegistrationJson.ToOutputElement(config), new { excludedSecrets });
                else
                    writer.WriteSuccess(PluginRegistrationJson.ToOutputElement(config));
            }
            else
            {
                if (excludedSecrets > 0) Console.Error.WriteLine($"{excludedSecrets} secure configuration value(s) excluded from output.");
                // Write to file
                File.WriteAllText(outputPath, jsonContent);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Found {assemblyConfig.Types.Count} plugin type(s) with {assemblyConfig.Types.Sum(t => t.Steps.Count)} step(s)");
                Console.Error.WriteLine($"Output: {outputPath}");
            }

            return Task.FromResult(ExitCodes.Success);
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "extracting plugin registrations", debug: globalOptions.Debug);
            writer.WriteError(error);
            return Task.FromResult(ExceptionMapper.ToExitCode(ex));
        }
    }

    /// <summary>
    /// Merges deployment settings from existing config into fresh extracted config.
    /// Preserves: solution, runAsUser, description, deployment, asyncAutoDelete, and any unknown fields.
    /// </summary>
    private static void MergeAssemblyConfig(PluginAssemblyConfig fresh, PluginRegistrationConfig existing) =>
        PluginExtractionMergeService.MergeAssemblyConfig(fresh, existing);
}
