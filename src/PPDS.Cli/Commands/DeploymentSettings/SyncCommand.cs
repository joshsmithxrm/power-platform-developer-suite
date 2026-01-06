using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.DeploymentSettings;

/// <summary>
/// Sync an existing deployment settings file with the current solution.
/// </summary>
public static class SyncCommand
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public static Command Create()
    {
        var fileOption = new Option<string>("--file", "-f")
        {
            Description = "Deployment settings file path",
            Required = true
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show what would change without modifying the file"
        };

        var command = new Command("sync", "Sync deployment settings file with solution (preserves existing values)")
        {
            DeploymentSettingsCommandGroup.SolutionOption,
            fileOption,
            dryRunOption,
            DeploymentSettingsCommandGroup.ProfileOption,
            DeploymentSettingsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(DeploymentSettingsCommandGroup.SolutionOption)!;
            var file = parseResult.GetValue(fileOption)!;
            var dryRun = parseResult.GetValue(dryRunOption);
            var profile = parseResult.GetValue(DeploymentSettingsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DeploymentSettingsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, file, dryRun, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string solution,
        string filePath,
        bool dryRun,
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

            var settingsService = serviceProvider.GetRequiredService<IDeploymentSettingsService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();

                if (dryRun)
                {
                    Console.Error.WriteLine("[Dry-Run Mode] No changes will be applied.");
                    Console.Error.WriteLine();
                }

                Console.Error.WriteLine($"Syncing deployment settings for solution '{solution}'...");
            }

            // Load existing settings if file exists
            DeploymentSettingsFile? existingSettings = null;
            var fullPath = Path.GetFullPath(filePath);

            if (File.Exists(fullPath))
            {
                var existingJson = await File.ReadAllTextAsync(fullPath, cancellationToken);
                existingSettings = JsonSerializer.Deserialize<DeploymentSettingsFile>(existingJson, JsonReadOptions);
            }

            var result = await settingsService.SyncAsync(solution, existingSettings, cancellationToken);

            var evStats = result.EnvironmentVariables;
            var crStats = result.ConnectionReferences;

            if (!dryRun)
            {
                // Write updated file
                var json = JsonSerializer.Serialize(result.Settings, JsonWriteOptions);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                await File.WriteAllTextAsync(fullPath, json, cancellationToken);
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new SyncOutput
                {
                    FilePath = fullPath,
                    DryRun = dryRun,
                    EnvironmentVariables = new SyncStatsOutput
                    {
                        Added = evStats.Added,
                        Removed = evStats.Removed,
                        Preserved = evStats.Preserved
                    },
                    ConnectionReferences = new SyncStatsOutput
                    {
                        Added = crStats.Added,
                        Removed = crStats.Removed,
                        Preserved = crStats.Preserved
                    }
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Sync complete{(dryRun ? " (dry-run)" : "")}:");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Environment Variables:");
                Console.Error.WriteLine($"    Added:     {evStats.Added}");
                Console.Error.WriteLine($"    Removed:   {evStats.Removed}");
                Console.Error.WriteLine($"    Preserved: {evStats.Preserved}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Connection References:");
                Console.Error.WriteLine($"    Added:     {crStats.Added}");
                Console.Error.WriteLine($"    Removed:   {crStats.Removed}");
                Console.Error.WriteLine($"    Preserved: {crStats.Preserved}");

                if (!dryRun)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Updated: {fullPath}");
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "syncing deployment settings", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SyncOutput
    {
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("dryRun")]
        public bool DryRun { get; set; }

        [JsonPropertyName("environmentVariables")]
        public SyncStatsOutput EnvironmentVariables { get; set; } = new();

        [JsonPropertyName("connectionReferences")]
        public SyncStatsOutput ConnectionReferences { get; set; } = new();
    }

    private sealed class SyncStatsOutput
    {
        [JsonPropertyName("added")]
        public int Added { get; set; }

        [JsonPropertyName("removed")]
        public int Removed { get; set; }

        [JsonPropertyName("preserved")]
        public int Preserved { get; set; }
    }

    #endregion
}
