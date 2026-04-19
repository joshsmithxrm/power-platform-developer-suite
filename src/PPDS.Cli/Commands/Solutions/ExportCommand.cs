using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// Export a solution to a ZIP file.
/// </summary>
public static class ExportCommand
{
    public static Command Create()
    {
        var uniqueNameArgument = new Argument<string>("unique-name")
        {
            Description = "The solution unique name"
        };

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output file path (default: <unique-name>.zip or <unique-name>_managed.zip)"
        };

        var managedOption = new Option<bool>("--managed")
        {
            Description = "Export as managed solution"
        };

        var allowOutsideWorkspaceOption = new Option<bool>("--allow-outside-workspace")
        {
            Description = "Permit --output paths that resolve outside the current working directory. " +
                          "Off by default to prevent a mistyped or redirected path from writing anywhere on disk."
        };

        var command = new Command("export", "Export a solution to a ZIP file")
        {
            uniqueNameArgument,
            outputOption,
            managedOption,
            allowOutsideWorkspaceOption,
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueName = parseResult.GetValue(uniqueNameArgument)!;
            var output = parseResult.GetValue(outputOption);
            var managed = parseResult.GetValue(managedOption);
            var allowOutsideWorkspace = parseResult.GetValue(allowOutsideWorkspaceOption);
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueName, output, managed, allowOutsideWorkspace, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueName,
        string? outputPath,
        bool managed,
        bool allowOutsideWorkspace,
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

            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Exporting solution: {uniqueName} ({(managed ? "managed" : "unmanaged")})...");
            }

            // Verify solution exists
            var solution = await solutionService.GetAsync(uniqueName, cancellationToken);
            if (solution == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Solution '{uniqueName}' not found.",
                    null,
                    uniqueName);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            var solutionZip = await solutionService.ExportAsync(uniqueName, managed, cancellationToken);

            // Determine output path
            var filePath = outputPath ?? $"{uniqueName}{(managed ? "_managed" : "")}.zip";
            var workspaceRoot = Path.GetFullPath(Environment.CurrentDirectory);
            var fullPath = Path.GetFullPath(filePath, workspaceRoot);

            // Refuse to write outside the current working directory unless the operator explicitly
            // opts in. Guards against mistyped paths and against an invoking script redirecting
            // output to a privileged location. RPC callers have no opt-out — only the CLI does.
            if (!allowOutsideWorkspace && !IsUnderWorkspace(fullPath, workspaceRoot))
            {
                var error = new StructuredError(
                    ErrorCodes.Validation.PathOutsideWorkspace,
                    $"Output path '{fullPath}' resolves outside the current working directory '{workspaceRoot}'. " +
                    "Re-run with --allow-outside-workspace to permit this write.",
                    null,
                    "--output");
                writer.WriteError(error);
                return ExitCodes.ValidationError;
            }

            await File.WriteAllBytesAsync(fullPath, solutionZip, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new ExportOutput
                {
                    UniqueName = uniqueName,
                    Managed = managed,
                    FilePath = fullPath,
                    FileSizeBytes = solutionZip.Length
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Exported to: {fullPath}");
                Console.Error.WriteLine($"Size: {FormatBytes(solutionZip.Length)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"exporting solution '{uniqueName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="candidate"/> is equal to or nested under <paramref name="root"/>.
    /// Uses a trailing directory separator to prevent prefix-match false positives (e.g., "C:\\work" vs "C:\\work-evil").
    /// </summary>
    internal static bool IsUnderWorkspace(string candidate, string root)
    {
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidate.Equals(root, comparison) || candidate.StartsWith(rootWithSep, comparison);
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB"];
        var order = 0;
        var size = (double)bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:0.##} {sizes[order]}";
    }

    #region Output Models

    private sealed class ExportOutput
    {
        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("managed")]
        public bool Managed { get; set; }

        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("fileSizeBytes")]
        public long FileSizeBytes { get; set; }
    }

    #endregion
}
