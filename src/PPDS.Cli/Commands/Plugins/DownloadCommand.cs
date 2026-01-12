using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;

namespace PPDS.Cli.Commands.Plugins;

/// <summary>
/// Download plugin assembly or package binary content from Dataverse.
/// </summary>
public static class DownloadCommand
{
    public static Command Create()
    {
        var command = new Command("download", "Download plugin assembly or package binary from Dataverse");

        command.Subcommands.Add(CreateAssemblySubcommand());
        command.Subcommands.Add(CreatePackageSubcommand());

        return command;
    }

    private static Command CreateAssemblySubcommand()
    {
        var nameArgument = new Argument<string>("name-or-id")
        {
            Description = "Assembly name or GUID"
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output file path or directory",
            Required = true
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Overwrite existing file"
        };

        var command = new Command("assembly", "Download a plugin assembly DLL")
        {
            nameArgument,
            outputOption,
            forceOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAssemblyAsync(nameOrId, output, force, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static Command CreatePackageSubcommand()
    {
        var nameArgument = new Argument<string>("name-or-id")
        {
            Description = "Package name, unique name, or GUID"
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output file path or directory",
            Required = true
        };

        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Overwrite existing file"
        };

        var command = new Command("package", "Download a plugin package (nupkg)")
        {
            nameArgument,
            outputOption,
            forceOption,
            PluginsCommandGroup.ProfileOption,
            PluginsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(PluginsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(PluginsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecutePackageAsync(nameOrId, output, force, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAssemblyAsync(
        string nameOrId,
        string output,
        bool force,
        string? profile,
        string? environment,
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

            // Resolve name to ID if needed
            Guid assemblyId;
            string assemblyName;

            if (Guid.TryParse(nameOrId, out assemblyId))
            {
                // User provided a GUID directly - we'll get the name from the download
                assemblyName = nameOrId;
            }
            else
            {
                // Look up by name
                var assembly = await registrationService.GetAssemblyByNameAsync(nameOrId, cancellationToken);
                if (assembly == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Assembly not found: {nameOrId}",
                        Target: nameOrId));
                    return ExitCodes.NotFoundError;
                }
                assemblyId = assembly.Id;
                assemblyName = assembly.Name;
            }

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Downloading assembly: {assemblyName}");
            }

            var (content, fileName) = await registrationService.DownloadAssemblyAsync(assemblyId, cancellationToken);

            // If user passed GUID, update the assemblyName to actual name from download
            if (Guid.TryParse(nameOrId, out _))
            {
                assemblyName = Path.GetFileNameWithoutExtension(fileName);
            }

            // Determine output path
            var outputPath = ResolveOutputPath(output, fileName, force, globalOptions, writer, out var pathError);
            if (outputPath == null)
            {
                return pathError;
            }

            if (globalOptions.IsJsonMode)
            {
                // JSON mode: output metadata only, no file written
                writer.WriteSuccess(new DownloadMetadata
                {
                    Name = assemblyName,
                    Id = assemblyId,
                    ContentSize = content.Length,
                    FileName = fileName
                });
            }
            else
            {
                // Write file
                await File.WriteAllBytesAsync(outputPath, content, cancellationToken);

                var sizeKb = content.Length / 1024.0;
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Downloaded: {fileName} ({sizeKb:F1} KB)");
                Console.Error.WriteLine($"  Source: {assemblyName} ({assemblyId})");
                Console.Error.WriteLine($"  Output: {Path.GetFullPath(outputPath)}");
            }

            return ExitCodes.Success;
        }
        catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Plugin.NoContent)
        {
            writer.WriteError(new StructuredError(
                ex.ErrorCode,
                ex.UserMessage,
                Target: nameOrId));
            return ExitCodes.InvalidArguments;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "downloading assembly", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<int> ExecutePackageAsync(
        string nameOrId,
        string output,
        bool force,
        string? profile,
        string? environment,
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

            // Resolve name to ID if needed
            Guid packageId;
            string packageName;
            string? packageVersion = null;

            if (Guid.TryParse(nameOrId, out packageId))
            {
                // User provided a GUID directly
                packageName = nameOrId;
            }
            else
            {
                // Look up by name or unique name
                var package = await registrationService.GetPackageByNameAsync(nameOrId, cancellationToken);
                if (package == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Package not found: {nameOrId}",
                        Target: nameOrId));
                    return ExitCodes.NotFoundError;
                }
                packageId = package.Id;
                packageName = package.Name;
                packageVersion = package.Version;
            }

            if (!globalOptions.IsJsonMode)
            {
                var versionSuffix = !string.IsNullOrEmpty(packageVersion) ? $" v{packageVersion}" : "";
                Console.Error.WriteLine($"Downloading package: {packageName}{versionSuffix}");
            }

            var (content, fileName) = await registrationService.DownloadPackageAsync(packageId, cancellationToken);

            // If user passed GUID, parse name from fileName
            if (Guid.TryParse(nameOrId, out _))
            {
                // fileName format: PackageName.Version.nupkg
                var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
                packageName = nameWithoutExt;
            }

            // Determine output path
            var outputPath = ResolveOutputPath(output, fileName, force, globalOptions, writer, out var pathError);
            if (outputPath == null)
            {
                return pathError;
            }

            if (globalOptions.IsJsonMode)
            {
                // JSON mode: output metadata only, no file written
                writer.WriteSuccess(new DownloadMetadata
                {
                    Name = packageName,
                    Id = packageId,
                    ContentSize = content.Length,
                    Version = packageVersion,
                    FileName = fileName
                });
            }
            else
            {
                // Write file
                await File.WriteAllBytesAsync(outputPath, content, cancellationToken);

                var sizeKb = content.Length / 1024.0;
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Downloaded: {fileName} ({sizeKb:F1} KB)");
                Console.Error.WriteLine($"  Source: {packageName} ({packageId})");
                Console.Error.WriteLine($"  Output: {Path.GetFullPath(outputPath)}");
            }

            return ExitCodes.Success;
        }
        catch (PpdsException ex) when (ex.ErrorCode == ErrorCodes.Plugin.NoContent)
        {
            writer.WriteError(new StructuredError(
                ex.ErrorCode,
                ex.UserMessage,
                Target: nameOrId));
            return ExitCodes.InvalidArguments;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "downloading package", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Resolves the output path, handling both file and directory cases.
    /// Returns null if path validation fails.
    /// </summary>
    private static string? ResolveOutputPath(
        string output,
        string defaultFileName,
        bool force,
        GlobalOptionValues globalOptions,
        IOutputWriter writer,
        out int errorCode)
    {
        errorCode = ExitCodes.Success;
        string outputPath;

        // Check if output is a directory (ends with separator or exists as directory)
        var isDirectory = output.EndsWith(Path.DirectorySeparatorChar) ||
                         output.EndsWith(Path.AltDirectorySeparatorChar) ||
                         Directory.Exists(output);

        if (isDirectory)
        {
            // Ensure directory exists
            if (!Directory.Exists(output))
            {
                try
                {
                    Directory.CreateDirectory(output);
                }
                catch (Exception ex)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Validation.DirectoryNotFound,
                        $"Cannot create directory: {output}",
                        Details: ex.Message,
                        Target: output));
                    errorCode = ExitCodes.Failure;
                    return null;
                }
            }

            outputPath = Path.Combine(output, defaultFileName);
        }
        else
        {
            outputPath = output;

            // Ensure parent directory exists
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                try
                {
                    Directory.CreateDirectory(directory);
                }
                catch (Exception ex)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Validation.DirectoryNotFound,
                        $"Cannot create directory: {directory}",
                        Details: ex.Message,
                        Target: directory));
                    errorCode = ExitCodes.Failure;
                    return null;
                }
            }
        }

        // Check if file exists (skip in JSON mode since we don't write files)
        if (!globalOptions.IsJsonMode && File.Exists(outputPath) && !force)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                $"Output file already exists. Use --force to overwrite.",
                Target: outputPath));
            errorCode = ExitCodes.Failure;
            return null;
        }

        return outputPath;
    }

    #region Output Models

    private sealed class DownloadMetadata
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("contentSize")]
        public int ContentSize { get; set; }

        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;
    }

    #endregion
}
