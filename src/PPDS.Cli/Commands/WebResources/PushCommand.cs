using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.WebResources;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Push locally-modified web resources back to Dataverse with conflict detection.
/// </summary>
public static class PushCommand
{
    public static Command Create()
    {
        var folderArgument = new Argument<string>("folder")
        {
            Description = "Folder previously populated by 'ppds webresources pull'"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Skip safety checks: server-conflict detection (someone edited since last pull) and environment URL validation (pulled from a different env than the current connection)"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Preview what would be pushed without uploading. Still authenticates and queries the server for conflict detection."
        };

        var publishOption = new Option<bool>("--publish")
        {
            Description = "After upload, run the Dataverse Publish step on the uploaded resources (required before changes take effect in apps)"
        };

        var command = new Command("push", "Push locally-modified web resources back to Dataverse")
        {
            folderArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption,
            forceOption,
            dryRunOption,
            publishOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(folderArgument)!;
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var force = parseResult.GetValue(forceOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var publish = parseResult.GetValue(publishOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(path, profile, environment, force, dryRun, publish, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string path,
        string? profile,
        string? environment,
        bool force,
        bool dryRun,
        bool publish,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Validate filesystem preconditions before authenticating — avoids a wasted auth round-trip
        // (and a misleading "Connected as ..." banner) when the user pointed push at a folder that
        // hasn't been pulled yet.
        try
        {
            ValidatePushTarget(path);
        }
        catch (PpdsException ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"pushing web resources from '{path}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var sync = serviceProvider.GetRequiredService<IWebResourceSyncService>();
            var pushOptions = new PushOptions(
                Folder: path,
                CurrentEnvironmentUrl: connectionInfo.EnvironmentUrl,
                Force: force,
                DryRun: dryRun,
                Publish: publish);

            IOperationProgress? progress = globalOptions.IsJsonMode ? null : new StderrOperationProgress();
            var result = await sync.PushAsync(pushOptions, progress, cancellationToken);

            if (result.Conflicts.Count > 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    var conflictError = new StructuredError(
                        ErrorCodes.WebResource.Conflict,
                        $"Push blocked: {result.Conflicts.Count} resource(s) have changed on the server since last pull. Run 'ppds webresources pull {path}' to fetch latest changes, or use --force to push anyway.",
                        null,
                        path);
                    writer.WriteResult(new CommandResult<PushOutput>
                    {
                        Success = false,
                        Data = BuildOutput(path, result),
                        Error = conflictError,
                    });
                }
                else
                {
                    Console.Error.WriteLine($"Conflicts detected: {result.Conflicts.Count} resource(s) have changed on the server since last pull.");
                    Console.Error.WriteLine();
                    foreach (var conflict in result.Conflicts)
                    {
                        Console.Error.WriteLine($"  {conflict.Name}");
                        Console.Error.WriteLine($"    tracked: {conflict.TrackedModifiedOn:o}");
                        Console.Error.WriteLine($"    server:  {conflict.ServerModifiedOn:o}");
                    }
                    Console.Error.WriteLine();
                    Console.Error.WriteLine($"Run 'ppds webresources pull {path}' to fetch latest changes, or use --force to push anyway.");
                }
                return ExitCodes.PreconditionFailed;
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(BuildOutput(path, result));
            }
            else
            {
                if (result.DryRun)
                {
                    Console.Error.WriteLine($"Dry run: would push {result.Pushed.Count} web resource(s) ({result.Skipped.Count} skipped)");
                    foreach (var p in result.Pushed)
                    {
                        Console.Error.WriteLine($"  + {p.Name}");
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Pushed {result.Pushed.Count} web resource(s) ({result.Skipped.Count} skipped, {result.Errors.Count} errors)");
                    if (publish)
                    {
                        Console.Error.WriteLine($"Published {result.PublishedCount} web resource(s)");
                    }
                }

                if (result.Errors.Count > 0)
                {
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("Errors:");
                    foreach (var err in result.Errors)
                    {
                        Console.Error.WriteLine($"  {err.Name}: {err.Error}");
                    }
                }
            }

            return result.Errors.Count > 0 ? ExitCodes.PartialSuccess : ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"pushing web resources from '{path}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Cheap filesystem check executed before authenticating to Dataverse. Mirrors the
    /// preconditions enforced by <see cref="WebResourceSyncService.PushAsync"/> so the user
    /// gets the same error without paying for an auth round-trip.
    /// </summary>
    private static void ValidatePushTarget(string path)
    {
        var rootAbsolute = Path.GetFullPath(path);

        if (File.Exists(rootAbsolute))
        {
            throw new PpdsException(
                ErrorCodes.Validation.InvalidValue,
                $"Path '{path}' is a file, not a folder. Pass the folder that contains the pulled web resources (the folder with the .ppds/webresources.json tracking file).");
        }

        if (!Directory.Exists(rootAbsolute))
        {
            throw new PpdsException(
                ErrorCodes.Validation.FileNotFound,
                $"Folder '{path}' does not exist. Run 'ppds webresources pull {path}' first.");
        }

        var trackingPath = Path.Combine(rootAbsolute, WebResourceTrackingFile.TrackingFileRelativePath);
        if (!File.Exists(trackingPath))
        {
            throw new PpdsException(
                ErrorCodes.Validation.FileNotFound,
                $"Tracking file '{WebResourceTrackingFile.TrackingFileRelativePath}' not found in '{path}'. Run 'ppds webresources pull {path}' first.");
        }
    }

    private static PushOutput BuildOutput(string path, PushResult result) => new()
    {
        Folder = path,
        DryRun = result.DryRun,
        PublishedCount = result.PublishedCount,
        Pushed = result.Pushed.Select(p => new PushedOutput { Name = p.Name, LocalPath = p.LocalPath }).ToList(),
        Conflicts = result.Conflicts.Select(c => new ConflictOutput
        {
            Name = c.Name,
            TrackedModifiedOn = c.TrackedModifiedOn,
            ServerModifiedOn = c.ServerModifiedOn,
        }).ToList(),
        Skipped = result.Skipped.Select(s => new SkippedOutput { Name = s.Name, Reason = s.Reason }).ToList(),
        Errors = result.Errors.Select(e => new ErrorOutput { Name = e.Name, Error = e.Error }).ToList(),
    };

    #region Output Models

    private sealed class PushOutput
    {
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = string.Empty;

        [JsonPropertyName("dryRun")]
        public bool DryRun { get; set; }

        [JsonPropertyName("publishedCount")]
        public int PublishedCount { get; set; }

        [JsonPropertyName("pushed")]
        public List<PushedOutput> Pushed { get; set; } = [];

        [JsonPropertyName("conflicts")]
        public List<ConflictOutput> Conflicts { get; set; } = [];

        [JsonPropertyName("skipped")]
        public List<SkippedOutput> Skipped { get; set; } = [];

        [JsonPropertyName("errors")]
        public List<ErrorOutput> Errors { get; set; } = [];
    }

    private sealed class PushedOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("localPath")]
        public string LocalPath { get; set; } = string.Empty;
    }

    private sealed class ConflictOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("trackedModifiedOn")]
        public DateTime? TrackedModifiedOn { get; set; }

        [JsonPropertyName("serverModifiedOn")]
        public DateTime? ServerModifiedOn { get; set; }
    }

    private sealed class SkippedOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    private sealed class ErrorOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;
    }

    #endregion
}
