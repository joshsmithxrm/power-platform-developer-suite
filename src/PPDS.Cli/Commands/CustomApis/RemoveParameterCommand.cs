using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Remove a request parameter or response property from a Custom API.
/// </summary>
public static class RemoveParameterCommand
{
    public static Command Create()
    {
        var apiNameArgument = new Argument<string>("api-name")
        {
            Description = "Custom API unique name or GUID"
        };

        var paramNameArgument = new Argument<string>("param-name")
        {
            Description = "Parameter unique name to remove"
        };

        var command = new Command("remove-parameter", "Remove a request parameter or response property from a Custom API")
        {
            apiNameArgument,
            paramNameArgument,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var apiName = parseResult.GetValue(apiNameArgument)!;
            var paramName = parseResult.GetValue(paramNameArgument)!;
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(apiName, paramName, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string apiName,
        string paramName,
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

            await customApiService.RemoveParameterAsync(param.Id, cancellationToken);

            var result = new RemoveParameterResult
            {
                Success = true,
                Operation = "remove-parameter",
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
                Console.Error.WriteLine($"Removed parameter '{param.UniqueName}' from Custom API '{api.UniqueName}'");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"removing parameter '{paramName}' from Custom API '{apiName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class RemoveParameterResult
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
