using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Auth.Profiles;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services;
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

        // Same DML guard options as 'ppds query sql' (defined once in DmlSafetyOptionSet).
        var dmlSafety = new DmlSafetyOptionSet();

        var command = new Command("execute",
            "Re-execute a query from history. DML (INSERT/UPDATE/DELETE) is guarded exactly like 'ppds query sql': " +
            "it needs --confirm, honours the row cap and environment protection, and --dry-run previews the plan.")
        {
            idArgument,
            HistoryCommandGroup.ProfileOption,
            HistoryCommandGroup.EnvironmentOption,
            topOption
        };

        dmlSafety.AddTo(command);

        GlobalOptions.AddToCommand(command, supportsCsv: true);

        // --dry-run renders a plan, not a result set (same rule as 'query sql').
        command.Validators.Add(result =>
        {
            if (!result.GetValue(dmlSafety.DryRun))
                return;

            try
            {
                if (GlobalOptions.GetOutputFormat(result) == OutputFormat.Csv)
                    result.AddError(DmlSafetyOptionSet.DryRunCsvError);
            }
            catch (InvalidOperationException)
            {
                // --output-format failed to parse; its own error is already reported.
            }
        });

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var id = parseResult.GetValue(idArgument)!;
            var profile = parseResult.GetValue(HistoryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(HistoryCommandGroup.EnvironmentOption);
            var top = parseResult.GetValue(topOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(
                id, profile, environment, top, dmlSafety.GetOptions(parseResult), globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string id,
        string? profile,
        string? environment,
        int? top,
        DmlSafetyOptions dmlSafety,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);
        var remoteProviders = new List<ServiceProvider>();

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
            }

            // Show exactly what is about to run before any guard refusal, prompt or execution
            // ('query sql' echoes nothing because the user just typed the statement; here they did not).
            Console.Error.WriteLine(dmlSafety.IsDryRun
                ? $"Building DML preview for history entry {id}:"
                : $"Executing query from history entry {id}:");
            Console.Error.WriteLine(entry.Sql);
            Console.Error.WriteLine();

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

                await SqlCommand.ConfigureDmlSafetyAsync(
                    concreteSqlService, envConfigStore, resolver, environmentUrl, cancellationToken);
            }

            var request = BuildRequest(entry.Sql, top, dmlSafety);

            var queryResult = await sqlQueryService.ExecuteAsync(request, cancellationToken);

            SqlCommand.WriteTopIgnoredNote(queryResult, top);

            if (queryResult.DmlSafetyResult?.IsDryRun == true)
            {
                SqlCommand.WriteDryRunOutput(queryResult, globalOptions.OutputFormat, writer);
                return ExitCodes.Success;
            }

            return SqlCommand.WriteResult(
                queryResult,
                globalOptions.OutputFormat,
                globalOptions.Verbose,
                writer,
                fetchXml: null,
                pagingHint: "More records available (use 'ppds query sql' with --page for continuation)");
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "executing query from history", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
        finally
        {
            foreach (var rp in remoteProviders)
                await rp.DisposeAsync();
        }
    }

    /// <summary>
    /// Builds the replay request. DmlSafety is always set (a null value would skip every DML check), carrying
    /// the same options 'ppds query sql' builds from --confirm/--dry-run/--no-limit.
    /// </summary>
    internal static SqlQueryRequest BuildRequest(string sql, int? top, DmlSafetyOptions dmlSafety) => new()
    {
        Sql = sql,
        TopOverride = top,
        DmlSafety = dmlSafety
    };
}
