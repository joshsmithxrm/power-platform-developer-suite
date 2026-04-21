using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.EnvironmentVariables;

namespace PPDS.Cli.Commands.EnvironmentVariables;

/// <summary>
/// Set the value of an environment variable.
/// </summary>
public static class SetCommand
{
    public static Command Create()
    {
        var schemaNameArgument = new Argument<string>("schemaName")
        {
            Description = "The schema name of the environment variable"
        };

        var valueArgument = new Argument<string>("value")
        {
            Description = "The value to set"
        };

        var command = new Command("set", "Set an environment variable value")
        {
            schemaNameArgument,
            valueArgument,
            EnvironmentVariablesCommandGroup.ProfileOption,
            EnvironmentVariablesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schemaName = parseResult.GetValue(schemaNameArgument)!;
            var value = parseResult.GetValue(valueArgument)!;
            var profile = parseResult.GetValue(EnvironmentVariablesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(EnvironmentVariablesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(schemaName, value, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string schemaName,
        string value,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var envVarService = serviceProvider.GetRequiredService<IEnvironmentVariableService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Setting environment variable '{schemaName}'...");
            }

            var success = await envVarService.SetValueAsync(schemaName, value, cancellationToken);

            if (!success)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Environment variable '{schemaName}' not found.",
                    null,
                    schemaName);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            if (globalOptions.IsJsonMode)
            {
                var output = new SetValueOutput
                {
                    SchemaName = schemaName,
                    Success = true
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Successfully set value for '{schemaName}'.");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"setting environment variable '{schemaName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class SetValueOutput
    {
        [JsonPropertyName("schemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool Success { get; set; }
    }

    #endregion
}
