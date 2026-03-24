using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// List registered data sources in a Dataverse environment.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "List registered data sources in the environment")
        {
            DataSourcesCommandGroup.ProfileOption,
            DataSourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(DataSourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataSourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
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

            var dataSources = await dataProviderService.ListDataSourcesAsync(cancellationToken);

            if (dataSources.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { DataSources = [] });
                }
                else
                {
                    Console.Error.WriteLine("No data sources found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    DataSources = dataSources.Select(ds => new DataSourceOutput
                    {
                        Id = ds.Id,
                        Name = ds.Name
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                foreach (var ds in dataSources)
                {
                    Console.Error.WriteLine($"Data Source: {ds.Name}");
                }

                Console.Error.WriteLine();
                Console.Error.WriteLine($"Total: {dataSources.Count} data source(s)");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing data sources", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("dataSources")]
        public List<DataSourceOutput> DataSources { get; set; } = [];
    }

    private sealed class DataSourceOutput
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;
    }

    #endregion
}
