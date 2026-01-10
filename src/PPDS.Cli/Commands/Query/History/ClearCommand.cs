using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.History;

namespace PPDS.Cli.Commands.Query.History;

/// <summary>
/// Clear all query history for an environment.
/// </summary>
public static class ClearCommand
{
    /// <summary>
    /// Creates the 'clear' command.
    /// </summary>
    public static Command Create()
    {
        var forceOption = new Option<bool>("--force", "-f")
        {
            Description = "Skip confirmation prompt"
        };

        var command = new Command("clear", "Clear all query history for the current environment")
        {
            HistoryCommandGroup.ProfileOption,
            HistoryCommandGroup.EnvironmentOption,
            forceOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(HistoryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(HistoryCommandGroup.EnvironmentOption);
            var force = parseResult.GetValue(forceOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, force, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        bool force,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var historyService = serviceProvider.GetRequiredService<IQueryHistoryService>();
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
            var environmentUrl = connectionInfo.EnvironmentUrl;
            var envDisplayName = connectionInfo.EnvironmentDisplayName ?? environmentUrl;

            // Confirm unless --force is specified
            if (!force && !globalOptions.IsJsonMode)
            {
                Console.Error.WriteLine($"This will clear all query history for environment: {envDisplayName}");
                Console.Error.Write("Are you sure? [y/N] ");

                var response = Console.ReadLine();
                if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Cancelled.");
                    return ExitCodes.Success;
                }
            }

            await historyService.ClearHistoryAsync(environmentUrl, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new ClearOutput
                {
                    Environment = envDisplayName,
                    Cleared = true
                });
            }
            else
            {
                Console.Error.WriteLine($"Cleared query history for: {envDisplayName}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "clearing query history", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ClearOutput
    {
        [JsonPropertyName("environment")]
        public string Environment { get; set; } = "";

        [JsonPropertyName("cleared")]
        public bool Cleared { get; set; }
    }

    #endregion
}
