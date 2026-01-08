using PPDS.Cli.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Terminal.Gui adapter for <see cref="IOperationProgress"/>.
/// Updates a ProgressBar and Label on the main UI thread.
/// </summary>
/// <remarks>
/// See ADR-0025 for architectural context.
/// </remarks>
public sealed class TuiOperationProgress : IOperationProgress
{
    private readonly ProgressBar? _progressBar;
    private readonly Label? _statusLabel;

    /// <summary>
    /// Creates a progress reporter that updates a ProgressBar.
    /// </summary>
    /// <param name="progressBar">The progress bar to update (optional).</param>
    /// <param name="statusLabel">The label for status messages (optional).</param>
    public TuiOperationProgress(ProgressBar? progressBar = null, Label? statusLabel = null)
    {
        _progressBar = progressBar;
        _statusLabel = statusLabel;
    }

    /// <inheritdoc />
    public void ReportStatus(string message)
    {
        InvokeOnMainThread(() =>
        {
            if (_statusLabel != null)
            {
                _statusLabel.Text = message;
            }

            // Set indeterminate state
            if (_progressBar != null)
            {
                _progressBar.Fraction = 0;
            }
        });
    }

    /// <inheritdoc />
    public void ReportProgress(int current, int total, string? message = null)
    {
        if (total <= 0)
        {
            ReportStatus(message ?? "Processing...");
            return;
        }

        var fraction = (float)current / total;
        ReportProgress(fraction, message);
    }

    /// <inheritdoc />
    public void ReportProgress(double fraction, string? message = null)
    {
        InvokeOnMainThread(() =>
        {
            if (_progressBar != null)
            {
                _progressBar.Fraction = (float)Math.Clamp(fraction, 0.0, 1.0);
            }

            if (_statusLabel != null && !string.IsNullOrEmpty(message))
            {
                _statusLabel.Text = message;
            }
        });
    }

    /// <inheritdoc />
    public void ReportComplete(string message)
    {
        InvokeOnMainThread(() =>
        {
            if (_progressBar != null)
            {
                _progressBar.Fraction = 1.0f;
            }

            if (_statusLabel != null)
            {
                _statusLabel.Text = message;
            }
        });
    }

    /// <inheritdoc />
    public void ReportError(string message)
    {
        InvokeOnMainThread(() =>
        {
            if (_statusLabel != null)
            {
                _statusLabel.Text = $"Error: {message}";
            }
        });
    }

    /// <summary>
    /// Invokes an action on the Terminal.Gui main thread.
    /// </summary>
    private static void InvokeOnMainThread(Action action)
    {
        if (Application.MainLoop != null)
        {
            Application.MainLoop.Invoke(action);
        }
        else
        {
            // Fallback if called outside of TUI context
            action();
        }
    }
}
