using System.CommandLine;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Infrastructure.Output;
using PPDS.Cli.Services.Solutions;

namespace PPDS.Cli.Commands.Solutions;

/// <summary>
/// Import a solution from a ZIP file.
/// </summary>
public static class ImportCommand
{
    public static Command Create()
    {
        var fileArgument = new Argument<FileInfo>("file")
        {
            Description = "The solution ZIP file to import"
        };

        var overwriteOption = new Option<bool>("--overwrite", "-w")
        {
            Description = "Overwrite unmanaged customizations (default: true)",
            DefaultValueFactory = _ => true
        };

        var publishWorkflowsOption = new Option<bool>("--publish-workflows")
        {
            Description = "Automatically publish workflows (default: true)",
            DefaultValueFactory = _ => true
        };

        var command = new Command("import", "Import a solution from a ZIP file")
        {
            fileArgument,
            overwriteOption,
            publishWorkflowsOption,
            SolutionsCommandGroup.ProfileOption,
            SolutionsCommandGroup.EnvironmentOption
        };

        GlobalOptions.AddToCommand(command);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var file = parseResult.GetValue(fileArgument)!;
            var overwrite = parseResult.GetValue(overwriteOption);
            var publishWorkflows = parseResult.GetValue(publishWorkflowsOption);
            var profile = parseResult.GetValue(SolutionsCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(SolutionsCommandGroup.EnvironmentOption);
            var globalOptions = GlobalOptions.GetValues(parseResult);

            return await ExecuteAsync(file, overwrite, publishWorkflows, profile, environment, globalOptions, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        FileInfo file,
        bool overwrite,
        bool publishWorkflows,
        string? profile,
        string? environment,
        GlobalOptionValues globalOptions,
        CancellationToken cancellationToken)
    {
        var writer = ServiceFactory.CreateOutputWriter(globalOptions);

        try
        {
            if (!file.Exists)
            {
                var error = new StructuredError(
                    ErrorCodes.Validation.FileNotFound,
                    $"File not found: {file.FullName}",
                    null,
                    file.FullName);
                writer.WriteError(error);
                return ExitCodes.InvalidArguments;
            }

            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile,
                environment,
                globalOptions.Verbose,
                globalOptions.Debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var solutionService = serviceProvider.GetRequiredService<ISolutionService>();

            if (!globalOptions.IsJsonMode)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Importing solution: {file.Name}...");
            }

            var solutionZip = await File.ReadAllBytesAsync(file.FullName, cancellationToken);
            var importJobId = await solutionService.ImportAsync(solutionZip, overwrite, publishWorkflows, cancellationToken);

            if (globalOptions.IsJsonMode)
            {
                var output = new ImportOutput
                {
                    ImportJobId = importJobId,
                    FileName = file.Name,
                    FileSizeBytes = file.Length
                };
                writer.WriteSuccess(output);
            }
            else
            {
                Console.Error.WriteLine($"Import started. Job ID: {importJobId}");
                Console.Error.WriteLine();
                Console.Error.WriteLine("To monitor progress:");
                Console.Error.WriteLine($"  ppds importjobs wait {importJobId}");
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            var error = ExceptionMapper.Map(ex, context: $"importing solution from '{file.Name}'", debug: globalOptions.Debug);
            writer.WriteError(error);
            return ExceptionMapper.ToExitCode(ex);
        }
    }

    #region Output Models

    private sealed class ImportOutput
    {
        [JsonPropertyName("importJobId")]
        public Guid ImportJobId { get; set; }

        [JsonPropertyName("fileName")]
        public string FileName { get; set; } = string.Empty;

        [JsonPropertyName("fileSizeBytes")]
        public long FileSizeBytes { get; set; }
    }

    #endregion
}
