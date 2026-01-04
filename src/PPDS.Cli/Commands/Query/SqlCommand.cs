using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Sql.Parsing;
using PPDS.Dataverse.Sql.Transpilation;

namespace PPDS.Cli.Commands.Query;

/// <summary>
/// Executes SQL queries against Dataverse by transpiling to FetchXML.
/// </summary>
public static class SqlCommand
{
    /// <summary>
    /// Creates the 'sql' command.
    /// </summary>
    public static Command Create()
    {
        var sqlArgument = new Argument<string?>("sql")
        {
            Description = "SQL query string. Can be omitted if using --file or --stdin.",
            Arity = ArgumentArity.ZeroOrOne
        };

        var fileOption = new Option<FileInfo?>("--file")
        {
            Description = "Read SQL from a file"
        };

        var stdinOption = new Option<bool>("--stdin")
        {
            Description = "Read SQL from standard input"
        };

        var showFetchXmlOption = new Option<bool>("--show-fetchxml")
        {
            Description = "Output the transpiled FetchXML instead of executing the query"
        };

        var command = new Command("sql", "Execute a SQL query against Dataverse (transpiled to FetchXML)")
        {
            sqlArgument,
            fileOption,
            stdinOption,
            showFetchXmlOption,
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
            var sql = result.GetValue(sqlArgument);
            var file = result.GetValue(fileOption);
            var stdin = result.GetValue(stdinOption);

            var sourceCount = (string.IsNullOrEmpty(sql) ? 0 : 1) +
                             (file != null ? 1 : 0) +
                             (stdin ? 1 : 0);

            if (sourceCount == 0)
            {
                result.AddError("A SQL source is required. Provide a query argument, --file, or --stdin.");
            }
            else if (sourceCount > 1)
            {
                result.AddError("Only one SQL source allowed. Use query argument, --file, or --stdin.");
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sql = parseResult.GetValue(sqlArgument);
            var file = parseResult.GetValue(fileOption);
            var stdin = parseResult.GetValue(stdinOption);
            var showFetchXml = parseResult.GetValue(showFetchXmlOption);
            var profile = parseResult.GetValue(QueryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(QueryCommandGroup.EnvironmentOption);
            var top = parseResult.GetValue(QueryCommandGroup.TopOption);
            var page = parseResult.GetValue(QueryCommandGroup.PageOption);
            var pagingCookie = parseResult.GetValue(QueryCommandGroup.PagingCookieOption);
            var count = parseResult.GetValue(QueryCommandGroup.CountOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                sql, file, stdin, showFetchXml,
                profile, environment,
                top, page, pagingCookie, count,
                globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? sql,
        FileInfo? file,
        bool stdin,
        bool showFetchXml,
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
            // Get the SQL from the appropriate source
            var query = await GetSqlAsync(sql, file, stdin, cancellationToken);

            // Parse and transpile SQL to FetchXML
            if (globalOptions.OutputFormat == OutputFormat.Text && !showFetchXml)
            {
                Console.Error.WriteLine("Parsing SQL...");
            }

            var parser = new SqlParser(query);
            var ast = parser.Parse();

            // Override top if specified
            if (top.HasValue)
            {
                ast = ast.WithTop(top.Value);
            }

            var transpiler = new SqlToFetchXmlTranspiler();
            var fetchXml = transpiler.Transpile(ast);

            // If --show-fetchxml, output and exit
            if (showFetchXml)
            {
                if (globalOptions.OutputFormat == OutputFormat.Json)
                {
                    writer.WriteSuccess(new { sql = query, fetchXml });
                }
                else
                {
                    Console.WriteLine(fetchXml);
                }

                return ExitCodes.Success;
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
                Console.Error.WriteLine("Executing query...");
            }

            var result = await queryExecutor.ExecuteFetchXmlAsync(
                fetchXml,
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
                    WriteTableOutput(result, fetchXml, globalOptions.Verbose);
                    break;
            }

            return ExitCodes.Success;
        }
        catch (SqlParseException ex)
        {
            var details = globalOptions.Debug
                ? $"Line {ex.Line}, Column {ex.Column}, Position {ex.Position}\nContext: {ex.ContextSnippet}"
                : null;

            var error = StructuredError.Create(
                "SQL_PARSE_ERROR",
                ex.Message,
                details,
                target: null,
                debug: globalOptions.Debug);

            writer.WriteError(error);
            return ExitCodes.InvalidArguments;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "executing SQL query", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static async Task<string> GetSqlAsync(
        string? sql,
        FileInfo? file,
        bool stdin,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(sql))
        {
            return sql;
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

        throw new InvalidOperationException("No SQL source provided.");
    }

    private static void WriteTableOutput(QueryResult result, string fetchXml, bool verbose)
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

        if (verbose)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Executed FetchXML:");
            Console.Error.WriteLine(fetchXml);
        }

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
