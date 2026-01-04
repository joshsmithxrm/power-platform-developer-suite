using PPDS.Cli.Infrastructure.Errors;

namespace PPDS.Cli.Infrastructure.Output;

/// <summary>
/// Writes human-readable output to the console.
/// Success data goes to stdout, errors to stderr.
/// </summary>
/// <remarks>
/// This writer respects the NO_COLOR environment variable and
/// disables color output when stdout is redirected.
/// </remarks>
public sealed class TextOutputWriter : IOutputWriter
{
    private const int MaxItemErrorsToDisplay = 10;

    /// <inheritdoc />
    public bool DebugMode { get; }

    /// <inheritdoc />
    public bool IsJsonMode => false;

    /// <summary>
    /// Creates a new TextOutputWriter.
    /// </summary>
    /// <param name="debugMode">Whether to show full error details including stack traces.</param>
    public TextOutputWriter(bool debugMode = false)
    {
        DebugMode = debugMode;
    }

    /// <summary>
    /// Whether color output should be used for the given writer.
    /// Respects NO_COLOR standard and detects redirected output.
    /// </summary>
    private static bool UseColorFor(TextWriter writer)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR")))
        {
            return false;
        }

        // Check redirection based on which stream is being written to
        if (writer == Console.Error)
        {
            return !Console.IsErrorRedirected;
        }

        return !Console.IsOutputRedirected;
    }

    /// <inheritdoc />
    public void WriteResult<T>(CommandResult<T> result)
    {
        if (result.Success)
        {
            WriteSuccess(result.Data!);
        }
        else if (result.Results?.Count > 0)
        {
            WritePartialSuccess(result.Data!, result.Results);
        }
        else if (result.Error != null)
        {
            WriteError(result.Error);
        }
    }

    /// <inheritdoc />
    public void WriteSuccess<T>(T data)
    {
        WriteData(data);
    }

    /// <inheritdoc />
    public void WriteError(StructuredError error)
    {
        WriteWithColor(ConsoleColor.Red, Console.Error, $"Error: {error.Message}");

        if (!string.IsNullOrEmpty(error.Target))
        {
            Console.Error.WriteLine($"  Target: {error.Target}");
        }

        if (!string.IsNullOrEmpty(error.Details))
        {
            Console.Error.WriteLine($"  Details: {error.Details}");
        }

        if (DebugMode)
        {
            Console.Error.WriteLine($"  Code: {error.Code}");
        }
    }

    /// <inheritdoc />
    public void WritePartialSuccess<T>(T data, IEnumerable<ItemResult> results)
    {
        var resultsList = results.ToList();
        var successCount = resultsList.Count(r => r.Success);
        var failureCount = resultsList.Count(r => !r.Success);

        WriteWithColor(ConsoleColor.Yellow, Console.Out, $"Partial success: {successCount} succeeded, {failureCount} failed");

        var failures = resultsList.Where(r => !r.Success).ToList();
        if (failures.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("Failed items:");

            foreach (var failure in failures.Take(MaxItemErrorsToDisplay))
            {
                WriteWithColor(ConsoleColor.Red, Console.Error, $"  - {failure.Name}: {failure.Error?.Message}");
            }

            if (failures.Count > MaxItemErrorsToDisplay)
            {
                Console.Error.WriteLine($"  ... and {failures.Count - MaxItemErrorsToDisplay} more errors");
            }
        }

        if (data != null)
        {
            Console.WriteLine();
            WriteData(data);
        }
    }

    /// <inheritdoc />
    public void WriteMessage(string message)
    {
        Console.WriteLine(message);
    }

    /// <inheritdoc />
    public void WriteWarning(string message)
    {
        WriteWithColor(ConsoleColor.Yellow, Console.Out, $"Warning: {message}");
    }

    private void WriteData<T>(T data)
    {
        if (data == null)
        {
            return;
        }

        if (data is string str)
        {
            Console.WriteLine(str);
        }
        else if (data is IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                Console.WriteLine(item);
            }
        }
        else
        {
            Console.WriteLine(data);
        }
    }

    private static void WriteWithColor(ConsoleColor color, TextWriter writer, string message)
    {
        var useColor = UseColorFor(writer);

        if (useColor)
        {
            Console.ForegroundColor = color;
        }

        writer.WriteLine(message);

        if (useColor)
        {
            Console.ResetColor();
        }
    }
}
