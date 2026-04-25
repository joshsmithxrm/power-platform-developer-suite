using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Solutions;
using PPDS.Cli.Services.WebResources;

namespace PPDS.Cli.Commands.WebResources;

/// <summary>
/// Pull web resources from a Dataverse environment to a local folder with tracking metadata.
/// </summary>
public static class PullCommand
{
    /// <summary>
    /// Type shortcuts shared with <see cref="ListCommand"/>. Duplicated locally to avoid
    /// exposing internal listcommand state; the small duplication is preferable to coupling.
    /// </summary>
    private static readonly Dictionary<string, int[]> TypeMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["text"] = [1, 2, 3, 4, 9, 11, 12],
        ["image"] = [5, 6, 7, 10, 11],
        ["data"] = [4, 12],
        ["html"] = [1],
        ["css"] = [2],
        ["js"] = [3], ["javascript"] = [3],
        ["xml"] = [4],
        ["png"] = [5],
        ["jpg"] = [6], ["jpeg"] = [6],
        ["gif"] = [7],
        ["xap"] = [8],
        ["xsl"] = [9], ["xslt"] = [9],
        ["ico"] = [10],
        ["svg"] = [11],
        ["resx"] = [12],
    };

    public static Command Create()
    {
        var folderArgument = new Argument<string>("folder")
        {
            Description = "Target directory (created if missing)"
        };

        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Filter by solution unique name"
        };

        var typeOption = new Option<string?>("--type", "-t")
        {
            Description = "Filter by type: text, image, data, or specific (js, css, html, xml, png, etc.)"
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "Filter by partial name match"
        };

        var stripPrefixOption = new Option<bool>("--strip-prefix")
        {
            Description = "Remove publisher prefix from local file paths (e.g., new_/scripts/app.js -> scripts/app.js)"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Overwrite local files even if they have uncommitted changes"
        };

        var command = new Command("pull", "Pull web resources to a local folder with tracking metadata")
        {
            folderArgument,
            WebResourcesCommandGroup.ProfileOption,
            WebResourcesCommandGroup.EnvironmentOption,
            solutionOption,
            typeOption,
            nameOption,
            stripPrefixOption,
            forceOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var folder = parseResult.GetValue(folderArgument)!;
            var profile = parseResult.GetValue(WebResourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(WebResourcesCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(solutionOption);
            var type = parseResult.GetValue(typeOption);
            var name = parseResult.GetValue(nameOption);
            var stripPrefix = parseResult.GetValue(stripPrefixOption);
            var force = parseResult.GetValue(forceOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(folder, profile, environment, solution, type, name, stripPrefix, force, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string folder,
        string? profile,
        string? environment,
        string? solution,
        string? type,
        string? namePattern,
        bool stripPrefix,
        bool force,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        int[]? typeCodes = null;
        if (type != null)
        {
            if (!TypeMap.TryGetValue(type, out typeCodes))
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    $"Unknown type '{type}'. Supported: text, image, data, js, css, html, xml, png, jpg, gif, svg, ico, xsl, resx",
                    null,
                    type));
                return ExitCodes.InvalidArguments;
            }
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

            Guid? solutionId = null;
            if (solution != null)
            {
                var solutionService = serviceProvider.GetRequiredService<ISolutionService>();
                var solutionInfo = await solutionService.GetAsync(solution, cancellationToken);
                if (solutionInfo == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Solution '{solution}' not found.",
                        null,
                        solution));
                    return ExitCodes.NotFoundError;
                }
                solutionId = solutionInfo.Id;
            }

            var sync = serviceProvider.GetRequiredService<IWebResourceSyncService>();
            var pullOptions = new PullOptions(
                Folder: folder,
                EnvironmentUrl: connectionInfo.EnvironmentUrl,
                SolutionId: solutionId,
                SolutionUniqueName: solution,
                TypeCodes: typeCodes,
                NamePattern: namePattern,
                StripPrefix: stripPrefix,
                Force: force);

            var result = await sync.PullAsync(pullOptions, progress: null, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new PullOutput
                {
                    Folder = folder,
                    TotalServerCount = result.TotalServerCount,
                    Pulled = result.Pulled.Select(p => new PulledOutput { Name = p.Name, LocalPath = p.LocalPath, IsNew = p.IsNew }).ToList(),
                    Skipped = result.Skipped.Select(s => new SkippedOutput { Name = s.Name, Reason = s.Reason }).ToList(),
                    Errors = result.Errors.Select(e => new ErrorOutput { Name = e.Name, Error = e.Error }).ToList(),
                });
            }
            else
            {
                var newCount = result.Pulled.Count(p => p.IsNew);
                var updatedCount = result.Pulled.Count - newCount;
                Console.Error.WriteLine($"Pulled {result.Pulled.Count} of {result.TotalServerCount} web resource(s) to {folder} ({newCount} new, {updatedCount} updated, {result.Skipped.Count} skipped, {result.Errors.Count} errors)");

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
            var error = ExceptionMapper.Map(ex, context: $"pulling web resources to '{folder}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class PullOutput
    {
        [JsonPropertyName("folder")]
        public string Folder { get; set; } = string.Empty;

        [JsonPropertyName("totalServerCount")]
        public int TotalServerCount { get; set; }

        [JsonPropertyName("pulled")]
        public List<PulledOutput> Pulled { get; set; } = [];

        [JsonPropertyName("skipped")]
        public List<SkippedOutput> Skipped { get; set; } = [];

        [JsonPropertyName("errors")]
        public List<ErrorOutput> Errors { get; set; } = [];
    }

    private sealed class PulledOutput
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("localPath")]
        public string LocalPath { get; set; } = string.Empty;

        [JsonPropertyName("isNew")]
        public bool IsNew { get; set; }
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
