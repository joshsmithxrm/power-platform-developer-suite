using System.Text.Json;

namespace PPDS.Cli.Commands;

/// <summary>
/// Shared console output helpers for CLI commands.
/// Supports both human-readable and JSON output formats.
/// </summary>
public static class ConsoleOutput
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Writes a progress message to the console.
    /// </summary>
    /// <param name="phase">The current operation phase.</param>
    /// <param name="message">The progress message.</param>
    /// <param name="json">Whether to output as JSON.</param>
    public static void WriteProgress(string phase, string message, bool json)
    {
        if (json)
        {
            var output = new { phase, message, timestamp = DateTime.UtcNow.ToString("O") };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
        else
        {
            Console.WriteLine($"[{phase}] {message}");
        }
    }

    /// <summary>
    /// Writes a completion message to the console.
    /// </summary>
    /// <param name="duration">The operation duration.</param>
    /// <param name="recordsProcessed">Number of records processed.</param>
    /// <param name="errors">Number of errors encountered.</param>
    /// <param name="json">Whether to output as JSON.</param>
    public static void WriteCompletion(TimeSpan duration, int recordsProcessed, int errors, bool json)
    {
        if (json)
        {
            var output = new
            {
                phase = "complete",
                duration = duration.ToString(),
                recordsProcessed,
                errors,
                timestamp = DateTime.UtcNow.ToString("O")
            };
            Console.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
    }

    /// <summary>
    /// Writes an error message to the console.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="json">Whether to output as JSON.</param>
    public static void WriteError(string message, bool json)
    {
        if (json)
        {
            var output = new { phase = "error", message, timestamp = DateTime.UtcNow.ToString("O") };
            Console.Error.WriteLine(JsonSerializer.Serialize(output, JsonOptions));
        }
        else
        {
            Console.Error.WriteLine($"Error: {message}");
        }
    }
}
