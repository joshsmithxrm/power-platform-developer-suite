using System.CommandLine;
using System.Text.Json;
using PPDS.Migration.Cli.Infrastructure;

namespace PPDS.Migration.Cli.Commands;

/// <summary>
/// Configuration diagnostic commands.
/// </summary>
public static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "Configuration management and diagnostics");

        command.AddCommand(CreateListCommand());

        return command;
    }

    private static Command CreateListCommand()
    {
        var configOption = new Option<FileInfo?>(
            name: "--config",
            description: "Path to configuration file (default: appsettings.json in current directory)");

        var jsonOption = new Option<bool>(
            name: "--json",
            getDefaultValue: () => false,
            description: "Output as JSON");

        var command = new Command("list", "List available environments from configuration file")
        {
            configOption,
            jsonOption
        };

        command.SetHandler((context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            try
            {
                var configPath = config?.FullName;
                var resolvedPath = configPath ?? Path.Combine(Directory.GetCurrentDirectory(), ConfigurationHelper.DefaultConfigFileName);
                var configuration = ConfigurationHelper.Build(configPath);
                var environments = ConfigurationHelper.GetEnvironmentNames(configuration);

                if (json)
                {
                    var output = JsonSerializer.Serialize(new
                    {
                        configFile = resolvedPath,
                        environments = environments
                    }, new JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine(output);
                }
                else
                {
                    Console.WriteLine($"Configuration file: {resolvedPath}");
                    Console.WriteLine();

                    if (environments.Count == 0)
                    {
                        Console.WriteLine("No environments found in Dataverse:Environments section.");
                        Console.WriteLine();
                        Console.WriteLine("Expected structure:");
                        Console.WriteLine("  {");
                        Console.WriteLine("    \"Dataverse\": {");
                        Console.WriteLine("      \"Environments\": {");
                        Console.WriteLine("        \"Dev\": { ... },");
                        Console.WriteLine("        \"QA\": { ... },");
                        Console.WriteLine("        \"Prod\": { ... }");
                        Console.WriteLine("      }");
                        Console.WriteLine("    }");
                        Console.WriteLine("  }");
                    }
                    else
                    {
                        Console.WriteLine("Available environments:");
                        foreach (var env in environments)
                        {
                            Console.WriteLine($"  - {env}");
                        }
                        Console.WriteLine();
                        Console.WriteLine($"Use --env <name> with export/import/schema commands.");
                        Console.WriteLine($"Use --source-env <name> --target-env <name> with migrate command.");
                    }
                }

                context.ExitCode = ExitCodes.Success;
            }
            catch (FileNotFoundException ex)
            {
                ConsoleOutput.WriteError(ex.Message, json);
                context.ExitCode = ExitCodes.InvalidArguments;
            }
        });

        return command;
    }
}
