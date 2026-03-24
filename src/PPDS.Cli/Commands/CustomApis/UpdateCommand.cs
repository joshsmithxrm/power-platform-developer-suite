using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.CustomApis;

/// <summary>
/// Update an existing Custom API in Dataverse.
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var uniqueNameOrIdArgument = new Argument<string>("unique-name-or-id")
        {
            Description = "Custom API unique name or GUID"
        };

        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "New display name"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "New description"
        };

        var isFunctionOption = new Option<bool?>("--is-function")
        {
            Description = "Mark as function (returns a value)"
        };

        var isPrivateOption = new Option<bool?>("--is-private")
        {
            Description = "Mark as private"
        };

        var command = new Command("update", "Update an existing Custom API")
        {
            uniqueNameOrIdArgument,
            displayNameOption,
            descriptionOption,
            isFunctionOption,
            isPrivateOption,
            CustomApisCommandGroup.ProfileOption,
            CustomApisCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var uniqueNameOrId = parseResult.GetValue(uniqueNameOrIdArgument)!;
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var isFunction = parseResult.GetValue(isFunctionOption);
            var isPrivate = parseResult.GetValue(isPrivateOption);
            var profile = parseResult.GetValue(CustomApisCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(CustomApisCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(uniqueNameOrId, displayName, description, isFunction, isPrivate, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string uniqueNameOrId,
        string? displayName,
        string? description,
        bool? isFunction,
        bool? isPrivate,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Check that at least one change was specified
        if (displayName == null && description == null && isFunction == null && isPrivate == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "No changes specified. Use --display-name, --description, --is-function, or --is-private.",
                Target: uniqueNameOrId));
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

            // Resolve to the Custom API record
            var api = await customApiService.GetAsync(uniqueNameOrId, cancellationToken);
            if (api == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Custom API '{uniqueNameOrId}' not found.",
                    Target: uniqueNameOrId));
                return ExitCodes.NotFoundError;
            }

            var request = new CustomApiUpdateRequest(
                DisplayName: displayName,
                Description: description,
                IsFunction: isFunction,
                IsPrivate: isPrivate);

            await customApiService.UpdateAsync(api.Id, request, cancellationToken);

            // Build list of changes
            var changes = new List<string>();
            if (displayName != null) changes.Add($"displayName -> {displayName}");
            if (description != null) changes.Add($"description -> {description}");
            if (isFunction != null) changes.Add($"isFunction -> {isFunction}");
            if (isPrivate != null) changes.Add($"isPrivate -> {isPrivate}");

            var result = new UpdateResult
            {
                Type = "custom-api",
                UniqueName = api.UniqueName,
                Id = api.Id,
                Changes = changes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Custom API updated: {api.UniqueName}");
                Console.Error.WriteLine($"  Changes: {string.Join(", ", changes)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"updating Custom API '{uniqueNameOrId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class UpdateResult
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("uniqueName")]
        public string UniqueName { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; init; }

        [JsonPropertyName("changes")]
        public List<string> Changes { get; init; } = [];
    }

    #endregion
}
