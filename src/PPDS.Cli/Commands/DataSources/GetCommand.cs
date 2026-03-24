using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// Get details for a specific data source.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var nameOrIdArgument = new Argument<string>("name-or-id")
        {
            Description = "Data source logical name or GUID"
        };

        var command = new Command("get", "Get details for a specific data source")
        {
            nameOrIdArgument,
            DataSourcesCommandGroup.ProfileOption,
            DataSourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var nameOrId = parseResult.GetValue(nameOrIdArgument)!;
            var profile = parseResult.GetValue(DataSourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataSourcesCommandGroup.EnvironmentOption);
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

            var dataSource = await dataProviderService.GetDataSourceAsync(nameOrId, cancellationToken);

            if (dataSource == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Data source '{nameOrId}' not found.",
                    null,
                    nameOrId);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new DataSourceDetailOutput
                {
                    Id = dataSource.Id,
                    Name = dataSource.Name,
                    DisplayName = dataSource.DisplayName,
                    Description = dataSource.Description,
                    IsManaged = dataSource.IsManaged,
                    CreatedOn = dataSource.CreatedOn,
                    ModifiedOn = dataSource.ModifiedOn
                };
                writer.WriteSuccess(output);
            }
            else
            {
                var properties = new Dictionary<string, string?>
                {
                    ["Name"] = dataSource.Name,
                    ["ID"] = dataSource.Id.ToString(),
                    ["Display Name"] = dataSource.DisplayName ?? "-",
                    ["Description"] = dataSource.Description ?? "-",
                    ["Is Managed"] = dataSource.IsManaged ? "Yes" : "No",
                    ["Created"] = dataSource.CreatedOn?.ToString("g") ?? "-",
                    ["Modified"] = dataSource.ModifiedOn?.ToString("g") ?? "-"
                };

                WritePropertyTable(properties);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting data source '{nameOrId}'", debug: globalOptions.Debug);
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

    private sealed class DataSourceDetailOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("createdOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
