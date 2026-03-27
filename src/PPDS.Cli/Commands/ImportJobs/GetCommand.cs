using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ImportJobs;

/// <summary>
/// Get details for a specific import job.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var idArgument = new Argument<Guid>("id")
        {
            Description = "The import job ID"
        };

        var command = new Command("get", "Get details for a specific import job")
        {
            idArgument,
            ImportJobsCommandGroup.ProfileOption,
            ImportJobsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument);
            var profile = parseResult.GetValue(ImportJobsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ImportJobsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(id, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        Guid importJobId,
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
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var job = await importJobService.GetAsync(importJobId, cancellationToken);

            if (job == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Import job '{importJobId}' not found.",
                    null,
                    importJobId.ToString());
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            var makerUrl = DataverseUrlBuilder.BuildImportJobMakerUrl(connectionInfo.EnvironmentUrl, importJobId);

            if (globalOptions.IsJsonMode)
            {
                var output = new ImportJobDetailOutput
                {
                    Id = job.Id,
                    Name = job.Name,
                    SolutionName = job.SolutionName,
                    SolutionId = job.SolutionId,
                    Progress = job.Progress,
                    IsComplete = job.IsComplete,
                    StartedOn = job.StartedOn,
                    CompletedOn = job.CompletedOn,
                    CreatedOn = job.CreatedOn,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Import Job: {job.Id}");
                Console.Error.WriteLine($"  Name:         {job.Name ?? "-"}");
                Console.Error.WriteLine($"  Solution:     {job.SolutionName ?? "-"}");
                Console.Error.WriteLine($"  Progress:     {job.Progress:F0}%");
                Console.Error.WriteLine($"  Status:       {(job.IsComplete ? "Complete" : "In Progress")}");
                Console.Error.WriteLine($"  Started:      {job.StartedOn?.ToString("g") ?? "-"}");
                Console.Error.WriteLine($"  Completed:    {job.CompletedOn?.ToString("g") ?? "-"}");
                Console.Error.WriteLine($"  Created:      {job.CreatedOn?.ToString("g") ?? "-"}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting import job '{importJobId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ImportJobDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("solutionName")]
        public string? SolutionName { get; set; }

        [JsonPropertyName("solutionId")]
        public Guid? SolutionId { get; set; }

        [JsonPropertyName("progress")]
        public double Progress { get; set; }

        [JsonPropertyName("isComplete")]
        public bool IsComplete { get; set; }

        [JsonPropertyName("startedOn")]
        public DateTime? StartedOn { get; set; }

        [JsonPropertyName("completedOn")]
        public DateTime? CompletedOn { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
