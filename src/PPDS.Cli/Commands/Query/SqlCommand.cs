using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;
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

        var dmlSafety = new DmlSafetyOptionSet();
        var confirmOption = dmlSafety.Confirm;
        var dryRunOption = dmlSafety.DryRun;
        var noLimitOption = dmlSafety.NoLimit;

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

        GlobalOptions.AddToCommand(command, supportsCsv: true);

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

        // --explain, --show-fetchxml, and --dry-run render plans / FetchXML blobs, not
        // result sets, so CSV is not applicable. Reject it rather than emitting nothing.
        command.Validators.Add(result =>
        {
            if (!result.GetValue(showFetchXmlOption) &&
                !result.GetValue(explainOption) &&
                !result.GetValue(dryRunOption))
            {
                return;
            }

            // If --output-format itself failed to parse, that error is already reported by
            // the option's parser; reading the value here would throw, so skip.
            OutputFormat format;
            try
            {
                format = GlobalOptions.GetOutputFormat(result);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (format == OutputFormat.Csv)
            {
                result.AddError(
                    "CSV output is not supported with --explain or --show-fetchxml. Use --output-format Json or Text. " +
                    "The same restriction applies to --dry-run.");
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
        var remoteProviders = new List<ServiceProvider>();

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

            // Wire cross-environment support: [LABEL].entity resolves via profile labels
            if (sqlQueryService is SqlQueryService concreteSqlService)
            {
                var envConfigStore = serviceProvider.GetRequiredService<EnvironmentConfigStore>();
                var envConfigs = await envConfigStore.LoadAsync(cancellationToken);
                var resolver = new ProfileResolutionService(envConfigs.Environments);

                concreteSqlService.RemoteExecutorFactory = label =>
                {
                    var config = resolver.ResolveByLabel(label);
                    if (config?.Url == null) return null;

#pragma warning disable PPDS012 // Planner is synchronous; single CLI invocation
                    var remoteProvider = ProfileServiceFactory.CreateFromProfileAsync(
                        profile, config.Url,
                        globalOptions.Verbose, globalOptions.Debug,
                        ProfileServiceFactory.DefaultDeviceCodeCallback,
                        cancellationToken: cancellationToken)
                        .GetAwaiter().GetResult();
#pragma warning restore PPDS012

                    remoteProviders.Add(remoteProvider);
                    return remoteProvider.GetRequiredService<IQueryExecutor>();
                };

                await ConfigureDmlSafetyAsync(
                    concreteSqlService,
                    envConfigStore,
                    resolver,
                    serviceProvider.GetRequiredService<ResolvedConnectionInfo>().EnvironmentUrl,
                    cancellationToken);
            }

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
                Console.Error.WriteLine(dryRun ? "Building DML preview..." : "Executing query...");
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

            if (queryResult.DmlSafetyResult?.IsDryRun == true)
            {
                WriteDryRunOutput(queryResult, globalOptions.OutputFormat, writer);
                return ExitCodes.Success;
            }

            return WriteResult(
                queryResult,
                globalOptions.OutputFormat,
                globalOptions.Verbose,
                writer,
                queryResult.TranspiledFetchXml,
                pagingHint: null);
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
        finally
        {
            foreach (var rp in remoteProviders)
                await rp.DisposeAsync();
        }
    }

    internal static void WriteDryRunOutput(
        SqlQueryResult result,
        OutputFormat outputFormat,
        IOutputWriter writer)
    {
        var output = new DmlDryRunOutput
        {
            Sql = result.OriginalSql,
            Plan = result.DryRunPlan,
            FetchXml = result.TranspiledFetchXml,
            RowCap = result.DmlSafetyResult?.RowCap ?? DmlSafetyGuard.DefaultRowCap,
            RequiresConfirmationForExecution = result.DmlSafetyResult?.RequiresConfirmation == true
        };

        if (outputFormat == OutputFormat.Json)
        {
            writer.WriteSuccess(output);
            return;
        }

        if (outputFormat == OutputFormat.Csv)
        {
            throw new PpdsValidationException(
                "outputFormat",
                "CSV output is not supported with a DML dry-run. Use --output-format Json or Text.");
        }

        Console.Error.WriteLine("Dry run complete. No Dataverse records were changed.");
        Console.Error.WriteLine($"Execution row cap: {output.RowCap:N0}");
        if (output.RequiresConfirmationForExecution)
        {
            Console.Error.WriteLine("Actual execution requires --confirm.");
        }

        if (output.Plan != null)
        {
            Console.WriteLine(PlanFormatter.Format(output.Plan));
        }

        if (!string.IsNullOrWhiteSpace(output.FetchXml))
        {
            Console.WriteLine("Planned FetchXML:");
            Console.WriteLine(output.FetchXml);
        }
    }

    internal sealed class DmlDryRunOutput
    {
        public bool DryRun => true;
        public required string Sql { get; init; }
        public QueryPlanDescription? Plan { get; init; }
        public string? FetchXml { get; init; }
        public int RowCap { get; init; }
        public bool RequiresConfirmationForExecution { get; init; }
    }

    /// <summary>
    /// Writes an executed query result and reports DML row failures. Data goes to stdout; the failure
    /// message goes to stderr (text/CSV) or into the JSON envelope (<c>success: false</c> with the unchanged
    /// <c>data</c>, a top-level <c>error</c> and a <c>results[]</c> entry). Returns the process exit code.
    /// </summary>
    internal static int WriteResult(
        SqlQueryResult queryResult,
        OutputFormat format,
        bool verbose,
        IOutputWriter writer,
        string? fetchXml,
        string? pagingHint)
    {
        var outcome = queryResult.DmlOutcome;
        var failure = outcome is { HasFailures: true }
            ? StructuredError.Create(outcome.ErrorCode, outcome.Message!)
            : null;

        switch (format)
        {
            case OutputFormat.Json when failure != null:
                writer.WritePartialSuccess(
                    queryResult.Result, new[] { ItemResult.Fail("dml", failure) }, failure);
                break;
            case OutputFormat.Json:
                writer.WriteSuccess(queryResult.Result);
                break;
            case OutputFormat.Csv:
                QueryResultFormatter.WriteCsvOutput(queryResult.Result);
                break;
            default:
                QueryResultFormatter.WriteTableOutput(queryResult.Result, verbose, fetchXml, pagingHint);
                break;
        }

        if (failure != null && format != OutputFormat.Json)
        {
            // Status goes to stderr; stdout stays data-only.
            Console.Error.WriteLine($"Error: {failure.Message}");
        }

        return outcome?.ExitCode ?? ExitCodes.Success;
    }

    /// <summary>
    /// Wires environment-specific DML safety settings (mirrors InteractiveSession). Shared by every
    /// CLI entry point that executes SQL so they all apply the same guard configuration.
    /// </summary>
    internal static async Task ConfigureDmlSafetyAsync(
        SqlQueryService service,
        EnvironmentConfigStore envConfigStore,
        ProfileResolutionService resolver,
        string environmentUrl,
        CancellationToken cancellationToken)
    {
        service.ProfileResolver = resolver;
        var envConfig = await envConfigStore.GetConfigAsync(environmentUrl, cancellationToken);
        if (envConfig != null)
        {
            service.EnvironmentSafetySettings = envConfig.SafetySettings;
            var envType = envConfig.Type ?? EnvironmentType.Unknown;
            service.EnvironmentProtectionLevel = envConfig.Protection
                ?? DmlSafetyGuard.DetectProtectionLevel(envType);
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
