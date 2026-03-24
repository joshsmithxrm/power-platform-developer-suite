using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataProviders;

/// <summary>
/// List registered data providers in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var dataSourceOption = new Option<string?>("--data-source")
        {
            Description = "Filter by data source name or GUID"
        };

        var command = new Command("list", "List registered data providers in the environment")
        {
            dataSourceOption,
            DataProvidersCommandGroup.ProfileOption,
            DataProvidersCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dataSource = parseResult.GetValue(dataSourceOption);
            var profile = parseResult.GetValue(DataProvidersCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataProvidersCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(dataSource, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? dataSourceFilter,
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

            // Resolve data source filter to ID if provided
            Guid? dataSourceId = null;
            if (!string.IsNullOrEmpty(dataSourceFilter))
            {
                if (Guid.TryParse(dataSourceFilter, out var dsId))
                {
                    dataSourceId = dsId;
                }
                else
                {
                    var ds = await dataProviderService.GetDataSourceAsync(dataSourceFilter, cancellationToken);
                    if (ds == null)
                    {
                        writer.WriteError(new StructuredError(
                            ErrorCodes.Operation.NotFound,
                            $"Data source '{dataSourceFilter}' not found.",
                            Target: dataSourceFilter));
                        return ExitCodes.NotFoundError;
                    }
                    dataSourceId = ds.Id;
                }
            }

            var providers = await dataProviderService.ListDataProvidersAsync(dataSourceId, cancellationToken);

            if (providers.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Providers = [] });
                }
                else
                {
                    Console.Error.WriteLine("No data providers found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Providers = providers.Select(p => new DataProviderOutput
                    {
                        Id = p.Id,
                        Name = p.Name,
                        DataSourceId = p.DataSourceId,
                        DataSourceName = p.DataSourceName,
                        RetrievePlugin = p.RetrievePlugin,
                        RetrieveMultiplePlugin = p.RetrieveMultiplePlugin,
                        CreatePlugin = p.CreatePlugin,
                        UpdatePlugin = p.UpdatePlugin,
                        DeletePlugin = p.DeletePlugin,
                        IsManaged = p.IsManaged,
                        CreatedOn = p.CreatedOn,
                        ModifiedOn = p.ModifiedOn
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                foreach (var provider in providers)
                {
                    var managed = provider.IsManaged ? " [managed]" : "";
                    Console.Error.WriteLine($"Data Provider: {provider.Name}{managed}");
                    if (!string.IsNullOrEmpty(provider.DataSourceName))
                        Console.Error.WriteLine($"  Data Source: {provider.DataSourceName}");
                    if (provider.RetrievePlugin.HasValue)
                        Console.Error.WriteLine($"  Retrieve: {provider.RetrievePlugin}");
                    if (provider.RetrieveMultiplePlugin.HasValue)
                        Console.Error.WriteLine($"  RetrieveMultiple: {provider.RetrieveMultiplePlugin}");
                    if (provider.CreatePlugin.HasValue)
                        Console.Error.WriteLine($"  Create: {provider.CreatePlugin}");
                    if (provider.UpdatePlugin.HasValue)
                        Console.Error.WriteLine($"  Update: {provider.UpdatePlugin}");
                    if (provider.DeletePlugin.HasValue)
                        Console.Error.WriteLine($"  Delete: {provider.DeletePlugin}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {providers.Count} data provider(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing data providers", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("providers")]
        public List<DataProviderOutput> Providers { get; set; } = [];
    }

    private sealed class DataProviderOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("dataSourceId")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? DataSourceId { get; set; }

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

        [JsonPropertyName("createdOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTime? ModifiedOn { get; set; }
    }

    #endregion
}
