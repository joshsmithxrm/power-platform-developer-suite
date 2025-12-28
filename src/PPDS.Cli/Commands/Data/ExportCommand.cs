using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Commands;
using PPDS.Cli.Infrastructure;
using PPDS.Migration.Export;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Export data from a Dataverse environment to a ZIP file.
/// </summary>
public static class ExportCommand
{
    public static Command Create()
    {
        var schemaOption = new Option<FileInfo>("--schema", "-s")
        {
            Description = "Path to schema.xml file",
            Required = true
        }.AcceptExistingOnly();

        var outputOption = new Option<FileInfo>("--output", "-o")
        {
            Description = "Output ZIP file path",
            Required = true
        }.AcceptLegalFileNamesOnly();

        // Validate output directory exists
        outputOption.Validators.Add(result =>
        {
            var file = result.GetValue(outputOption);
            if (file?.Directory is { Exists: false })
                result.AddError($"Output directory does not exist: {file.Directory.FullName}");
        });

        var parallelOption = new Option<int>("--parallel")
        {
            Description = "Degree of parallelism for concurrent entity exports",
            DefaultValueFactory = _ => Environment.ProcessorCount * 2
        };
        parallelOption.Validators.Add(result =>
        {
            if (result.GetValue(parallelOption) < 1)
                result.AddError("--parallel must be at least 1");
        });

        var pageSizeOption = new Option<int>("--page-size")
        {
            Description = "FetchXML page size for data retrieval",
            DefaultValueFactory = _ => 5000
        };
        pageSizeOption.Validators.Add(result =>
        {
            var value = result.GetValue(pageSizeOption);
            if (value < 1)
                result.AddError("--page-size must be at least 1");
            if (value > 5000)
                result.AddError("--page-size cannot exceed 5000 (Dataverse limit)");
        });

        var includeFilesOption = new Option<bool>("--include-files")
        {
            Description = "Export file attachments (notes, annotations)",
            DefaultValueFactory = _ => false
        };

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Output progress as JSON (for tool integration)",
            DefaultValueFactory = _ => false
        };

        var verboseOption = new Option<bool>("--verbose", "-v")
        {
            Description = "Enable verbose logging output",
            DefaultValueFactory = _ => false
        };

        var debugOption = new Option<bool>("--debug")
        {
            Description = "Enable diagnostic logging output",
            DefaultValueFactory = _ => false
        };

        var command = new Command("export", "Export data from Dataverse to a ZIP file")
        {
            schemaOption,
            outputOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            parallelOption,
            pageSizeOption,
            includeFilesOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var schema = parseResult.GetValue(schemaOption)!;
            var output = parseResult.GetValue(outputOption)!;
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var parallel = parseResult.GetValue(parallelOption);
            var pageSize = parseResult.GetValue(pageSizeOption);
            var includeFiles = parseResult.GetValue(includeFilesOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteAsync(
                profile, environment, schema, output, parallel, pageSize,
                includeFiles, json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        FileInfo schema,
        FileInfo output,
        int parallel,
        int pageSize,
        bool includeFiles,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            // Create service provider from profile(s)
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile ?? string.Empty,
                environment,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            // Write connection header (non-JSON mode only)
            if (!json)
            {
                var connectionInfo = serviceProvider.GetRequiredService<ResolvedConnectionInfo>();
                ConsoleHeader.WriteConnectedAs(connectionInfo);
                Console.WriteLine();
            }

            var exporter = serviceProvider.GetRequiredService<IExporter>();

            // Configure export options
            var exportOptions = new ExportOptions
            {
                DegreeOfParallelism = parallel,
                PageSize = pageSize,
                ExportFiles = includeFiles
            };

            // Execute export
            var result = await exporter.ExportAsync(
                schema.FullName,
                output.FullName,
                exportOptions,
                progressReporter,
                cancellationToken);

            return result.Success ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Export cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            progressReporter.Error(ex, "Export failed");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }
}
