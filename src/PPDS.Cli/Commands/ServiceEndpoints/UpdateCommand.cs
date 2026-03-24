using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.ServiceEndpoints;

/// <summary>
/// Update an existing service endpoint in Dataverse.
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Service endpoint name or GUID"
        };

        var nameOption = new Option<string?>("--name")
        {
            Description = "New display name for the endpoint"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "New description for the endpoint"
        };

        var urlOption = new Option<string?>("--url")
        {
            Description = "New webhook URL (webhooks only)"
        };

        var authTypeOption = new Option<string?>("--auth-type")
        {
            Description = "New authentication type"
        };

        var authValueOption = new Option<string?>("--auth-value")
        {
            Description = "New auth secret value"
        };

        var command = new Command("update", "Update an existing service endpoint")
        {
            nameOrIdArgument,
            nameOption,
            descriptionOption,
            urlOption,
            authTypeOption,
            authValueOption,
            ServiceEndpointsCommandGroup.ProfileOption,
            ServiceEndpointsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var name = parseResult.GetValue(nameOption);
            var description = parseResult.GetValue(descriptionOption);
            var url = parseResult.GetValue(urlOption);
            var authType = parseResult.GetValue(authTypeOption);
            var authValue = parseResult.GetValue(authValueOption);
            var profile = parseResult.GetValue(ServiceEndpointsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(ServiceEndpointsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(nameOrId, name, description, url, authType, authValue, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
        string? name,
        string? description,
        string? url,
        string? authType,
        string? authValue,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Check that at least one change was specified
        if (name == null && description == null && url == null && authType == null && authValue == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "No changes specified. Use --name, --description, --url, --auth-type, or --auth-value.",
                Target: nameOrId));
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

            var endpointService = serviceProvider.GetRequiredService<IServiceEndpointService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve name-or-id to GUID
            Guid endpointId;
            string endpointName;

            if (Guid.TryParse(nameOrId, out var id))
            {
                var existing = await endpointService.GetByIdAsync(id, cancellationToken);
                if (existing == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Service endpoint '{nameOrId}' not found.",
                        Target: nameOrId));
                    return ExitCodes.NotFoundError;
                }
                endpointId = existing.Id;
                endpointName = existing.Name;
            }
            else
            {
                var existing = await endpointService.GetByNameAsync(nameOrId, cancellationToken);
                if (existing == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Service endpoint '{nameOrId}' not found.",
                        Target: nameOrId));
                    return ExitCodes.NotFoundError;
                }
                endpointId = existing.Id;
                endpointName = existing.Name;
            }

            var request = new ServiceEndpointUpdateRequest(
                Name: name,
                Description: description,
                Url: url,
                AuthType: authType,
                AuthValue: authValue);

            await endpointService.UpdateAsync(endpointId, request, cancellationToken);

            // Build list of changes
            var changes = new List<string>();
            if (name != null) changes.Add($"name -> {name}");
            if (description != null) changes.Add($"description -> {description}");
            if (url != null) changes.Add($"url -> {url}");
            if (authType != null) changes.Add($"authType -> {authType}");
            if (authValue != null) changes.Add("authValue -> [updated]");

            var result = new UpdateResult
            {
                Type = "service-endpoint",
                Name = name ?? endpointName,
                Id = endpointId,
                Changes = changes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Service endpoint updated: {endpointName}");
                Console.Error.WriteLine($"  Changes: {string.Join(", ", changes)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating service endpoint", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class UpdateResult
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("changes")]
        public List<string> Changes { get; init; } = [];
    }

    #endregion
}
