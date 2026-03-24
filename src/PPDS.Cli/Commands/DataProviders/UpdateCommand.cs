using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataProviders;

/// <summary>
/// Update plugin bindings on an existing data provider.
/// </summary>
public static class UpdateCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Data provider name or GUID"
        };

        var retrieveOption = new Option<string?>("--retrieve")
        {
            Description = "Plugin type name for Retrieve operations"
        };

        var retrieveMultipleOption = new Option<string?>("--retrieve-multiple")
        {
            Description = "Plugin type name for RetrieveMultiple operations"
        };

        var createOption = new Option<string?>("--create")
        {
            Description = "Plugin type name for Create operations"
        };

        var updateOption = new Option<string?>("--update")
        {
            Description = "Plugin type name for Update operations"
        };

        var deleteOption = new Option<string?>("--delete")
        {
            Description = "Plugin type name for Delete operations"
        };

        var command = new Command("update", "Update plugin bindings on an existing data provider")
        {
            nameOrIdArgument,
            retrieveOption,
            retrieveMultipleOption,
            createOption,
            updateOption,
            deleteOption,
            DataProvidersCommandGroup.ProfileOption,
            DataProvidersCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var retrieve = parseResult.GetValue(retrieveOption);
            var retrieveMultiple = parseResult.GetValue(retrieveMultipleOption);
            var create = parseResult.GetValue(createOption);
            var update = parseResult.GetValue(updateOption);
            var delete = parseResult.GetValue(deleteOption);
            var profile = parseResult.GetValue(DataProvidersCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataProvidersCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                nameOrId, retrieve, retrieveMultiple, create, update, delete,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
        string? retrieveTypeName,
        string? retrieveMultipleTypeName,
        string? createTypeName,
        string? updateTypeName,
        string? deleteTypeName,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        // Check that at least one change was specified
        if (retrieveTypeName == null && retrieveMultipleTypeName == null && createTypeName == null
            && updateTypeName == null && deleteTypeName == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidArguments,
                "No changes specified. Use --retrieve, --retrieve-multiple, --create, --update, or --delete.",
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
            var registrationService = serviceProvider.GetRequiredService<IPluginRegistrationService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            // Resolve name-or-id to GUID
            Guid providerId;
            string providerName;

            var existing = await dataProviderService.GetDataProviderAsync(nameOrId, cancellationToken);
            if (existing == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Data provider '{nameOrId}' not found.",
                    Target: nameOrId));
                return ExitCodes.NotFoundError;
            }
            providerId = existing.Id;
            providerName = existing.Name;

            // Resolve plugin type names to IDs
            var retrieveId = await ResolvePluginTypeAsync(retrieveTypeName, registrationService, writer, cancellationToken);
            if (retrieveTypeName != null && retrieveId == null) return ExitCodes.NotFoundError;

            var retrieveMultipleId = await ResolvePluginTypeAsync(retrieveMultipleTypeName, registrationService, writer, cancellationToken);
            if (retrieveMultipleTypeName != null && retrieveMultipleId == null) return ExitCodes.NotFoundError;

            var createId = await ResolvePluginTypeAsync(createTypeName, registrationService, writer, cancellationToken);
            if (createTypeName != null && createId == null) return ExitCodes.NotFoundError;

            var updateId = await ResolvePluginTypeAsync(updateTypeName, registrationService, writer, cancellationToken);
            if (updateTypeName != null && updateId == null) return ExitCodes.NotFoundError;

            var deleteId = await ResolvePluginTypeAsync(deleteTypeName, registrationService, writer, cancellationToken);
            if (deleteTypeName != null && deleteId == null) return ExitCodes.NotFoundError;

            var request = new DataProviderUpdateRequest(
                RetrievePlugin: retrieveId,
                RetrieveMultiplePlugin: retrieveMultipleId,
                CreatePlugin: createId,
                UpdatePlugin: updateId,
                DeletePlugin: deleteId);

            await dataProviderService.UpdateDataProviderAsync(providerId, request, cancellationToken);

            // Build list of changes
            var changes = new List<string>();
            if (retrieveTypeName != null) changes.Add($"retrieve -> {retrieveTypeName}");
            if (retrieveMultipleTypeName != null) changes.Add($"retrieveMultiple -> {retrieveMultipleTypeName}");
            if (createTypeName != null) changes.Add($"create -> {createTypeName}");
            if (updateTypeName != null) changes.Add($"update -> {updateTypeName}");
            if (deleteTypeName != null) changes.Add($"delete -> {deleteTypeName}");

            var result = new UpdateResult
            {
                Type = "data-provider",
                Name = providerName,
                Id = providerId,
                Changes = changes
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Data provider updated: {providerName}");
                Console.Error.WriteLine($"  Changes: {string.Join(", ", changes)}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "updating data provider", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<Guid?> ResolvePluginTypeAsync(
        string? nameOrId,
        IPluginRegistrationService registrationService,
        IOutputWriter writer,
        CancellationToken cancellationToken)
    {
        if (nameOrId == null) return null;

        if (Guid.TryParse(nameOrId, out var id)) return id;

        var type = await registrationService.GetPluginTypeByNameOrIdAsync(nameOrId, cancellationToken);
        if (type == null)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Operation.NotFound,
                $"Plugin type '{nameOrId}' not found.",
                Target: nameOrId));
            return null;
        }

        return type.Id;
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
