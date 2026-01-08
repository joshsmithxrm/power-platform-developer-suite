using System.Reflection;
using System.Runtime.InteropServices;

namespace PPDS.Cli.Commands;

/// <summary>
/// Utility for consistent error output formatting.
/// </summary>
public static class ErrorOutput
{
    /// <summary>
    /// Whether color output should be used.
    /// Respects NO_COLOR standard (https://no-color.org/) and detects redirected output.
    /// </summary>
    private static bool UseColor =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")) &&
        !Console.IsErrorRedirected;

    /// <summary>
    /// Gets the CLI version from assembly information.
    /// Uses InformationalVersion which includes pre-release suffix and git commit hash.
    /// </summary>
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            return GetInformationalVersion(assembly);
        }
    }

    /// <summary>
    /// Gets the SDK version (PPDS.Dataverse assembly).
    /// Uses InformationalVersion which includes pre-release suffix and git commit hash.
    /// </summary>
    public static string SdkVersion
    {
        get
        {
            try
            {
                // Get version from PPDS.Dataverse assembly
                var sdkAssembly = typeof(PPDS.Dataverse.Pooling.IDataverseConnectionPool).Assembly;
                return GetInformationalVersion(sdkAssembly);
            }
            catch
            {
                return "0.0.0";
            }
        }
    }

    /// <summary>
    /// Extracts the informational version from an assembly.
    /// Falls back to assembly version if informational version is not available.
    /// </summary>
    private static string GetInformationalVersion(Assembly assembly)
    {
        // InformationalVersion includes pre-release suffix and commit hash (e.g., "1.2.3-beta.1+abc1234")
        var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(infoVersion))
        {
            return infoVersion;
        }

        // Fallback to assembly version
        var version = assembly.GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }

    /// <summary>
    /// Writes a diagnostic version header to stderr.
    /// Call this at CLI startup for troubleshooting context.
    /// </summary>
    /// <example>
    /// PPDS CLI v1.2.3 (SDK v1.2.3, .NET 8.0.1)
    /// Platform: Windows 10.0.22631
    /// </example>
    public static void WriteVersionHeader()
    {
        var runtimeVersion = Environment.Version.ToString();
        var platform = RuntimeInformation.OSDescription;

        Console.Error.WriteLine($"PPDS CLI v{Version} (SDK v{SdkVersion}, .NET {runtimeVersion})");
        Console.Error.WriteLine($"Platform: {platform}");
    }

    /// <summary>
    /// Documentation URL.
    /// </summary>
    public const string DocumentationUrl = "https://github.com/joshsmithxrm/power-platform-developer-suite";

    /// <summary>
    /// Issues URL.
    /// </summary>
    public const string IssuesUrl = "https://github.com/joshsmithxrm/power-platform-developer-suite/issues";

    /// <summary>
    /// Writes a formatted error message with version and documentation info.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static void WriteError(string message)
    {
        Console.Error.WriteLine("PPDS CLI");
        Console.Error.WriteLine($"Version: {Version}");
        Console.Error.WriteLine($"Documentation: {DocumentationUrl}");
        Console.Error.WriteLine($"Issues: {IssuesUrl}");
        Console.Error.WriteLine();
        WriteErrorLine(message);
    }

    /// <summary>
    /// Writes a simple error line in red to stderr.
    /// Respects NO_COLOR and detects redirected output.
    /// </summary>
    /// <param name="message">The error message (without "Error:" prefix).</param>
    public static void WriteLine(string message)
    {
        if (UseColor)
            Console.ForegroundColor = ConsoleColor.Red;

        Console.Error.WriteLine(message);

        if (UseColor)
            Console.ResetColor();
    }

    /// <summary>
    /// Writes "Error: {message}" in red to stderr.
    /// Respects NO_COLOR and detects redirected output.
    /// </summary>
    /// <param name="message">The error message.</param>
    public static void WriteErrorLine(string message)
    {
        if (UseColor)
            Console.ForegroundColor = ConsoleColor.Red;

        Console.Error.WriteLine($"Error: {message}");

        if (UseColor)
            Console.ResetColor();
    }

    /// <summary>
    /// Writes a formatted error message from an exception with version and documentation info.
    /// </summary>
    /// <param name="ex">The exception.</param>
    public static void WriteException(Exception ex)
    {
        WriteError(ex.Message);
    }
}
