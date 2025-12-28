using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

namespace PPDS.Cli.Commands.Data;

/// <summary>
/// Import data from a ZIP file into a Dataverse environment.
/// </summary>
public static class ImportCommand
{
    public static Command Create()
    {
        var dataOption = new Option<FileInfo>("--data", "-d")
        {
            Description = "Path to data.zip file",
            Required = true
        }.AcceptExistingOnly();

        var bypassPluginsOption = new Option<bool>("--bypass-plugins")
        {
            Description = "Bypass custom plugin execution during import",
            DefaultValueFactory = _ => false
        };

        var bypassFlowsOption = new Option<bool>("--bypass-flows")
        {
            Description = "Bypass Power Automate flow triggers during import",
            DefaultValueFactory = _ => false
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Continue import on individual record failures",
            DefaultValueFactory = _ => false
        };

        var modeOption = new Option<ImportMode>("--mode")
        {
            Description = "Import mode: Create, Update, or Upsert",
            DefaultValueFactory = _ => ImportMode.Upsert
        };

        var userMappingOption = new Option<FileInfo?>("--user-mapping", "-u")
        {
            Description = "Path to user mapping XML file for remapping user references"
        };
        userMappingOption.Validators.Add(result =>
        {
            var file = result.GetValue(userMappingOption);
            if (file is { Exists: false })
                result.AddError($"User mapping file not found: {file.FullName}");
        });

        var stripOwnerFieldsOption = new Option<bool>("--strip-owner-fields")
        {
            Description = "Strip ownership fields (ownerid, createdby, modifiedby) allowing Dataverse to assign current user",
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

        var command = new Command("import", "Import data from a ZIP file into Dataverse")
        {
            dataOption,
            DataCommandGroup.ProfileOption,
            DataCommandGroup.EnvironmentOption,
            bypassPluginsOption,
            bypassFlowsOption,
            continueOnErrorOption,
            modeOption,
            userMappingOption,
            stripOwnerFieldsOption,
            jsonOption,
            verboseOption,
            debugOption
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var data = parseResult.GetValue(dataOption)!;
            var profile = parseResult.GetValue(DataCommandGroup.ProfileOption);
            var environment = parseResult.GetValue(DataCommandGroup.EnvironmentOption);
            var bypassPlugins = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var mode = parseResult.GetValue(modeOption);
            var userMappingFile = parseResult.GetValue(userMappingOption);
            var stripOwnerFields = parseResult.GetValue(stripOwnerFieldsOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            return await ExecuteAsync(
                profile, environment, data, bypassPlugins, bypassFlows,
                continueOnError, mode, userMappingFile, stripOwnerFields,
                json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        string? profile,
        string? environment,
        FileInfo data,
        bool bypassPlugins,
        bool bypassFlows,
        bool continueOnError,
        ImportMode mode,
        FileInfo? userMappingFile,
        bool stripOwnerFields,
        bool json,
        bool verbose,
        bool debug,
        CancellationToken cancellationToken)
    {
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            var profileInfo = string.IsNullOrEmpty(profile) ? "active profile" : $"profile '{profile}'";
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Connecting to Dataverse using {profileInfo}..."
            });

            // Create service provider from profile(s)
            await using var serviceProvider = await ProfileServiceFactory.CreateFromProfilesAsync(
                profile ?? string.Empty,
                environment,
                verbose,
                debug,
                ProfileServiceFactory.DefaultDeviceCodeCallback,
                cancellationToken);

            var importer = serviceProvider.GetRequiredService<IImporter>();

            // Load user mappings if provided
            UserMappingCollection? userMappings = null;
            if (userMappingFile != null)
            {
                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Loading user mappings from {userMappingFile.Name}..."
                });

                var mappingReader = new UserMappingReader();
                userMappings = await mappingReader.ReadAsync(userMappingFile.FullName, cancellationToken);

                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = $"Loaded {userMappings.Mappings.Count} user mapping(s)."
                });
            }

            if (stripOwnerFields)
            {
                progressReporter.Report(new ProgressEventArgs
                {
                    Phase = MigrationPhase.Analyzing,
                    Message = "Owner fields will be stripped (ownerid, createdby, modifiedby, etc.)"
                });
            }

            // Configure import options
            var importOptions = new ImportOptions
            {
                BypassCustomPluginExecution = bypassPlugins,
                BypassPowerAutomateFlows = bypassFlows,
                ContinueOnError = continueOnError,
                Mode = MapImportMode(mode),
                UserMappings = userMappings,
                StripOwnerFields = stripOwnerFields
            };

            // Execute import
            var result = await importer.ImportAsync(
                data.FullName,
                importOptions,
                progressReporter,
                cancellationToken);

            return result.Success ? ExitCodes.Success : ExitCodes.Failure;
        }
        catch (OperationCanceledException)
        {
            progressReporter.Error(new OperationCanceledException(), "Import cancelled by user.");
            return ExitCodes.Failure;
        }
        catch (Exception ex)
        {
            progressReporter.Error(ex, "Import failed");
            if (debug)
            {
                Console.Error.WriteLine(ex.StackTrace);
            }
            return ExitCodes.Failure;
        }
    }

    private static PPDS.Migration.Import.ImportMode MapImportMode(ImportMode mode) => mode switch
    {
        ImportMode.Create => PPDS.Migration.Import.ImportMode.Create,
        ImportMode.Update => PPDS.Migration.Import.ImportMode.Update,
        ImportMode.Upsert => PPDS.Migration.Import.ImportMode.Upsert,
        _ => PPDS.Migration.Import.ImportMode.Upsert
    };
}
