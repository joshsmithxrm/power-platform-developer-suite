using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.EnvironmentVariables;

namespace PPDS.Cli.Commands.EnvironmentVariables;

/// <summary>
/// List environment variables.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Filter by solution unique name"
        };

        var command = new Command("list", "List environment variables")
        {
            solutionOption,
            EnvironmentVariablesCommandGroup.ProfileOption,
            EnvironmentVariablesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption);
            var profile = parseResult.GetValue(EnvironmentVariablesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(EnvironmentVariablesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? solution,
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
            }

            var variablesResult = await envVarService.ListAsync(solutionName: solution, cancellationToken: cancellationToken);
            var variables = variablesResult.Items;

            if (globalOptions.IsJsonMode)
            {
                var output = variables.Select(v => new EnvironmentVariableListItem
                {
                    Id = v.Id,
                    SchemaName = v.SchemaName,
                    DisplayName = v.DisplayName,
                    Type = v.Type,
                    CurrentValue = v.Type == "Secret" ? "[REDACTED]" : v.CurrentValue,
                    DefaultValue = v.Type == "Secret" ? "[REDACTED]" : v.DefaultValue,
                    HasValue = v.CurrentValueId.HasValue,
                    IsRequired = v.IsRequired,
                    IsManaged = v.IsManaged
                }).ToList();

                writer.WriteSuccess(output);
            }
            else
            {
                if (variables.Count == 0)
                {
                    Console.Error.WriteLine("No environment variables found.");
                }
                else
                {
                    Console.Error.WriteLine($"Found {variables.Count} environment variable(s):");
                    Console.Error.WriteLine();

                    foreach (var v in variables)
                    {
                        var valueDisplay = v.CurrentValueId.HasValue
                            ? (v.Type == "Secret" ? "[SET]" : TruncateValue(v.CurrentValue))
                            : (v.DefaultValue != null ? $"(default: {TruncateValue(v.DefaultValue)})" : "[NOT SET]");

                        Console.WriteLine($"  {v.SchemaName}");
                        Console.WriteLine($"    Type: {v.Type}  Value: {valueDisplay}");
                        if (v.IsManaged) Console.WriteLine($"    Managed: Yes");
                        Console.WriteLine();
                    }
                }
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "listing environment variables", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    private static string? TruncateValue(string? value, int maxLength = 50)
    {
        if (value == null) return null;
        return value.Length > maxLength ? value[..(maxLength - 3)] + "..." : value;
    }

    #region Output Models

    private sealed class EnvironmentVariableListItem
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }

        [JsonPropertyName("schemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("currentValue")]
        public string? CurrentValue { get; set; }

        [JsonPropertyName("defaultValue")]
        public string? DefaultValue { get; set; }

        [JsonPropertyName("hasValue")]
        public bool HasValue { get; set; }

        [JsonPropertyName("isRequired")]
        public bool IsRequired { get; set; }

        [JsonPropertyName("isManaged")]
        public bool IsManaged { get; set; }
    }

    #endregion
}
