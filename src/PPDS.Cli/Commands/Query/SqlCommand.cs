using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Parsing;

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

        var explainOption = new Option<bool>("--explain")
        {
            Description = "Show the execution plan without executing the query"
        };

        var tdsOption = new Option<bool>("--tds")
        {
            Description = "Route query through the TDS Endpoint (direct SQL) instead of FetchXML"
        };

        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Confirm DML execution without interactive prompt"
        };

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Show the execution plan without running the DML statement"
        };

        var noLimitOption = new Option<bool>("--no-limit")
        {
            Description = "Remove the 10,000 row safety cap for DML operations"
        };

        var command = new Command("sql", "Execute a SQL query against Dataverse (transpiled to FetchXML)")
        {
            sqlArgument,
            fileOption,
            stdinOption,
            showFetchXmlOption,
            explainOption,
            tdsOption,
            confirmOption,
            dryRunOption,
            noLimitOption,
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
            var explain = parseResult.GetValue(explainOption);
            var useTds = parseResult.GetValue(tdsOption);
            var confirm = parseResult.GetValue(confirmOption);
            var dryRun = parseResult.GetValue(dryRunOption);
            var noLimit = parseResult.GetValue(noLimitOption);
            var profile = parseResult.GetValue(QueryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(QueryCommandGroup.EnvironmentOption);
            var top = parseResult.GetValue(QueryCommandGroup.TopOption);
            var page = parseResult.GetValue(QueryCommandGroup.PageOption);
            var pagingCookie = parseResult.GetValue(QueryCommandGroup.PagingCookieOption);
            var count = parseResult.GetValue(QueryCommandGroup.CountOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                sql, file, stdin, showFetchXml, explain, useTds,
                confirm, dryRun, noLimit,
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
        bool explain,
        bool useTds,
        bool confirm,
        bool dryRun,
        bool noLimit,
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

            // Create service provider to get ISqlQueryService
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfileAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken: cancellationToken);

            var sqlQueryService = serviceProvider.GetRequiredService<ISqlQueryService>();

            // If --explain, show execution plan without executing
            if (explain)
            {
                if (globalOptions.OutputFormat == OutputFormat.Text)
                {
                    Console.Error.WriteLine("Building execution plan...");
                }

                var plan = await sqlQueryService.ExplainAsync(query, cancellationToken);
                var formatted = PlanFormatter.Format(plan);

                if (globalOptions.OutputFormat == OutputFormat.Json)
                {
                    writer.WriteSuccess(plan);
                }
                else
                {
                    Console.WriteLine(formatted);
                }

                return ExitCodes.Success;
            }

            // If --show-fetchxml, transpile only (no execution)
            if (showFetchXml)
            {
                if (globalOptions.OutputFormat == OutputFormat.Text)
                {
                    Console.Error.WriteLine("Parsing SQL...");
                }

                var fetchXml = sqlQueryService.TranspileSql(query, top);

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

            // Execute query via service
            if (globalOptions.OutputFormat == OutputFormat.Text)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Executing query...");
            }

            var request = new SqlQueryRequest
            {
                Sql = query,
                TopOverride = top,
                PageNumber = page,
                PagingCookie = pagingCookie,
                IncludeCount = count,
                UseTdsEndpoint = useTds,
                DmlSafety = new DmlSafetyOptions
                {
                    IsConfirmed = confirm,
                    IsDryRun = dryRun,
                    NoLimit = noLimit
                }
            };

            var queryResult = await sqlQueryService.ExecuteAsync(request, cancellationToken);

            switch (globalOptions.OutputFormat)
            {
                case OutputFormat.Json:
                    writer.WriteSuccess(queryResult.Result);
                    break;
                case OutputFormat.Csv:
                    QueryResultFormatter.WriteCsvOutput(queryResult.Result);
                    break;
                default:
                    QueryResultFormatter.WriteTableOutput(
                        queryResult.Result,
                        globalOptions.Verbose,
                        queryResult.TranspiledFetchXml);
                    break;
            }

            return ExitCodes.Success;
        }
        catch (QueryParseException ex)
        {
            string? details = null;
            if (globalOptions.Debug && ex.Errors.Count > 0)
            {
                var first = ex.Errors[0];
                details = $"Line {first.Line}, Column {first.Column}: {first.Message}";
            }

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

}
