using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.ImportJobs;

namespace PPDS.Cli.Commands.ImportJobs;

/// <summary>
/// List import jobs in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Filter by solution name"
        };

        var topOption = new Option<int>("--top", "-n")
        {
            Description = "Maximum number of results to return",
            DefaultValueFactory = _ => 50
        };

        var command = new Command("list", "List import jobs in the environment")
        {
            ImportJobsCommandGroup.ProfileOption,
            ImportJobsCommandGroup.EnvironmentOption,
            solutionOption,
            topOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(ImportJobsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ImportJobsCommandGroup.EnvironmentOption);
            var solution = parseResult.GetValue(solutionOption);
            var top = parseResult.GetValue(topOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, solution, top, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        string? solutionFilter,
        int top,
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
            }

            var result = await importJobService.ListAsync(solutionFilter, cancellationToken);
            var jobs = result.Items;

            if (jobs.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { ImportJobs = [] });
                }
                else
                {
                    Console.Error.WriteLine("No import jobs found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    ImportJobs = jobs.Select(j => new ImportJobOutput
                    {
                        Id = j.Id,
                        Name = j.Name,
                        SolutionName = j.SolutionName,
                        Progress = j.Progress,
                        IsComplete = j.IsComplete,
                        StartedOn = j.StartedOn,
                        CompletedOn = j.CompletedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"{"Solution",-30} {"Progress",-10} {"Status",-12} {"Started",-20} {"Completed",-20}");
                Console.Error.WriteLine(new string('-', 95));

                foreach (var job in jobs)
                {
                    var solution = Truncate(job.SolutionName ?? job.Name ?? "-", 30);
                    var progress = $"{job.Progress:F0}%";
                    var status = job.IsComplete ? "Complete" : "In Progress";
                    var started = job.StartedOn?.ToString("g") ?? "-";
                    var completed = job.CompletedOn?.ToString("g") ?? "-";

                    Console.Error.WriteLine($"{solution,-30} {progress,-10} {status,-12} {started,-20} {completed,-20}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {jobs.Count} import job(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing import jobs", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("importJobs")]
        public List<ImportJobOutput> ImportJobs { get; set; } = [];
    }

    private sealed class ImportJobOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("solutionName")]
        public string? SolutionName { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("isComplete")]
        public bool IsComplete { get; set; }

        [JsonPropertyName("startedOn")]
        public DateTime? StartedOn { get; set; }

        [JsonPropertyName("completedOn")]
        public DateTime? CompletedOn { get; set; }
    }

    #endregion
}
