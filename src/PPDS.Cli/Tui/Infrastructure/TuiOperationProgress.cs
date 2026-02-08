using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tui.Views;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Infrastructure;

/// <summary>
/// Terminal.Gui adapter for <see cref="IOperationProgress"/>.
/// Updates a ProgressBar, Label, and/or TuiSpinner on the main UI thread.
/// </summary>
/// <remarks>
/// <para>
/// When a <see cref="TuiSpinner"/> is provided, status messages will animate
/// with a braille spinner to indicate ongoing activity. The spinner is stopped
/// automatically when <see cref="ReportComplete"/> or <see cref="ReportError"/> is called.
/// </para>
/// </remarks>
public sealed class TuiOperationProgress : IOperationProgress
{
    private readonly ProgressBar? _progressBar;
    private readonly Label? _statusLabel;
    private readonly TuiSpinner? _spinner;

    /// <summary>
    /// Creates a progress reporter that updates a ProgressBar.
    /// </summary>
    /// <param name="progressBar">The progress bar to update (optional).</param>
    /// <param name="statusLabel">The label for status messages (optional).</param>
    public TuiOperationProgress(ProgressBar? progressBar = null, Label? statusLabel = null)
        : this(progressBar, statusLabel, spinner: null)
    {
    }

    /// <summary>
    /// Creates a progress reporter with optional spinner animation.
    /// </summary>
    /// <param name="progressBar">The progress bar to update (optional).</param>
    /// <param name="statusLabel">The label for status messages (optional).</param>
    /// <param name="spinner">The spinner for animated status (optional).</param>
    public TuiOperationProgress(ProgressBar? progressBar, Label? statusLabel, TuiSpinner? spinner)
    {
        _progressBar = progressBar;
        _statusLabel = statusLabel;
        _spinner = spinner;
    }

    /// <inheritdoc />
    public void ReportStatus(string message)
    {
        InvokeOnMainThread(() =>
        {
            // Start spinner if available
            if (_spinner != null)
            {
                _spinner.Start(message);
            }
            else if (_statusLabel != null)
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
            // Stop spinner and show final message
            if (_spinner != null)
            {
                _spinner.StopWithMessage(message);
            }
            else if (_statusLabel != null)
            {
                _statusLabel.Text = message;
            }

            if (_progressBar != null)
            {
                _progressBar.Fraction = 1.0f;
            }
        });
    }

    /// <inheritdoc />
    public void ReportError(string message)
    {
        InvokeOnMainThread(() =>
        {
            // Stop spinner and show error
            if (_spinner != null)
            {
                _spinner.StopWithMessage($"Error: {message}");
            }
            else if (_statusLabel != null)
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
