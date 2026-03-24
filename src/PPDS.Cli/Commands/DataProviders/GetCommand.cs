using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataProviders;

/// <summary>
/// Get details for a specific data provider.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Data provider name or GUID"
        };

        var command = new Command("get", "Get details for a specific data provider")
        {
            nameOrIdArgument,
            DataProvidersCommandGroup.ProfileOption,
            DataProvidersCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var profile = parseResult.GetValue(DataProvidersCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataProvidersCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(nameOrId, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string nameOrId,
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

            var provider = await dataProviderService.GetDataProviderAsync(nameOrId, cancellationToken);

            if (provider == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Data provider '{nameOrId}' not found.",
                    null,
                    nameOrId);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new DataProviderDetailOutput
                {
                    Id = provider.Id,
                    Name = provider.Name,
                    DataSourceName = provider.DataSourceName,
                    RetrievePlugin = provider.RetrievePlugin,
                    RetrieveMultiplePlugin = provider.RetrieveMultiplePlugin,
                    CreatePlugin = provider.CreatePlugin,
                    UpdatePlugin = provider.UpdatePlugin,
                    DeletePlugin = provider.DeletePlugin,
                    IsManaged = provider.IsManaged
                };
                writer.WriteSuccess(output);
            }
            else
            {
                var properties = new Dictionary<string, string?>
                {
                    ["Name"] = provider.Name,
                    ["ID"] = provider.Id.ToString(),
                    ["Data Source"] = provider.DataSourceName ?? "-",
                    ["Is Managed"] = provider.IsManaged ? "Yes" : "No",
                    ["Retrieve"] = provider.RetrievePlugin?.ToString() ?? "-",
                    ["RetrieveMultiple"] = provider.RetrieveMultiplePlugin?.ToString() ?? "-",
                    ["Create"] = provider.CreatePlugin?.ToString() ?? "-",
                    ["Update"] = provider.UpdatePlugin?.ToString() ?? "-",
                    ["Delete"] = provider.DeletePlugin?.ToString() ?? "-"
                };

                WritePropertyTable(properties);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting data provider '{nameOrId}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WritePropertyTable(Dictionary<string, string?> properties)
    {
        if (properties.Count == 0) return;

        var maxKeyLength = properties.Keys.Max(k => k.Length);

        foreach (var kvp in properties)
        {
            var key = kvp.Key.PadRight(maxKeyLength);
            Console.Error.WriteLine($"  {key}  {kvp.Value}");
        }
    }

    #region Output Models

    private sealed class DataProviderDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dataSourceName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DataSourceName { get; set; }

        [JsonPropertyName("retrievePlugin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? RetrievePlugin { get; set; }

        [JsonPropertyName("retrieveMultiplePlugin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? RetrieveMultiplePlugin { get; set; }

        [JsonPropertyName("createPlugin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? CreatePlugin { get; set; }

        [JsonPropertyName("updatePlugin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? UpdatePlugin { get; set; }

        [JsonPropertyName("deletePlugin")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? DeletePlugin { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }
    }

    #endregion
}
