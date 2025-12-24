using Microsoft.Extensions.Configuration;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Helper for building configuration from appsettings.json files.
/// </summary>
public static class ConfigurationHelper
{
    /// <summary>
    /// Default configuration file name.
    /// </summary>
    public const string DefaultConfigFileName = "appsettings.json";

    /// <summary>
    /// Builds configuration from the specified config file path.
    /// </summary>
    /// <param name="configPath">Path to config file, or null for default (appsettings.json in CWD).</param>
    /// <returns>The built configuration.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the config file does not exist.</exception>
    public static IConfiguration Build(string? configPath = null)
    {
        var path = configPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigFileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file not found: {path}. " +
                $"Provide a valid --config path or create {DefaultConfigFileName} in the current directory.",
                path);
        }

        return new ConfigurationBuilder()
            .AddJsonFile(path, optional: false)
            .Build();
    }

    /// <summary>
    /// Gets available environment names from the configuration.
    /// </summary>
    /// <param name="configuration">The configuration to read from.</param>
    /// <param name="sectionName">The root section name. Default: "Dataverse".</param>
    /// <returns>A list of environment names, or empty if none configured.</returns>
    public static IReadOnlyList<string> GetEnvironmentNames(IConfiguration configuration, string sectionName = "Dataverse")
    {
        var section = configuration.GetSection($"{sectionName}:Environments");
        return section.GetChildren().Select(c => c.Key).ToList();
    }
}
