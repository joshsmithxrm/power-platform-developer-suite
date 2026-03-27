using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// Get the Maker portal URL for a solution.
/// </summary>
public static class UrlCommand
{
    public static Command Create()
    {
        var uniqueNameArgument = new Argument<string>("unique-name")
        {
            Description = "The solution unique name"
        };

        var command = new Command("url", "Get the Maker portal URL for a solution")
        {
            uniqueNameArgument,
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueName = parseResult.GetValue(uniqueNameArgument)!;
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueName, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueName,
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
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

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

            var makerUrl = DataverseUrlBuilder.BuildSolutionMakerUrl(connectionInfo.EnvironmentUrl, solution.Id);

            if (globalOptions.IsJsonMode)
            {
                var output = new UrlOutput
                {
                    UniqueName = uniqueName,
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
            var error = ExceptionMapper.Map(ex, context: $"getting URL for solution '{uniqueName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UrlOutput
    {
        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
