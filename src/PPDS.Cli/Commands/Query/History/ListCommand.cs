using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.History;

namespace PPDS.Cli.Commands.Query.History;

/// <summary>
/// List query history entries with optional filtering.
/// </summary>
public static class ListCommand
{
    /// <summary>
    /// Creates the 'list' command.
    /// </summary>
    public static Command Create()
    {
        var limitOption = new Option<int>("--limit", "-l")
        {
            Description = "Maximum number of entries to return",
            DefaultValueFactory = _ => 20
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter queries by substring match (case-insensitive)"
        };

        var command = new Command("list", "List recent query history entries")
        {
            HistoryCommandGroup.ProfileOption,
            HistoryCommandGroup.EnvironmentOption,
            limitOption,
            filterOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var profile = parseResult.GetValue(HistoryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(HistoryCommandGroup.EnvironmentOption);
            var limit = parseResult.GetValue(limitOption);
            var filter = parseResult.GetValue(filterOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(profile, environment, limit, filter, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        int limit,
        string? filter,
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

            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var entries = string.IsNullOrWhiteSpace(filter)
                ? await historyService.GetHistoryAsync(environmentUrl, limit, cancellationToken)
                : await historyService.SearchHistoryAsync(environmentUrl, filter, limit, cancellationToken);

            if (entries.Count == 0)
            {
                if (globalOptions.IsJsonMode)
                {
                    writer.WriteSuccess(new ListOutput { Entries = [] });
                }
                else
                {
                    Console.Error.WriteLine("No query history found.");
                }
                return ExitCodes.Success;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new ListOutput
                {
                    Entries = entries.Select(e => new HistoryEntryOutput
                    {
                        Id = e.Id,
                        Sql = e.Sql,
                        ExecutedAt = e.ExecutedAt,
                        RowCount = e.RowCount,
                        ExecutionTimeMs = e.ExecutionTimeMs,
                        Success = e.Success,
                        ErrorMessage = e.ErrorMessage
                    }).ToList()
                };
                writer.WriteSuccess(output);
            }
            else
            {
                WriteTextOutput(entries);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing query history", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WriteTextOutput(IReadOnlyList<QueryHistoryEntry> entries)
    {
        Console.Error.WriteLine($"{"ID",-14} {"Executed",-18} {"Rows",-8} {"Time",-10} {"Query Preview",-40}");
        Console.Error.WriteLine(new string('-', 95));

        foreach (var entry in entries)
        {
            var id = entry.Id;
            var executed = entry.ExecutedAt.LocalDateTime.ToString("MM/dd/yy HH:mm:ss");
            var rows = entry.RowCount?.ToString() ?? "-";
            var time = entry.ExecutionTimeMs.HasValue ? $"{entry.ExecutionTimeMs}ms" : "-";
            var preview = GetQueryPreview(entry.Sql, 40);

            Console.Error.WriteLine($"{id,-14} {executed,-18} {rows,-8} {time,-10} {preview,-40}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine($"Total: {entries.Count} entries");
    }

    private static string GetQueryPreview(string sql, int maxLength)
    {
        // Efficiently collapse all whitespace to single spaces using regex
        var normalized = System.Text.RegularExpressions.Regex.Replace(sql, @"\s+", " ").Trim();

        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..(maxLength - 3)] + "...";
    }

    #region Output Models

    private sealed class ListOutput
    {
        [JsonPropertyName("entries")]
        public List<HistoryEntryOutput> Entries { get; set; } = [];
    }

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
