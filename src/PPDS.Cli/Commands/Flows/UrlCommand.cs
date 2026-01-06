using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.Flows;

/// <summary>
/// Get the Power Automate maker URL for a cloud flow.
/// </summary>
public static class UrlCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "The unique name of the flow"
        };

        var command = new Command("url", "Get Power Automate maker URL for a flow")
        {
            nameArgument,
            FlowsCommandGroup.ProfileOption,
            FlowsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var profile = parseResult.GetValue(FlowsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(FlowsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
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

            var flowService = serviceProvider.GetRequiredService<IFlowService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            var flow = await flowService.GetAsync(name, cancellationToken);

            if (flow == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Cloud flow '{name}' not found"));
                return ExitCodes.NotFoundError;
            }

            // Require environment ID for maker URL
            if (string.IsNullOrEmpty(connectionInfo.EnvironmentId))
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.RequiredField,
                    "Environment ID is not available. Re-select the environment with 'ppds env select' to populate the environment ID."));
                return ExitCodes.ValidationError;
            }

            // Build Power Automate URL using the Power Platform environment ID GUID
            // Format: https://make.powerautomate.com/environments/{environmentId}/flows/{flowId}/details
            var makerUrl = $"https://make.powerautomate.com/environments/{connectionInfo.EnvironmentId}/flows/{flow.Id}/details";

            if (globalOptions.IsJsonMode)
            {
                var output = new UrlOutput
                {
                    Id = flow.Id,
                    UniqueName = flow.UniqueName,
                    DisplayName = flow.DisplayName,
                    MakerUrl = makerUrl
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.WriteLine(makerUrl);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting flow URL", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UrlOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
