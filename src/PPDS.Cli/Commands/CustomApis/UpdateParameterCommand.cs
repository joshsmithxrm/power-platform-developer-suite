using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Update mutable properties (display name, description) of a Custom API parameter.
/// </summary>
public static class UpdateParameterCommand
{
    public static Command Create()
    {
        var apiNameArgument = new Argument<string>("api-name")
        {
            Description = "Custom API unique name or GUID"
        };

        var paramNameArgument = new Argument<string>("param-name")
        {
            Description = "Parameter unique name to update"
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "New display name for the parameter"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "New description for the parameter"
        };

        var command = new Command("update-parameter", "Update display name or description of a Custom API parameter")
        {
            apiNameArgument,
            paramNameArgument,
            displayNameOption,
            descriptionOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var apiName = parseResult.GetValue(apiNameArgument)!;
            var paramName = parseResult.GetValue(paramNameArgument)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(apiName, paramName, displayName, description, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string apiName,
        string paramName,
        string? displayName,
        string? description,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        if (displayName == null && description == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.RequiredField,
                "At least one of --display-name or --description must be provided.",
                Target: "options"));
            return ExitCodes.ValidationError;
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

            var customApiService = serviceProvider.GetRequiredService<ICustomApiService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve the Custom API and find the parameter
            var api = await customApiService.GetAsync(apiName, cancellationToken);
            if (api == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Custom API '{apiName}' not found.",
                    Target: apiName));
                return ExitCodes.NotFoundError;
            }

            // Look up in request parameters then response properties
            var allParams = api.RequestParameters.Concat(api.ResponseProperties).ToList();
            var param = allParams.FirstOrDefault(p =>
                string.Equals(p.UniqueName, paramName, StringComparison.OrdinalIgnoreCase));

            if (param == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Parameter '{paramName}' not found on Custom API '{api.UniqueName}'.",
                    Target: paramName));
                return ExitCodes.NotFoundError;
            }

            await customApiService.UpdateParameterAsync(
                param.Id,
                new CustomApiParameterUpdateRequest(displayName, description),
                cancellationToken);

            var result = new UpdateParameterResult
            {
                Success = true,
                Operation = "update-parameter",
                ParameterId = param.Id,
                ApiId = api.Id,
                ApiUniqueName = api.UniqueName,
                ParamName = param.UniqueName
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Updated parameter '{param.UniqueName}' on Custom API '{api.UniqueName}'");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"updating parameter '{paramName}' on Custom API '{apiName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class UpdateParameterResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("parameterId")]
        public Guid ParameterId { get; set; }

        [JsonPropertyName("apiId")]
        public Guid ApiId { get; set; }

        [JsonPropertyName("apiUniqueName")]
        public string ApiUniqueName { get; set; } = string.Empty;

        [JsonPropertyName("paramName")]
        public string ParamName { get; set; } = string.Empty;
    }

    #endregion
}
