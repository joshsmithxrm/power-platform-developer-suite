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
                Console.Error.WriteLine($"Executing query from history: {id}");
                Console.Error.WriteLine();
            }

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
                        cancellationToken: CancellationToken.None)
                        .GetAwaiter().GetResult();
#pragma warning restore PPDS012

                    remoteProviders.Add(remoteProvider);
                    return remoteProvider.GetRequiredService<IQueryExecutor>();
                };
            }

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
                    QueryResultFormatter.WriteCsvOutput(queryResult.Result);
                    break;
                default:
                    QueryResultFormatter.WriteTableOutput(
                        queryResult.Result,
                        globalOptions.Verbose,
                        fetchXml: null,
                        pagingHint: "More records available (use 'ppds query sql' with --page for continuation)");
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
        finally
        {
            foreach (var rp in remoteProviders)
                await rp.DisposeAsync();
        }
    }

}
