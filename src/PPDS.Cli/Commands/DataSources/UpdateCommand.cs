using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// Update mutable properties of an existing data source.
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Data source logical name or GUID"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "New description for the data source"
        };

        var command = new Command("update", "Update mutable properties of an existing data source")
        {
            nameOrIdArgument,
            descriptionOption,
            DataSourcesCommandGroup.ProfileOption,
            DataSourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var description = parseResult.GetValue(descriptionOption);
            var profile = parseResult.GetValue(DataSourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataSourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(nameOrId, description, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
        string? description,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Check that at least one change was specified
        if (description == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "No changes specified. Use --description.",
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

            var dataProviderService = serviceProvider.GetRequiredService<IDataProviderService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve name-or-id to GUID
            var existing = await dataProviderService.GetDataSourceAsync(nameOrId, cancellationToken);
            if (existing == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Data source '{nameOrId}' not found.",
                    Target: nameOrId));
                return ExitCodes.NotFoundError;
            }

            var request = new DataSourceUpdateRequest(
                Description: description);

            await dataProviderService.UpdateDataSourceAsync(existing.Id, request, cancellationToken);

            // Build list of changes
            var changes = new List<string>();
            if (description != null) changes.Add($"description -> {description}");

            var result = new UpdateResult
            {
                Type = "data-source",
                Name = existing.Name,
                Id = existing.Id,
                Changes = changes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Data source updated: {existing.Name}");
                Console.Error.WriteLine($"  Changes: {string.Join(", ", changes)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating data source", debug: globalOptions.Debug);
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
