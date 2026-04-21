using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ImportJobs;

namespace PPDS.Cli.Commands.ImportJobs;

/// <summary>
/// Get the raw XML data for an import job.
/// </summary>
public static class DataCommand
{
    public static Command Create()
    {
        var idArgument = new Argument<Guid>("id")
        {
            Description = "The import job ID"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output file path (default: stdout)"
        };

        var command = new Command("data", "Get the raw XML data for an import job")
        {
            idArgument,
            outputOption,
            ImportJobsCommandGroup.ProfileOption,
            ImportJobsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument);
            var output = parseResult.GetValue(outputOption);
            var profile = parseResult.GetValue(ImportJobsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ImportJobsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(id, output, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid importJobId,
        string? outputPath,
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

            var importJobService = serviceProvider.GetRequiredService<IImportJobService>();

            if (!globalOptions.IsJsonMode && string.IsNullOrEmpty(outputPath))
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var data = await importJobService.GetDataAsync(importJobId, cancellationToken);

            if (data == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Import job '{importJobId}' not found or has no data.",
                    null,
                    importJobId.ToString());
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (!string.IsNullOrEmpty(outputPath))
            {
                var fullPath = Path.GetFullPath(outputPath);
                await File.WriteAllTextAsync(fullPath, data, cancellationToken);

                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Data written to: {fullPath}");
                }
                else
                {
                    var output = new DataOutput
                    {
                        ImportJobId = importJobId,
                        FilePath = fullPath,
                        SizeBytes = data.Length
                    };
                    writer.WriteSuccess(output);
                }
            }
            else
            {
                // Output to stdout
                if (globalOptions.IsJsonMode)
                {
                    // Wrap XML in JSON structure for consistent JSON output
                    var output = new DataOutput
                    {
                        ImportJobId = importJobId,
                        Data = data,
                        SizeBytes = data.Length
                    };
                    writer.WriteSuccess(output);
                }
                else
                {
                    Console.WriteLine(data);
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting data for import job '{importJobId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class DataOutput
    {
        [JsonPropertyName("importJobId")]
        public Guid ImportJobId { get; set; }

        [JsonPropertyName("filePath")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? FilePath { get; set; }

        [JsonPropertyName("data")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Data { get; set; }

        [JsonPropertyName("sizeBytes")]
        public long SizeBytes { get; set; }
    }

    #endregion
}
