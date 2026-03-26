using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.EnvironmentVariables;

/// <summary>
/// Get the Maker portal URL for an environment variable.
/// </summary>
public static class UrlCommand
{
    public static Command Create()
    {
        var schemaNameArgument = new Argument<string>("schemaName")
        {
            Description = "The schema name of the environment variable"
        };

        var command = new Command("url", "Get the Maker portal URL for an environment variable")
        {
            schemaNameArgument,
            EnvironmentVariablesCommandGroup.ProfileOption,
            EnvironmentVariablesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schemaName = parseResult.GetValue(schemaNameArgument)!;
            var profile = parseResult.GetValue(EnvironmentVariablesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(EnvironmentVariablesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(schemaName, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string schemaName,
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
            var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();

            var variable = await envVarService.GetAsync(schemaName, cancellationToken);

            if (variable == null)
            {
                var error = new StructuredError(
                    ErrorCodes.Operation.NotFound,
                    $"Environment variable '{schemaName}' not found.",
                    null,
                    schemaName);
                writer.WriteError(error);
                return ExitCodes.NotFoundError;
            }

            var makerUrl = DataverseUrlBuilder.BuildEnvironmentVariableMakerUrl(connectionInfo.EnvironmentUrl, variable.Id);

            if (globalOptions.IsJsonMode)
            {
                var output = new UrlOutput
                {
                    SchemaName = schemaName,
                    EnvironmentVariableDefinitionId = variable.Id,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                // Just output the URL for easy piping
                Console.WriteLine(makerUrl);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting URL for environment variable '{schemaName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class UrlOutput
    {
        [JsonPropertyName("schemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("environmentVariableDefinitionId")]
        public Guid EnvironmentVariableDefinitionId { get; set; }

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
