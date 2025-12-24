using Microsoft.Extensions.Configuration;

namespace PPDS.Migration.Cli.Infrastructure;

/// <summary>
/// Helper for building configuration using standard .NET configuration layering.
/// </summary>
/// <remarks>
/// Configuration sources are layered in priority order (highest wins):
/// <list type="number">
///   <item>Environment variables (Dataverse__* format)</item>
///   <item>User Secrets (via --secrets-id parameter)</item>
///   <item>appsettings.{DOTNET_ENVIRONMENT}.json</item>
///   <item>appsettings.json (base)</item>
/// </list>
/// </remarks>
public static class ConfigurationHelper
{
    /// <summary>
    /// Default configuration file name.
    /// </summary>
    public const string DefaultConfigFileName = "appsettings.json";

    /// <summary>
    /// Builds configuration using standard .NET configuration layering.
    /// </summary>
    /// <param name="configPath">Path to config file, or null for default (appsettings.json in CWD).</param>
    /// <param name="secretsId">User Secrets ID for cross-process secret sharing (e.g., from calling application).</param>
    /// <returns>The built configuration with all sources layered.</returns>
    /// <exception cref="FileNotFoundException">Thrown when --env is used but config file does not exist.</exception>
    /// <remarks>
    /// When <paramref name="secretsId"/> is provided, User Secrets from that project's secret store
    /// will be loaded. This enables cross-process scenarios where a calling application (like a demo project)
    /// can share its secrets with the CLI.
    /// </remarks>
    public static IConfiguration Build(string? configPath = null, string? secretsId = null)
    {
        var builder = new ConfigurationBuilder();

        // Determine config file path
        var path = configPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigFileName);
        var configDir = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        var configFileName = Path.GetFileName(path);
        var configFileNameWithoutExt = Path.GetFileNameWithoutExtension(path);
        var configExt = Path.GetExtension(path);

        // 1. Base: appsettings.json (optional - may use env vars only)
        if (File.Exists(path))
        {
            builder.AddJsonFile(path, optional: false, reloadOnChange: false);
        }

        // 2. Environment-specific: appsettings.{Environment}.json
        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
        var envSpecificPath = Path.Combine(configDir, $"{configFileNameWithoutExt}.{environment}{configExt}");
        if (File.Exists(envSpecificPath))
        {
            builder.AddJsonFile(envSpecificPath, optional: true, reloadOnChange: false);
        }

        // 3. User Secrets (if secrets ID provided)
        if (!string.IsNullOrEmpty(secretsId))
        {
            // Use the userSecretsId overload - reloadOnChange is the second parameter
            builder.AddUserSecrets(secretsId, reloadOnChange: false);
        }

        // 4. Environment variables (Dataverse__* format - standard .NET pattern)
        builder.AddEnvironmentVariables();

        return builder.Build();
    }

    /// <summary>
    /// Builds configuration and validates that the config file exists.
    /// Use this when --env is specified and a config file is required.
    /// </summary>
    /// <param name="configPath">Path to config file, or null for default.</param>
    /// <param name="secretsId">User Secrets ID for cross-process secret sharing.</param>
    /// <returns>The built configuration.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the config file does not exist.</exception>
    public static IConfiguration BuildRequired(string? configPath = null, string? secretsId = null)
    {
        var path = configPath ?? Path.Combine(Directory.GetCurrentDirectory(), DefaultConfigFileName);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Configuration file not found: {path}. " +
                $"Provide a valid --config path or create {DefaultConfigFileName} in the current directory.",
                path);
        }

        return Build(configPath, secretsId);
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

    /// <summary>
    /// Gets a description of how configuration can be provided.
    /// </summary>
    public static string GetConfigurationHelpDescription()
    {
        return "Configure via appsettings.json + User Secrets (--secrets-id), or environment variables (Dataverse__*).";
    }
}
