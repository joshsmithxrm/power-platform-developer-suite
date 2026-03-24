using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;

namespace PPDS.Cli.Commands.DataSources;

/// <summary>
/// Register a new data source entity in Dataverse.
/// </summary>
public static class RegisterCommand
{
    public static Command Create()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Logical name in the format {prefix}_{name}"
        };

        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Optional description"
        };

        var command = new Command("register", "Register a new data source entity in Dataverse")
        {
            nameArgument,
            descriptionOption,
            DataSourcesCommandGroup.ProfileOption,
            DataSourcesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var name = parseResult.GetValue(nameArgument)!;
            var description = parseResult.GetValue(descriptionOption);
            var profile = parseResult.GetValue(DataSourcesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataSourcesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(name, description, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string name,
        string? description,
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
                Console.Error.WriteLine($"Registering data source: {name}");
            }

            var registration = new DataSourceRegistration(
                Name: name,
                Description: description);

            var dataSourceId = await dataProviderService.RegisterDataSourceAsync(registration, cancellationToken);

            var result = new RegisterResult
            {
                Success = true,
                Operation = "register-data-source",
                Id = dataSourceId,
                Name = name
            };

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(result);
            }
            else
            {
                Console.Error.WriteLine($"  Data source registered: {dataSourceId}");
                Console.Error.WriteLine($"  Logical name: {name}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"registering data source '{name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
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
    }

    #endregion
}
