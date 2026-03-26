using System;
using PPDS.Migration.Progress;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// Adapts <see cref="IProgressReporter"/> to TUI controls.
/// All Report/Complete/Error calls are dispatched to the UI thread via
/// <see cref="Application.MainLoop"/>.Invoke() so Terminal.Gui views
/// can be updated safely from background migration threads.
/// </summary>
internal sealed class TuiMigrationProgressReporter : IProgressReporter
{
    private readonly Label _phaseLabel;
    private readonly Label _entityLabel;
    private readonly ProgressBar _progressBar;
    private readonly Label _rateLabel;
    private readonly Label _statusLabel;
    private readonly Action<MigrationResult>? _onComplete;
    private readonly Action<Exception, string?>? _onError;

    /// <inheritdoc />
    public string OperationName { get; set; } = "Migration";

    /// <summary>
    /// Creates a new TUI migration progress reporter.
    /// </summary>
    /// <param name="phaseLabel">Label showing the current phase.</param>
    /// <param name="entityLabel">Label showing the current entity name.</param>
    /// <param name="progressBar">Progress bar to update.</param>
    /// <param name="rateLabel">Label showing rate and ETA.</param>
    /// <param name="statusLabel">Label for overall status messages.</param>
    /// <param name="onComplete">Callback when operation completes.</param>
    /// <param name="onError">Callback when an error occurs.</param>
    public TuiMigrationProgressReporter(
        Label phaseLabel,
        Label entityLabel,
        ProgressBar progressBar,
        Label rateLabel,
        Label statusLabel,
        Action<MigrationResult>? onComplete = null,
        Action<Exception, string?>? onError = null)
    {
        _phaseLabel = phaseLabel ?? throw new ArgumentNullException(nameof(phaseLabel));
        _entityLabel = entityLabel ?? throw new ArgumentNullException(nameof(entityLabel));
        _progressBar = progressBar ?? throw new ArgumentNullException(nameof(progressBar));
        _rateLabel = rateLabel ?? throw new ArgumentNullException(nameof(rateLabel));
        _statusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
        _onComplete = onComplete;
        _onError = onError;
    }

    /// <inheritdoc />
    public void Report(ProgressEventArgs args)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _phaseLabel.Text = FormatPhase(args.Phase);

            var entityText = args.Entity ?? string.Empty;
            if (args.TierNumber.HasValue && args.TotalTiers.HasValue)
            {
                entityText = $"Tier {args.TierNumber}/{args.TotalTiers} - {entityText}";
            }
            _entityLabel.Text = entityText;

            // Use overall progress if available, otherwise per-entity progress
            if (args.OverallTotal > 0 && args.OverallProcessed.HasValue)
            {
                _progressBar.Fraction = (float)((double)args.OverallProcessed.Value / args.OverallTotal.Value);
            }
            else if (args.Total > 0)
            {
                _progressBar.Fraction = (float)((double)args.Current / args.Total);
            }

            var rateParts = new System.Collections.Generic.List<string>();
            if (args.RecordsPerSecond.HasValue && args.RecordsPerSecond.Value > 0)
            {
                rateParts.Add($"{args.RecordsPerSecond.Value:F0} rec/s");
            }
            if (args.EstimatedRemaining.HasValue)
            {
                rateParts.Add($"ETA: {FormatTimeSpan(args.EstimatedRemaining.Value)}");
            }
            if (args.SuccessCount > 0 || args.FailureCount > 0)
            {
                rateParts.Add($"{args.SuccessCount} ok / {args.FailureCount} fail");
            }
            _rateLabel.Text = string.Join("  |  ", rateParts);

            if (!string.IsNullOrEmpty(args.Message))
            {
                _statusLabel.Text = args.Message;
            }
        });
    }

    /// <inheritdoc />
    public void Complete(MigrationResult result)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _phaseLabel.Text = result.Success ? "Complete" : "Completed with errors";
            _progressBar.Fraction = 1.0f;
            _rateLabel.Text = $"{result.RecordsProcessed} records in {FormatTimeSpan(result.Duration)} ({result.RecordsPerSecond:F0} rec/s)";
            _statusLabel.Text = result.Success
                ? $"{OperationName} completed successfully."
                : $"{OperationName} completed with {result.FailureCount} error(s).";
            _onComplete?.Invoke(result);
        });
    }

    /// <inheritdoc />
    public void Error(Exception exception, string? context = null)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _phaseLabel.Text = "Error";
            _statusLabel.Text = context != null
                ? $"Error during {context}: {exception.Message}"
                : $"Error: {exception.Message}";
            _onError?.Invoke(exception, context);
        });
    }

    /// <inheritdoc />
    public void Reset()
    {
        Application.MainLoop?.Invoke(() =>
        {
            _phaseLabel.Text = string.Empty;
            _entityLabel.Text = string.Empty;
            _progressBar.Fraction = 0f;
            _rateLabel.Text = string.Empty;
        });
    }

    private static string FormatPhase(MigrationPhase phase) => phase switch
    {
        MigrationPhase.Analyzing => "Analyzing schema...",
        MigrationPhase.Exporting => "Exporting data...",
        MigrationPhase.Importing => "Importing records...",
        MigrationPhase.ProcessingDeferredFields => "Processing deferred fields...",
        MigrationPhase.ProcessingStateTransitions => "Processing state transitions...",
        MigrationPhase.ProcessingRelationships => "Processing M2M relationships...",
        MigrationPhase.Complete => "Complete",
        MigrationPhase.Error => "Error",
        _ => phase.ToString()
    };

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}
