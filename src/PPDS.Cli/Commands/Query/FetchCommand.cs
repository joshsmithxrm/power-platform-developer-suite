using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Query;

namespace PPDS.Cli.Commands.Query;

/// <summary>
/// Executes FetchXML queries against Dataverse.
/// </summary>
public static class FetchCommand
{
    /// <summary>
    /// Creates the 'fetch' command.
    /// </summary>
    public static Command Create()
    {
        var fetchXmlArgument = new Argument<string?>("fetchxml")
        {
            Description = "FetchXML query string. Can be omitted if using --file or --stdin.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var fileOption = new Option<FileInfo?>("--file")
        {
            Description = "Read FetchXML from a file"
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read FetchXML from standard input"
        };

        var command = new Command("fetch", "Execute a FetchXML query against Dataverse")
        {
            fetchXmlArgument,
            fileOption,
            stdinOption,
            QueryCommandGroup.ProfileOption,
            QueryCommandGroup.EnvironmentOption,
            QueryCommandGroup.TopOption,
            QueryCommandGroup.PageOption,
            QueryCommandGroup.PagingCookieOption,
            QueryCommandGroup.CountOption
        };

        GlobalOptions.AddToCommand(command);

        // Validate that exactly one input source is provided
        command.Validators.Add(result =>
        {
            var fetchXml = result.GetValue(fetchXmlArgument);
            var file = result.GetValue(fileOption);
            var stdin = result.GetValue(stdinOption);

            var sourceCount = (string.IsNullOrEmpty(fetchXml) ? 0 : 1) +
                             (file != null ? 1 : 0) +
                             (stdin ? 1 : 0);

            if (sourceCount == 0)
            {
                result.AddError("A FetchXML source is required. Provide a query argument, --file, or --stdin.");
            }
            else if (sourceCount > 1)
            {
                result.AddError("Only one FetchXML source allowed. Use query argument, --file, or --stdin.");
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var fetchXml = parseResult.GetValue(fetchXmlArgument);
            var file = parseResult.GetValue(fileOption);
            var stdin = parseResult.GetValue(stdinOption);
            var profile = parseResult.GetValue(QueryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(QueryCommandGroup.EnvironmentOption);
            var top = parseResult.GetValue(QueryCommandGroup.TopOption);
            var page = parseResult.GetValue(QueryCommandGroup.PageOption);
            var pagingCookie = parseResult.GetValue(QueryCommandGroup.PagingCookieOption);
            var count = parseResult.GetValue(QueryCommandGroup.CountOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                fetchXml, file, stdin,
                profile, environment,
                top, page, pagingCookie, count,
                globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? fetchXml,
        FileInfo? file,
        bool stdin,
        string? profile,
        string? environment,
        int? top,
        int? page,
        string? pagingCookie,
        bool count,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            // Get the FetchXML from the appropriate source
            var query = await GetFetchXmlAsync(fetchXml, file, stdin, cancellationToken);

            // Inject top attribute if specified and not already present
            if (top.HasValue)
            {
                query = InjectTopAttribute(query, top.Value);
            }

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var queryExecutor = serviceProvider.GetRequiredService<IQueryExecutor>();

            if (globalOptions.OutputFormat == OutputFormat.Text)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Executing FetchXML query...");
            }

            var result = await queryExecutor.ExecuteFetchXmlAsync(
                query,
                page,
                pagingCookie,
                count,
                cancellationToken);

            switch (globalOptions.OutputFormat)
            {
                case OutputFormat.Json:
                    writer.WriteSuccess(result);
                    break;
                case OutputFormat.Csv:
                    WriteCsvOutput(result);
                    break;
                default:
                    WriteTableOutput(result);
                    break;
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "executing FetchXML query", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<string> GetFetchXmlAsync(
        string? fetchXml,
        FileInfo? file,
        bool stdin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(fetchXml))
        {
            return fetchXml;
        }

        if (file != null)
        {
            return await File.ReadAllTextAsync(file.FullName, cancellationToken);
        }

        if (stdin)
        {
            using var reader = new StreamReader(Console.OpenStandardInput());
            return await reader.ReadToEndAsync(cancellationToken);
        }

        throw new InvalidOperationException("No FetchXML source provided.");
    }

    private static string InjectTopAttribute(string fetchXml, int top)
    {
        // Simple injection - find the first <fetch and add/update the top attribute
        var fetchIndex = fetchXml.IndexOf("<fetch", StringComparison.OrdinalIgnoreCase);
        if (fetchIndex < 0)
        {
            return fetchXml;
        }

        var endOfFetch = fetchXml.IndexOf('>', fetchIndex);
        if (endOfFetch < 0)
        {
            return fetchXml;
        }

        var fetchElement = fetchXml.Substring(fetchIndex, endOfFetch - fetchIndex);

        // Check if top is already specified
        if (fetchElement.Contains("top=", StringComparison.OrdinalIgnoreCase))
        {
            // Replace existing top value
            var topPattern = "top=\"";
            var topStart = fetchElement.IndexOf(topPattern, StringComparison.OrdinalIgnoreCase);
            if (topStart >= 0)
            {
                var valueStart = topStart + topPattern.Length;
                var valueEnd = fetchElement.IndexOf('"', valueStart);
                if (valueEnd > valueStart)
                {
                    var prefix = fetchXml.Substring(0, fetchIndex + topStart + topPattern.Length);
                    var suffix = fetchXml.Substring(fetchIndex + valueEnd);
                    return prefix + top + suffix;
                }
            }
        }
        else
        {
            // Insert top attribute
            var insertPoint = fetchIndex + "<fetch".Length;
            return fetchXml.Substring(0, insertPoint) + $" top=\"{top}\"" + fetchXml.Substring(insertPoint);
        }

        return fetchXml;
    }

    private static void WriteTableOutput(QueryResult result)
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
            Console.Error.WriteLine("More records available (use --page or --paging-cookie for continuation)");
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
            Console.Error.WriteLine($"Paging cookie (for continuation):");
            Console.Error.WriteLine(result.PagingCookie);
        }
    }

    private static string TruncateValue(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value.PadRight(maxLength);
        }

        return value.Substring(0, maxLength - 3) + "...";
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
