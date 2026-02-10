using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Query;
using PPDS.Dataverse.Query.Planning;
using PPDS.Query.Parsing;

namespace PPDS.Cli.Commands.Query;

/// <summary>
/// Shows the execution plan for a SQL query without executing it.
/// </summary>
public static class ExplainCommand
{
    /// <summary>
    /// Creates the 'explain' command.
    /// </summary>
    public static Command Create()
    {
        var sqlArgument = new Argument<string>("sql")
        {
            Description = "SQL query to explain"
        };

        var command = new Command("explain", "Show the execution plan for a SQL query")
        {
            sqlArgument,
            QueryCommandGroup.ProfileOption,
            QueryCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var sql = parseResult.GetValue(sqlArgument)!;
            var profile = parseResult.GetValue(QueryCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(QueryCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(sql, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string sql,
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

            var sqlQueryService = serviceProvider.GetRequiredService<ISqlQueryService>();

            if (globalOptions.OutputFormat == OutputFormat.Text)
            {
                Console.Error.WriteLine("Building execution plan...");
            }

            var plan = await sqlQueryService.ExplainAsync(sql, cancellationToken);
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
            var error = ExceptionMapper.Map(ex, context: "explaining SQL query", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
