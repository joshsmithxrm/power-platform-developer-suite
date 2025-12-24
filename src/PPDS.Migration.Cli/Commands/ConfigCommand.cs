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

        var command = new Command("list", "List available environments from configuration")
        {
            configOption,
            jsonOption
        };

        command.SetHandler((context) =>
        {
            var config = context.ParseResult.GetValueForOption(configOption);
            var secretsId = context.ParseResult.GetValueForOption(Program.SecretsIdOption);
            var json = context.ParseResult.GetValueForOption(jsonOption);

            try
            {
                var configPath = config?.FullName;
                var resolvedPath = configPath ?? Path.Combine(Directory.GetCurrentDirectory(), ConfigurationHelper.DefaultConfigFileName);

                // Build configuration with User Secrets if provided
                var configuration = ConfigurationHelper.Build(configPath, secretsId);
                var environments = ConfigurationHelper.GetEnvironmentNames(configuration);

                if (json)
                {
                    var output = JsonSerializer.Serialize(new
                    {
                        configFile = resolvedPath,
                        secretsId = secretsId,
                        environments = environments
                    }, new JsonSerializerOptions { WriteIndented = true });
                    Console.WriteLine(output);
                }
                else
                {
                    Console.WriteLine($"Configuration file: {resolvedPath}");
                    if (!string.IsNullOrEmpty(secretsId))
                    {
                        Console.WriteLine($"User Secrets ID: {secretsId}");
                    }
                    Console.WriteLine();

                    if (environments.Count == 0)
                    {
                        Console.WriteLine("No environments found in Dataverse:Environments section.");
                        Console.WriteLine();
                        Console.WriteLine("Expected structure in appsettings.json:");
                        Console.WriteLine("  {");
                        Console.WriteLine("    \"Dataverse\": {");
                        Console.WriteLine("      \"Environments\": {");
                        Console.WriteLine("        \"Dev\": { \"Url\": \"...\", \"Connections\": [...] },");
                        Console.WriteLine("        \"QA\": { ... },");
                        Console.WriteLine("        \"Prod\": { ... }");
                        Console.WriteLine("      }");
                        Console.WriteLine("    }");
                        Console.WriteLine("  }");
                        Console.WriteLine();
                        Console.WriteLine("Secrets (ClientId, ClientSecret) should be stored in User Secrets:");
                        Console.WriteLine("  dotnet user-secrets set \"Dataverse:Environments:Dev:Connections:0:ClientId\" \"<value>\"");
                        Console.WriteLine("  dotnet user-secrets set \"Dataverse:Environments:Dev:Connections:0:ClientSecret\" \"<value>\"");
                    }
                    else
                    {
                        Console.WriteLine("Available environments:");
                        foreach (var env in environments)
                        {
                            Console.WriteLine($"  - {env}");
                        }
                        Console.WriteLine();
                        Console.WriteLine("Usage:");
                        Console.WriteLine("  ppds-migrate export --env <name> --schema schema.xml --output data.zip");
                        Console.WriteLine("  ppds-migrate migrate --source-env Dev --target-env Prod --schema schema.xml");
                        Console.WriteLine();
                        Console.WriteLine("Cross-process (e.g., from demo app):");
                        Console.WriteLine("  ppds-migrate export --env Dev --secrets-id <your-project-secrets-id>");
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
