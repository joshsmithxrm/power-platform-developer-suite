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
/// Register a new data provider with plugin operation bindings.
/// </summary>
public static class RegisterCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Display name for the data provider"
        };

        var dataSourceOption = new Option<string>("--data-source")
        {
            Description = "Data source name or GUID",
            Required = true
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

        var command = new Command("register", "Register a new data provider with plugin operation bindings")
        {
            nameArgument,
            dataSourceOption,
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
            var name = parseResult.GetValue(nameArgument)!;
            var dataSource = parseResult.GetValue(dataSourceOption)!;
            var retrieve = parseResult.GetValue(retrieveOption);
            var retrieveMultiple = parseResult.GetValue(retrieveMultipleOption);
            var create = parseResult.GetValue(createOption);
            var update = parseResult.GetValue(updateOption);
            var delete = parseResult.GetValue(deleteOption);
            var profile = parseResult.GetValue(DataProvidersCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataProvidersCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                name, dataSource, retrieve, retrieveMultiple, create, update, delete,
                profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string dataSource,
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
                Console.Error.WriteLine($"Registering data provider: {name}");
            }

            // Resolve data source to ID
            Guid dataSourceId;
            if (Guid.TryParse(dataSource, out var dsId))
            {
                dataSourceId = dsId;
            }
            else
            {
                var ds = await dataProviderService.GetDataSourceAsync(dataSource, cancellationToken);
                if (ds == null)
                {
                    writer.WriteError(new StructuredError(
                        ErrorCodes.Operation.NotFound,
                        $"Data source '{dataSource}' not found.",
                        Target: dataSource));
                    return ExitCodes.NotFoundError;
                }
                dataSourceId = ds.Id;
            }

            // Resolve plugin type names to IDs
            var retrieveId = await ResolvePluginTypeAsync(retrieveTypeName, registrationService, writer, globalOptions, cancellationToken);
            if (retrieveTypeName != null && retrieveId == null) return ExitCodes.NotFoundError;

            var retrieveMultipleId = await ResolvePluginTypeAsync(retrieveMultipleTypeName, registrationService, writer, globalOptions, cancellationToken);
            if (retrieveMultipleTypeName != null && retrieveMultipleId == null) return ExitCodes.NotFoundError;

            var createId = await ResolvePluginTypeAsync(createTypeName, registrationService, writer, globalOptions, cancellationToken);
            if (createTypeName != null && createId == null) return ExitCodes.NotFoundError;

            var updateId = await ResolvePluginTypeAsync(updateTypeName, registrationService, writer, globalOptions, cancellationToken);
            if (updateTypeName != null && updateId == null) return ExitCodes.NotFoundError;

            var deleteId = await ResolvePluginTypeAsync(deleteTypeName, registrationService, writer, globalOptions, cancellationToken);
            if (deleteTypeName != null && deleteId == null) return ExitCodes.NotFoundError;

            var registration = new DataProviderRegistration(
                Name: name,
                DataSourceId: dataSourceId,
                RetrievePlugin: retrieveId,
                RetrieveMultiplePlugin: retrieveMultipleId,
                CreatePlugin: createId,
                UpdatePlugin: updateId,
                DeletePlugin: deleteId);

            var providerId = await dataProviderService.RegisterDataProviderAsync(registration, cancellationToken);

            var result = new RegisterResult
            {
                Success = true,
                Operation = "register-data-provider",
                Id = providerId,
                Name = name,
                DataSourceId = dataSourceId
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Data provider registered: {providerId}");
                Console.Error.WriteLine($"  Name: {name}");
                Console.Error.WriteLine($"  Data Source: {dataSource}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"registering data provider '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    /// <summary>
    /// Resolves a plugin type name or GUID string to a Guid. Returns null if the name is null,
    /// or writes an error and returns null if the type cannot be found.
    /// </summary>
    private static async Task<Guid?> ResolvePluginTypeAsync(
        string? nameOrId,
        IPluginRegistrationService registrationService,
        IOutputWriter writer,
        GlobalOptionValues globalOptions,
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

    private sealed class RegisterResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dataSourceId")]
        public Guid DataSourceId { get; set; }
    }

    #endregion
}
