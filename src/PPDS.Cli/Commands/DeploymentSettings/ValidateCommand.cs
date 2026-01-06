using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Dataverse.Services;

namespace PPDS.Cli.Commands.DeploymentSettings;

/// <summary>
/// Validate a deployment settings file against the current solution.
/// </summary>
public static class ValidateCommand
{
    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static Command Create()
    {
        var fileOption = new Option<string>("--file", "-f")
        {
            Description = "Deployment settings file path",
            Required = true
        };

        var command = new Command("validate", "Validate deployment settings file against solution")
        {
            DeploymentSettingsCommandGroup.SolutionOption,
            fileOption,
            DeploymentSettingsCommandGroup.ProfileOption,
            DeploymentSettingsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var solution = parseResult.GetValue(DeploymentSettingsCommandGroup.SolutionOption)!;
            var file = parseResult.GetValue(fileOption)!;
            var profile = parseResult.GetValue(DeploymentSettingsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DeploymentSettingsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(solution, file, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string solution,
        string filePath,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            var fullPath = Path.GetFullPath(filePath);

            if (!File.Exists(fullPath))
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.FileNotFound,
                    $"File not found: {fullPath}"));
                return ExitCodes.InvalidArguments;
            }

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
                Console.Error.WriteLine($"Validating deployment settings for solution '{solution}'...");
            }

            // Load settings file
            var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
            var settings = JsonSerializer.Deserialize<DeploymentSettingsFile>(json, JsonReadOptions);

            if (settings == null)
            {
                writer.WriteError(new StructuredError(
                    ErrorCodes.Validation.InvalidValue,
                    "Failed to parse deployment settings file"));
                return ExitCodes.InvalidArguments;
            }

            var result = await settingsService.ValidateAsync(solution, settings, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new ValidationOutput
                {
                    FilePath = fullPath,
                    IsValid = result.IsValid,
                    ErrorCount = result.Issues.Count(i => i.Severity == ValidationSeverity.Error),
                    WarningCount = result.Issues.Count(i => i.Severity == ValidationSeverity.Warning),
                    Issues = result.Issues.Select(i => new IssueOutput
                    {
                        Severity = i.Severity.ToString(),
                        EntryType = i.EntryType,
                        Name = i.Name,
                        Message = i.Message
                    }).ToList()
                };

                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine();

                if (result.IsValid)
                {
                    Console.Error.WriteLine("Validation passed - no issues found.");
                }
                else
                {
                    var errorCount = result.Issues.Count(i => i.Severity == ValidationSeverity.Error);
                    var warningCount = result.Issues.Count(i => i.Severity == ValidationSeverity.Warning);

                    Console.Error.WriteLine($"Validation complete: {errorCount} error(s), {warningCount} warning(s)");
                    Console.Error.WriteLine();

                    // Group by severity
                    var errors = result.Issues.Where(i => i.Severity == ValidationSeverity.Error).ToList();
                    var warnings = result.Issues.Where(i => i.Severity == ValidationSeverity.Warning).ToList();

                    if (errors.Count > 0)
                    {
                        Console.Error.WriteLine("ERRORS:");
                        foreach (var issue in errors)
                        {
                            Console.Error.WriteLine($"  [{issue.EntryType}] {issue.Name}");
                            Console.Error.WriteLine($"    {issue.Message}");
                        }
                        Console.Error.WriteLine();
                    }

                    if (warnings.Count > 0)
                    {
                        Console.Error.WriteLine("WARNINGS:");
                        foreach (var issue in warnings)
                        {
                            Console.Error.WriteLine($"  [{issue.EntryType}] {issue.Name}");
                            Console.Error.WriteLine($"    {issue.Message}");
                        }
                    }
                }
            }

            // Return error exit code if there are errors (not just warnings)
            var hasErrors = result.Issues.Any(i => i.Severity == ValidationSeverity.Error);
            return hasErrors ? ExitCodes.Failure : ExitCodes.Success;
        }
        catch (JsonException ex)
        {
            writer.WriteError(new StructuredError(
                ErrorCodes.Validation.InvalidValue,
                $"Invalid JSON in deployment settings file: {ex.Message}"));
            return ExitCodes.InvalidArguments;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: "validating deployment settings", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ValidationOutput
    {
        [JsonPropertyName("filePath")]
        public string FilePath { get; set; } = string.Empty;

        [JsonPropertyName("isValid")]
        public bool IsValid { get; set; }

        [JsonPropertyName("errorCount")]
        public int ErrorCount { get; set; }

        [JsonPropertyName("warningCount")]
        public int WarningCount { get; set; }

        [JsonPropertyName("issues")]
        public List<IssueOutput> Issues { get; set; } = new();
    }

    private sealed class IssueOutput
    {
        [JsonPropertyName("severity")]
        public string Severity { get; set; } = string.Empty;

        [JsonPropertyName("entryType")]
        public string EntryType { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("message")]
        public string Message { get; set; } = string.Empty;
    }

    #endregion
}
