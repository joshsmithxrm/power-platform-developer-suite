using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ImportJobs;

namespace PPDS.Cli.Commands.ImportJobs;

/// <summary>
/// Wait for an import job to complete.
/// </summary>
public static class WaitCommand
{
    public static Command Create()
    {
        var idArgument = new Argument<Guid>("id")
        {
            Description = "The import job ID"
        };

        var timeoutOption = new Option<int>("--timeout", "-t")
        {
            Description = "Maximum wait time in minutes",
            DefaultValueFactory = _ => 30
        };

        var intervalOption = new Option<int>("--interval", "-i")
        {
            Description = "Poll interval in seconds",
            DefaultValueFactory = _ => 5
        };

        var command = new Command("wait", "Wait for an import job to complete")
        {
            idArgument,
            timeoutOption,
            intervalOption,
            ImportJobsCommandGroup.ProfileOption,
            ImportJobsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument);
            var timeout = parseResult.GetValue(timeoutOption);
            var interval = parseResult.GetValue(intervalOption);
            var profile = parseResult.GetValue(ImportJobsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ImportJobsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(id, timeout, interval, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid importJobId,
        int timeoutMinutes,
        int intervalSeconds,
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

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Waiting for import job {importJobId} to complete...");
            }

            var startTime = DateTime.UtcNow;
            double lastProgress = -1;

            var job = await importJobService.WaitForCompletionAsync(
                importJobId,
                TimeSpan.FromSeconds(intervalSeconds),
                TimeSpan.FromMinutes(timeoutMinutes),
                j =>
                {
                    if (!globalOptions.IsJsonMode && Math.Abs(j.Progress - lastProgress) >= 1)
                    {
                        Console.Error.WriteLine($"  Progress: {j.Progress:F0}%");
                        lastProgress = j.Progress;
                    }
                },
                cancellationToken);

            var duration = DateTime.UtcNow - startTime;

            if (globalOptions.IsJsonMode)
            {
                var output = new WaitOutput
                {
                    Id = job.Id,
                    SolutionName = job.SolutionName,
                    Progress = job.Progress,
                    IsComplete = job.IsComplete,
                    CompletedOn = job.CompletedOn,
                    DurationSeconds = duration.TotalSeconds
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Import job completed in {duration.TotalSeconds:F1} seconds.");
                Console.Error.WriteLine($"  Solution: {job.SolutionName ?? "-"}");
                Console.Error.WriteLine($"  Progress: {job.Progress:F0}%");
            }

            return ExitCodes.Success;
        }
        catch (TimeoutException ex)
        {
            var error = new StructuredError(
                ErrorCodes.Operation.Timeout,
                ex.Message,
                null,
                importJobId.ToString());
            writer.WriteError(error);
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"waiting for import job '{importJobId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class WaitOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("solutionName")]
        public string? SolutionName { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("isComplete")]
        public bool IsComplete { get; set; }

        [JsonPropertyName("completedOn")]
        public DateTime? CompletedOn { get; set; }

        [JsonPropertyName("durationSeconds")]
        public double DurationSeconds { get; set; }
    }

    #endregion
}
