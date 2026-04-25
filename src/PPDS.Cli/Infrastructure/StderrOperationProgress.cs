namespace PPDS.Cli.Infrastructure;

/// <summary>
/// Stderr adapter for <see cref="IOperationProgress"/>. Renders progress as a
/// carriage-return-updated line so it overwrites in place on a TTY, then
/// terminates with a newline on completion or error.
/// </summary>
/// <remarks>
/// Why stderr: Constitution I1 reserves stdout for data so that command output
/// remains pipeable. Status, progress, and diagnostics belong on stderr.
/// </remarks>
public sealed class StderrOperationProgress : IOperationProgress
{
    private readonly TextWriter _writer;
    private bool _hasInlineLine;

    /// <summary>
    /// Creates a stderr progress reporter.
    /// </summary>
    /// <param name="writer">Defaults to <see cref="Console.Error"/> when null.</param>
    public StderrOperationProgress(TextWriter? writer = null)
    {
        _writer = writer ?? Console.Error;
    }

    /// <inheritdoc />
    public void ReportStatus(string message)
    {
        EndInlineLine();
        _writer.WriteLine(message);
    }

    /// <inheritdoc />
    public void ReportProgress(int current, int total, string? message = null)
    {
        var label = string.IsNullOrEmpty(message) ? string.Empty : $" {message}";
        if (total > 0)
        {
            var pct = (double)current / total * 100.0;
            WriteInline($"  Progress: {current:N0}/{total:N0} ({pct:F1}%){label}");
        }
        else
        {
            WriteInline($"  Progress: {current:N0}{label}");
        }
    }

    /// <inheritdoc />
    public void ReportProgress(double fraction, string? message = null)
    {
        var pct = Math.Clamp(fraction, 0.0, 1.0) * 100.0;
        var label = string.IsNullOrEmpty(message) ? string.Empty : $" {message}";
        WriteInline($"  Progress: {pct:F1}%{label}");
    }

    /// <inheritdoc />
    public void ReportComplete(string message)
    {
        EndInlineLine();
        _writer.WriteLine(message);
    }

    /// <inheritdoc />
    public void ReportError(string message)
    {
        EndInlineLine();
        _writer.WriteLine($"Error: {message}");
    }

    private void WriteInline(string text)
    {
        _writer.Write($"\r{text}");
        _writer.Flush();
        _hasInlineLine = true;
    }

    private void EndInlineLine()
    {
        if (_hasInlineLine)
        {
            _writer.WriteLine();
            _hasInlineLine = false;
        }
    }
}
