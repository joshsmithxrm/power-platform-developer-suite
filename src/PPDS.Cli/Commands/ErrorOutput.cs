using System.Reflection;

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
    /// </summary>
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
        }
    }

    /// <summary>
    /// Documentation URL.
    /// </summary>
    public const string DocumentationUrl = "https://github.com/joshsmithxrm/ppds-sdk";

    /// <summary>
    /// Issues URL.
    /// </summary>
    public const string IssuesUrl = "https://github.com/joshsmithxrm/ppds-sdk/issues";

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
