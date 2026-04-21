using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.EnvironmentVariables;

namespace PPDS.Cli.Commands.EnvironmentVariables;

/// <summary>
/// Export environment variables for deployment settings.
/// </summary>
public static class ExportCommand
{
    public static Command Create()
    {
        var solutionOption = new Option<string?>("--solution", "-s")
        {
            Description = "Filter by solution unique name"
        };

        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output file path (default: stdout)"
        };

        var command = new Command("export", "Export environment variables for deployment settings")
        {
            solutionOption,
            outputOption,
            EnvironmentVariablesCommandGroup.ProfileOption,
            EnvironmentVariablesCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(solutionOption);
            var outputPath = parseResult.GetValue(outputOption);
            var profile = parseResult.GetValue(EnvironmentVariablesCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(EnvironmentVariablesCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, outputPath, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? solution,
        string? outputPath,
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

            if (!globalOptions.IsJsonMode && string.IsNullOrEmpty(outputPath))
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
            }

            var export = await envVarService.ExportAsync(solution, cancellationToken);

            // Convert to deployment settings format
            var deploymentSettings = new DeploymentSettingsExport
            {
                EnvironmentVariables = export.EnvironmentVariables
                    .Select(ev => new DeploymentSettingsVariable
                    {
                        SchemaName = ev.SchemaName,
                        Value = ev.Value ?? string.Empty
                    })
                    .ToList()
            };

            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            var json = JsonSerializer.Serialize(deploymentSettings, jsonOptions);

            if (!string.IsNullOrEmpty(outputPath))
            {
                var fullPath = Path.GetFullPath(outputPath);
                await File.WriteAllTextAsync(fullPath, json, cancellationToken);

                if (!globalOptions.IsJsonMode)
                {
                    Console.Error.WriteLine($"Exported {export.EnvironmentVariables.Count} environment variable(s) to: {fullPath}");
                }
                else
                {
                    var output = new ExportOutput
                    {
                        FilePath = fullPath,
                        Count = export.EnvironmentVariables.Count
                    };
                    writer.WriteSuccess(output);
                }
            }
            else
            {
                // Output to stdout
                Console.WriteLine(json);
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "exporting environment variables", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class DeploymentSettingsExport
    {
        [JsonPropertyName("EnvironmentVariables")]
        public List<DeploymentSettingsVariable> EnvironmentVariables { get; set; } = new();
    }

    private sealed class DeploymentSettingsVariable
    {
        [JsonPropertyName("SchemaName")]
        public string SchemaName { get; set; } = string.Empty;

        [JsonPropertyName("Value")]
        public string Value { get; set; } = string.Empty;
    }

    private sealed class ExportOutput
    {
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("count")]
        public int Count { get; set; }
    }

    #endregion
}
