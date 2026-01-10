using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.History;

namespace PPDS.Cli.Commands.Query.History;

/// <summary>
/// Delete a specific query history entry.
/// </summary>
public static class DeleteCommand
{
    /// <summary>
    /// Creates the 'delete' command.
    /// </summary>
    public static Command Create()
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "The history entry ID to delete"
        };

        var command = new Command("delete", "Delete a query history entry")
        {
            idArgument,
            HistoryCommandGroup.ProfileOption,
            HistoryCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument)!;
            var profile = parseResult.GetValue(HistoryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(HistoryCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(id, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string id,
        string? profile,
        string? environment,
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

            var deleted = await historyService.DeleteEntryAsync(environmentUrl, id, cancellationToken);

            if (!deleted)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"History entry '{id}' not found.",
                    null,
                    "id");

                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new DeleteOutput
                {
                    Id = id,
                    Deleted = true
                });
            }
            else
            {
                Console.Error.WriteLine($"Deleted history entry: {id}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "deleting query history entry", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class DeleteOutput
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("deleted")]
        public bool Deleted { get; set; }
    }

    #endregion
}
