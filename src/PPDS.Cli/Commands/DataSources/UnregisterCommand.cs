using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// Unregister a data source from Dataverse, cascade-deleting all child data providers.
/// </summary>
public static class UnregisterCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Data source logical name or GUID"
        };

        var forceOption = new Option<bool>("--force")
        {
            Description = "Cascade-delete all child data providers. Required if any providers exist."
        };

        var command = new Command("unregister", "Unregister a data source and cascade-delete all child data providers")
        {
            nameOrIdArgument,
            forceOption,
            DataSourcesCommandGroup.ProfileOption,
            DataSourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var force = parseResult.GetValue(forceOption);
            var profile = parseResult.GetValue(DataSourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataSourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(nameOrId, force, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
        bool force,
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

            await dataProviderService.UnregisterDataSourceAsync(existing.Id, force: force, cancellationToken: cancellationToken);

            var result = new UnregisterResult
            {
                Success = true,
                Operation = "unregister-data-source",
                Id = existing.Id,
                Name = existing.Name
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"Unregistered data source: {existing.Name}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "unregistering data source", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Result Models

    private sealed class UnregisterResult
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("operation")]
        public string Operation { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    #endregion
}
