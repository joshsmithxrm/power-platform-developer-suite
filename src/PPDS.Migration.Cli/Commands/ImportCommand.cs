using System.CommandLine;
using System.CommandLine.Completions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Migration.Cli.Infrastructure;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;

// Aliases for clarity
using AuthResult = PPDS.Migration.Cli.Infrastructure.AuthResolver.AuthResult;

namespace PPDS.Migration.Cli.Commands;

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
        // Validate user mapping file exists if provided
        userMappingOption.Validators.Add(result =>
        {
            var file = result.GetValue(userMappingOption);
            if (file is { Exists: false })
                result.AddError($"User mapping file not found: {file.FullName}");
        });

        var stripOwnerFieldsOption = new Option<bool>("--strip-owner-fields")
        {
            Description = "Strip ownership fields (ownerid, createdby, modifiedby) allowing Dataverse to assign current user. Use when importing to a different environment where source users don't exist.",
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

        var envOption = new Option<string?>("--env")
        {
            Description = "Environment name from configuration (e.g., Dev, QA, Prod)"
        };
        // Add tab completion for environment names from configuration
        envOption.CompletionSources.Add(ctx =>
        {
            try
            {
                var config = ConfigurationHelper.Build(null, null);
                return ConfigurationHelper.GetEnvironmentNames(config)
                    .Select(name => new CompletionItem(name));
            }
            catch
            {
                return [];
            }
        });

        var configOption = new Option<FileInfo?>("--config")
        {
            Description = "Path to configuration file (default: appsettings.json in current directory)"
        };

        var command = new Command("import", "Import data from a ZIP file into Dataverse. " + ConfigurationHelper.GetConfigurationHelpDescription())
        {
            dataOption,
            envOption,
            configOption,
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
            var url = parseResult.GetValue(Program.UrlOption);
            var env = parseResult.GetValue(envOption);
            var config = parseResult.GetValue(configOption);
            var secretsId = parseResult.GetValue(Program.SecretsIdOption);
            var authMode = parseResult.GetValue(Program.AuthOption);
            var bypassPlugins = parseResult.GetValue(bypassPluginsOption);
            var bypassFlows = parseResult.GetValue(bypassFlowsOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            var mode = parseResult.GetValue(modeOption);
            var userMappingFile = parseResult.GetValue(userMappingOption);
            var stripOwnerFields = parseResult.GetValue(stripOwnerFieldsOption);
            var json = parseResult.GetValue(jsonOption);
            var verbose = parseResult.GetValue(verboseOption);
            var debug = parseResult.GetValue(debugOption);

            // Validate --auth config requires --env
            if (authMode == AuthMode.Config && string.IsNullOrEmpty(env))
            {
                ConsoleOutput.WriteError("--env is required when using --auth config.", json);
                return ExitCodes.InvalidArguments;
            }

            // Resolve authentication based on mode
            AuthResolver.AuthResult authResult;
            IConfiguration? configuration = null;
            try
            {
                // Build configuration if needed (for config mode or when using --env)
                if (authMode == AuthMode.Config || !string.IsNullOrEmpty(env))
                {
                    configuration = ConfigurationHelper.Build(config?.FullName, secretsId);
                }

                authResult = AuthResolver.Resolve(authMode, url, env, configuration);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                return ExitCodes.InvalidArguments;
            }

            return await ExecuteAsync(
                authResult, configuration, env, data, bypassPlugins, bypassFlows,
                continueOnError, mode, userMappingFile, stripOwnerFields, json, verbose, debug, cancellationToken);
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(
        AuthResolver.AuthResult authResult,
        IConfiguration? configuration,
        string? environmentName,
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
        // Create progress reporter first - it handles all user-facing output
        var progressReporter = ServiceFactory.CreateProgressReporter(json);

        try
        {
            // Determine URL for status message
            var displayUrl = authResult.Url ?? "(from config)";

            // Report connecting status with auth mode info
            var authModeInfo = authResult.Mode switch
            {
                AuthMode.Interactive => " (interactive login)",
                AuthMode.Managed => " (managed identity)",
                AuthMode.Env => " (environment variables)",
                _ => ""
            };
            progressReporter.Report(new ProgressEventArgs
            {
                Phase = MigrationPhase.Analyzing,
                Message = $"Connecting to Dataverse ({displayUrl}){authModeInfo}..."
            });

            // Create service provider based on auth mode
            await using var serviceProvider = ServiceFactory.CreateProviderForAuthMode(
                authResult.Mode, authResult, configuration, environmentName, verbose, debug);
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

            // Report if stripping owner fields
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

            // Execute import - progress reporter receives Complete() callback with results
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

    /// <summary>
    /// Maps CLI ImportMode to Migration library ImportMode.
    /// </summary>
    private static PPDS.Migration.Import.ImportMode MapImportMode(ImportMode mode) => mode switch
    {
        ImportMode.Create => PPDS.Migration.Import.ImportMode.Create,
        ImportMode.Update => PPDS.Migration.Import.ImportMode.Update,
        ImportMode.Upsert => PPDS.Migration.Import.ImportMode.Upsert,
        _ => PPDS.Migration.Import.ImportMode.Upsert
    };
}
