using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.DeploymentSettings;

/// <summary>
/// Generate a new deployment settings file from the current environment.
/// </summary>
public static class GenerateCommand
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public static Command Create()
    {
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output file path",
            Required = true
        };

        var command = new Command("generate", "Generate a new deployment settings file from current environment")
        {
            DeploymentSettingsCommandGroup.SolutionOption,
            outputOption,
            DeploymentSettingsCommandGroup.ProfileOption,
            DeploymentSettingsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(DeploymentSettingsCommandGroup.SolutionOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var profile = parseResult.GetValue(DeploymentSettingsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DeploymentSettingsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, output, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string solution,
        string outputPath,
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

            var settingsService = serviceProvider.GetRequiredService<IDeploymentSettingsService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Generating deployment settings for solution '{solution}'...");
            }

            var settings = await settingsService.GenerateAsync(solution, cancellationToken);

            // Write to file
            var json = JsonSerializer.Serialize(settings, JsonWriteOptions);
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            await File.WriteAllTextAsync(fullPath, json, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                writer.WriteSuccess(new
                {
                    outputPath = fullPath,
                    environmentVariableCount = settings.EnvironmentVariables.Count,
                    connectionReferenceCount = settings.ConnectionReferences.Count
                });
            }
            else
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Generated deployment settings file: {fullPath}");
                Console.Error.WriteLine($"  Environment Variables: {settings.EnvironmentVariables.Count}");
                Console.Error.WriteLine($"  Connection References: {settings.ConnectionReferences.Count}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "generating deployment settings", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }
}
