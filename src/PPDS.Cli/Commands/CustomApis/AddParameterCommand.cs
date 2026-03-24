using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Add a request parameter or response property to an existing Custom API.
/// </summary>
public static class AddParameterCommand
{
    public static Command Create()
    {
        var apiNameArgument = new Argument<string>("api-name")
        {
            Description = "Custom API unique name or GUID"
        };

        var paramNameArgument = new Argument<string>("param-name")
        {
            Description = "Unique parameter name"
        };

        var typeOption = new Option<string>("--type")
        {
            Description = "Data type: Boolean, DateTime, Decimal, Entity, EntityCollection, EntityReference, Float, Integer, Money, Picklist, String, StringArray, or Guid",
            Required = true
        };

        var directionOption = new Option<string>("--direction")
        {
            Description = "Direction: input or output",
            Required = true
        };

        var optionalOption = new Option<bool>("--optional")
        {
            Description = "Mark the parameter as optional (input parameters only)",
            DefaultValueFactory = _ => false
        };

        var entityOption = new Option<string?>("--entity")
        {
            Description = "Logical entity name (required for Entity, EntityCollection, EntityReference types)"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Optional description"
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "Display name (defaults to param-name)"
        };

        var command = new Command("add-parameter", "Add a request parameter or response property to a Custom API")
        {
            apiNameArgument,
            paramNameArgument,
            typeOption,
            directionOption,
            optionalOption,
            entityOption,
            descriptionOption,
            displayNameOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var apiName = parseResult.GetValue(apiNameArgument)!;
            var paramName = parseResult.GetValue(paramNameArgument)!;
            var type = parseResult.GetValue(typeOption)!;
            var direction = parseResult.GetValue(directionOption)!;
            var optional = parseResult.GetValue(optionalOption);
            var entity = parseResult.GetValue(entityOption);
            var description = parseResult.GetValue(descriptionOption);
            var displayName = parseResult.GetValue(displayNameOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(apiName, paramName, type, direction, optional, entity, description, displayName, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string apiName,
        string paramName,
        string type,
        string direction,
        bool optional,
        string? entity,
        string? description,
        string? displayName,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Normalize direction
        var normalizedDirection = direction.ToLowerInvariant() switch
        {
            "input" => "Request",
            "output" => "Response",
            "request" => "Request",
            "response" => "Response",
            _ => null
        };

        if (normalizedDirection == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                $"Invalid direction '{direction}'. Use 'input' or 'output'.",
                Target: direction));
            return ExitCodes.InvalidArguments;
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

            // Resolve the Custom API
            var api = await customApiService.GetAsync(apiName, cancellationToken);
            if (api == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Custom API '{apiName}' not found.",
                    Target: apiName));
                return ExitCodes.NotFoundError;
            }

            if (!globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"Adding {normalizedDirection.ToLowerInvariant()} parameter '{paramName}' to '{api.UniqueName}'");
            }

            var parameter = new CustomApiParameterRegistration(
                UniqueName: paramName,
                DisplayName: displayName ?? paramName,
                Name: null,
                Description: description,
                Type: type,
                LogicalEntityName: entity,
                IsOptional: optional,
                Direction: normalizedDirection);

            var parameterId = await customApiService.AddParameterAsync(api.Id, parameter, cancellationToken);

            var result = new AddParameterResult
            {
                Success = true,
                Operation = "add-parameter",
                ParameterId = parameterId,
                ApiId = api.Id,
                ApiUniqueName = api.UniqueName,
                ParamName = paramName,
                Direction = normalizedDirection,
                Type = type
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Parameter added: {parameterId}");
                Console.Error.WriteLine($"  Name: {paramName}");
                Console.Error.WriteLine($"  Type: {type}");
                Console.Error.WriteLine($"  Direction: {normalizedDirection}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"adding parameter '{paramName}' to Custom API '{apiName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class AddParameterResult
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

        [JsonPropertyName("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;
    }

    #endregion
}
