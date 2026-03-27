using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.ImportJobs;

/// <summary>
/// Get the Maker portal URL for an import job.
/// </summary>
public static class UrlCommand
{
    public static Command Create()
    {
        var idArgument = new Argument<Guid>("id")
        {
            Description = "The import job ID"
        };

        var command = new Command("url", "Get the Maker portal URL for an import job")
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

            // Verify the job exists
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
                var output = new UrlOutput
                {
                    ImportJobId = importJobId,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                // Just output the URL for easy piping
                Console.WriteLine(makerUrl);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting URL for import job '{importJobId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UrlOutput
    {
        [JsonPropertyName("importJobId")]
        public Guid ImportJobId { get; set; }

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
