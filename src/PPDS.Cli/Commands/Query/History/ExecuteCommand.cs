using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.History;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Commands.Query.History;

/// <summary>
/// Re-execute a query from history.
/// </summary>
public static class ExecuteCommand
{
    /// <summary>
    /// Creates the 'execute' command.
    /// </summary>
    public static Command Create()
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "The history entry ID to execute"
        };

        var topOption = new Option<int?>("--top", "-t")
        {
            Description = "Limit the number of results returned (overrides query TOP)"
        };

        var command = new Command("execute", "Re-execute a query from history")
        {
            idArgument,
            HistoryCommandGroup.ProfileOption,
            HistoryCommandGroup.EnvironmentOption,
            topOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument)!;
            var profile = parseResult.GetValue(HistoryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(HistoryCommandGroup.EnvironmentOption);
            var top = parseResult.GetValue(topOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(id, profile, environment, top, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string id,
        string? profile,
        string? environment,
        int? top,
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

            // Get the history entry
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

            // Execute the query
            if (!globalOptions.IsJsonMode)
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Executing query from history: {id}");
                Console.Error.WriteLine();
            }

            var sqlQueryService = serviceProvider.GetRequiredService<ISqlQueryService>();

            var request = new SqlQueryRequest
            {
                Sql = entry.Sql,
                TopOverride = top
            };

            var queryResult = await sqlQueryService.ExecuteAsync(request, cancellationToken);

            switch (globalOptions.OutputFormat)
            {
                case Commands.OutputFormat.Json:
                    writer.WriteSuccess(queryResult.Result);
                    break;
                case Commands.OutputFormat.Csv:
                    WriteCsvOutput(queryResult.Result);
                    break;
                default:
                    WriteTableOutput(queryResult.Result, globalOptions.Verbose);
                    break;
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "executing query from history", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static void WriteTableOutput(QueryResult result, bool verbose)
    {
        Console.Error.WriteLine();

        if (result.Count == 0)
        {
            Console.Error.WriteLine("No records found.");
            return;
        }

        Console.Error.WriteLine($"Entity: {result.EntityLogicalName}");
        Console.Error.WriteLine($"Records: {result.Count}");

        if (result.TotalCount.HasValue)
        {
            Console.Error.WriteLine($"Total Count: {result.TotalCount}");
        }

        if (result.MoreRecords)
        {
            Console.Error.WriteLine("More records available (use 'ppds query sql' with --page for continuation)");
        }

        Console.Error.WriteLine($"Execution Time: {result.ExecutionTimeMs}ms");
        Console.Error.WriteLine();

        // Print table header
        var columns = result.Columns;
        var columnWidths = new int[columns.Count];

        for (var i = 0; i < columns.Count; i++)
        {
            columnWidths[i] = Math.Max(
                columns[i].Alias?.Length ?? columns[i].LogicalName.Length,
                20);
        }

        // Header row
        var header = string.Join(" | ", columns.Select((c, i) =>
            (c.Alias ?? c.LogicalName).PadRight(columnWidths[i])));
        Console.WriteLine(header);
        Console.WriteLine(new string('-', header.Length));

        // Data rows
        foreach (var record in result.Records)
        {
            var row = new List<string>();
            for (var i = 0; i < columns.Count; i++)
            {
                var columnName = columns[i].Alias ?? columns[i].LogicalName;
                if (record.TryGetValue(columnName, out var queryValue) && queryValue != null)
                {
                    var displayValue = queryValue.FormattedValue ?? queryValue.Value?.ToString() ?? "";
                    row.Add(TruncateValue(displayValue, columnWidths[i]));
                }
                else
                {
                    row.Add("".PadRight(columnWidths[i]));
                }
            }

            Console.WriteLine(string.Join(" | ", row));
        }

        if (result.MoreRecords && !string.IsNullOrEmpty(result.PagingCookie))
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Paging cookie (use with 'ppds query sql --paging-cookie' for continuation):");
            Console.Error.WriteLine(result.PagingCookie);
        }
    }

    private static string TruncateValue(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value.PadRight(maxLength);
        }

        return value[..(maxLength - 3)] + "...";
    }

    private static void WriteCsvOutput(QueryResult result)
    {
        if (result.Count == 0)
        {
            return;
        }

        // Header row
        var headers = result.Columns.Select(c => EscapeCsvField(c.Alias ?? c.LogicalName));
        Console.WriteLine(string.Join(",", headers));

        // Data rows
        foreach (var record in result.Records)
        {
            var values = result.Columns.Select(c =>
            {
                var key = c.Alias ?? c.LogicalName;
                if (record.TryGetValue(key, out var qv) && qv != null)
                {
                    return EscapeCsvField(qv.FormattedValue ?? qv.Value?.ToString() ?? "");
                }
                return "";
            });
            Console.WriteLine(string.Join(",", values));
        }
    }

    private static string EscapeCsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
        return value;
    }
}
