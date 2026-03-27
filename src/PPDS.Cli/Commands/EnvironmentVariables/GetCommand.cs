using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.EnvironmentVariables;

/// <summary>
/// Get details of a specific environment variable.
/// </summary>
public static class GetCommand
{
    public static Command Create()
    {
        var schemaNameArgument = new Argument<string>("schemaName")
        {
            Description = "The schema name of the environment variable"
        };

        var command = new Command("get", "Get environment variable details")
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
                var output = new EnvironmentVariableDetails
                {
                    Id = variable.Id,
                    SchemaName = variable.SchemaName,
                    DisplayName = variable.DisplayName,
                    Description = variable.Description,
                    Type = variable.Type,
                    DefaultValue = variable.Type == "Secret" ? "[REDACTED]" : variable.DefaultValue,
                    CurrentValue = variable.Type == "Secret" ? "[REDACTED]" : variable.CurrentValue,
                    CurrentValueId = variable.CurrentValueId,
                    HasValue = variable.CurrentValueId.HasValue,
                    IsRequired = variable.IsRequired,
                    IsManaged = variable.IsManaged,
                    SecretStore = variable.SecretStore,
                    CreatedOn = variable.CreatedOn,
                    ModifiedOn = variable.ModifiedOn,
                    MakerUrl = makerUrl
                };
                writer.WriteSuccess(output);
            }
            else
            {
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();

                Console.WriteLine($"Schema Name:    {variable.SchemaName}");
                Console.WriteLine($"Display Name:   {variable.DisplayName ?? "-"}");
                Console.WriteLine($"Description:    {variable.Description ?? "-"}");
                Console.WriteLine($"Type:           {variable.Type}");
                Console.WriteLine($"Is Required:    {variable.IsRequired}");
                Console.WriteLine($"Is Managed:     {variable.IsManaged}");

                if (variable.Type == "Secret")
                {
                    Console.WriteLine($"Secret Store:   {variable.SecretStore ?? "-"}");
                    Console.WriteLine($"Default Value:  [REDACTED]");
                    Console.WriteLine($"Current Value:  {(variable.CurrentValueId.HasValue ? "[SET]" : "[NOT SET]")}");
                }
                else
                {
                    Console.WriteLine($"Default Value:  {variable.DefaultValue ?? "-"}");
                    Console.WriteLine($"Current Value:  {variable.CurrentValue ?? "[NOT SET]"}");
                }

                Console.WriteLine($"Created:        {variable.CreatedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
                Console.WriteLine($"Modified:       {variable.ModifiedOn?.ToString("yyyy-MM-dd HH:mm:ss") ?? "-"}");
                Console.WriteLine();
                Console.WriteLine($"Maker URL:      {makerUrl}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"getting environment variable '{schemaName}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class EnvironmentVariableDetails
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("schemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("currentValue")]
        public string? CurrentValue { get; set; }

        [JsonPropertyName("currentValueId")]
        public Guid? CurrentValueId { get; set; }

        [JsonPropertyName("hasValue")]
        public bool HasValue { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }

        [JsonPropertyName("secretStore")]
        public string? SecretStore { get; set; }

        [JsonPropertyName("createdOn")]
        public DateTime? CreatedOn { get; set; }

        [JsonPropertyName("modifiedOn")]
        public DateTime? ModifiedOn { get; set; }

        [JsonPropertyName("makerUrl")]
        public string MakerUrl { get; set; } = string.Empty;
    }

    #endregion
}
