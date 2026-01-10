using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.History;

namespace PPDS.Cli.Commands.Query.History;

/// <summary>
/// Get a specific query history entry by ID.
/// </summary>
public static class GetCommand
{
    /// <summary>
    /// Creates the 'get' command.
    /// </summary>
    public static Command Create()
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "The history entry ID to retrieve"
        };

        var command = new Command("get", "Get a specific query history entry")
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

            var entry = await historyService.GetEntryByIdAsync(environmentUrl, id, cancellationToken);

            if (entry == null)
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
                var output = new HistoryEntryOutput
                {
                    Id = entry.Id,
                    Sql = entry.Sql,
                    ExecutedAt = entry.ExecutedAt,
                    RowCount = entry.RowCount,
                    ExecutionTimeMs = entry.ExecutionTimeMs,
                    Success = entry.Success,
                    ErrorMessage = entry.ErrorMessage
                };
                writer.WriteSuccess(output);
            }
            else
            {
                WriteTextOutput(entry);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "getting query history entry", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WriteTextOutput(QueryHistoryEntry entry)
    {
        Console.Error.WriteLine($"ID:           {entry.Id}");
        Console.Error.WriteLine($"Executed At:  {entry.ExecutedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        Console.Error.WriteLine($"Row Count:    {entry.RowCount?.ToString() ?? "-"}");
        Console.Error.WriteLine($"Duration:     {(entry.ExecutionTimeMs.HasValue ? $"{entry.ExecutionTimeMs}ms" : "-")}");
        Console.Error.WriteLine($"Success:      {entry.Success}");

        if (!string.IsNullOrEmpty(entry.ErrorMessage))
        {
            Console.Error.WriteLine($"Error:        {entry.ErrorMessage}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("SQL:");
        Console.Error.WriteLine(new string('-', 40));
        Console.WriteLine(entry.Sql);
    }

    #region Output Models

    private sealed class HistoryEntryOutput
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = "";

        [JsonPropertyName("sql")]
        public string Sql { get; set; } = "";

        [JsonPropertyName("executedAt")]
        public DateTimeOffset ExecutedAt { get; set; }

        [JsonPropertyName("rowCount")]
        public int? RowCount { get; set; }

        [JsonPropertyName("executionTimeMs")]
        public long? ExecutionTimeMs { get; set; }

        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("errorMessage")]
        public string? ErrorMessage { get; set; }
    }

    #endregion
}
