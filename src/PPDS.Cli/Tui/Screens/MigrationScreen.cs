using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Migration.Analysis;
using PPDS.Migration.Export;
using PPDS.Migration.Formats;
using PPDS.Migration.Import;
using PPDS.Migration.Models;
using PPDS.Migration.Progress;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

internal sealed class MigrationScreen : TuiScreenBase, ITuiStateCapture<MigrationScreenState>
{
    private readonly RadioGroup _modeRadio;
    private readonly FrameView _configFrame;
    private readonly Label _schemaPathLabel;
    private readonly TextField _schemaPathField;
    private readonly Label _schemaPathHint;
    private readonly Label _outputPathLabel;
    private readonly TextField _outputPathField;
    private readonly Label _outputPathHint;
    private readonly Label _dataPathLabel;
    private readonly TextField _dataPathField;
    private readonly Label _dataPathHint;
    private readonly CheckBox _resolveLookupsCheckBox;
    private readonly CheckBox _skipUnresolvedLookupsCheckBox;
    private readonly CheckBox _impersonateOwnersCheckBox;
    private readonly CheckBox _continueOnErrorCheckBox;
    private readonly CheckBox _skipMissingColumnsCheckBox;
    private readonly Label _importModeLabel;
    private readonly RadioGroup _importModeRadio;
    private readonly Label _exportDescriptionLabel;
    private readonly FrameView _progressFrame;
    private readonly Label _phaseLabel;
    private readonly Label _entityLabel;
    private readonly ProgressBar _progressBar;
    private readonly Label _rateLabel;
    private readonly Label _statusLabel;
    private readonly Label _resultsLabel;

    private MigrationMode _mode = MigrationMode.Export;
    private MigrationOperationState _operationState = MigrationOperationState.Idle;
    private CancellationTokenSource? _operationCts;
    private readonly Stopwatch _elapsed = new();

    private int _recordCount;
    private int _successCount;
    private int _failureCount;
    private int _warningCount;
    private int _skipCount;
    private double? _recordsPerSecond;
    private string? _estimatedRemaining;
    private string? _errorMessage;

    public override string Title => EnvironmentDisplayName != null
        ? $"Data Migration - {EnvironmentDisplayName}"
        : "Data Migration";

    public MigrationScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        // QA-CRIT-2: Use "E_xport" (Alt+X) to avoid conflict with global Alt+E (environment picker in TuiShell).
        // QA-CRIT-1: "Import" keeps Alt+I; the impersonate checkbox below uses a different hotkey.
        _modeRadio = new RadioGroup(new NStack.ustring[] { "E_xport", "_Import" })
        {
            X = 1,
            Y = 0,
            Width = 20,
            Height = 1,
            CanFocus = true,
            TabStop = true, // QA-IMP-1: Ensure radio group participates in Tab focus cycle
            DisplayMode = DisplayModeLayout.Horizontal
        };
        _modeRadio.SelectedItemChanged += OnModeChanged;

        // QA-IMP-4: Add keyboard shortcut hints to the frame title, following the SQL Query screen pattern.
        _configFrame = new FrameView("Configuration (Ctrl+Enter to start, Ctrl+P to preview plan)")
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = 10,
            TabStop = true // QA-IMP-1: Ensure FrameView participates in Tab focus cycle
        };

        _schemaPathLabel = new Label("Schema path:") { X = 1, Y = 0 };
        _schemaPathField = new TextField("") { X = 15, Y = 0, Width = Dim.Fill(1) };
        _schemaPathHint = new Label("e.g., ./data_schema.xml") { X = 15, Y = 0, Width = Dim.Fill(1), ColorScheme = TuiColorPalette.Dimmed };
        _outputPathLabel = new Label("Output path:") { X = 1, Y = 1 };
        _outputPathField = new TextField("") { X = 15, Y = 1, Width = Dim.Fill(1) };
        _outputPathHint = new Label("e.g., ./export.zip") { X = 15, Y = 1, Width = Dim.Fill(1), ColorScheme = TuiColorPalette.Dimmed };

        _dataPathLabel = new Label("Data path:") { X = 1, Y = 0 };
        _dataPathField = new TextField("") { X = 15, Y = 0, Width = Dim.Fill(1) };
        _dataPathHint = new Label("e.g., ./data.zip") { X = 15, Y = 0, Width = Dim.Fill(1), ColorScheme = TuiColorPalette.Dimmed };
        _importModeLabel = new Label("Mode:") { X = 1, Y = 2 };
        _importModeRadio = new RadioGroup(new NStack.ustring[] { "Create", "Update", "Upsert", "Skip" })
        {
            X = 15,
            Y = 2,
            TabStop = true, // QA-IMP-1: Ensure radio group participates in Tab focus cycle
            DisplayMode = DisplayModeLayout.Horizontal
        };
        _importModeRadio.SelectedItem = 2;

        // QA-IMP-1: Ensure all checkboxes have TabStop = true so they participate in Tab focus cycle.
        // QA-CRIT-1: "Im_personate owners" uses Alt+P to avoid conflict with "Import" (Alt+I).
        //   Alt+P does not conflict with global hotkeys because TuiShell registers Alt+P via
        //   HotkeyRegistry (not as a label hotkey), and screen-level label hotkeys take precedence
        //   when the screen content is focused.
        _resolveLookupsCheckBox = new CheckBox("Resolve external lookups") { X = 1, Y = 4, TabStop = true };
        _skipUnresolvedLookupsCheckBox = new CheckBox("Skip unresolved lookups", true) { X = 1, Y = 5, TabStop = true };
        _impersonateOwnersCheckBox = new CheckBox("Im_personate owners") { X = 1, Y = 6, TabStop = true };
        _continueOnErrorCheckBox = new CheckBox("Continue on error", true) { X = 1, Y = 7, TabStop = true };
        _skipMissingColumnsCheckBox = new CheckBox("Skip missing columns") { X = 40, Y = 4, TabStop = true };

        // QA-IMP-5: Description label shown in export mode to fill the empty space below the two fields.
        _exportDescriptionLabel = new Label(
            "Export uses a schema file to define which entities and columns\n" +
            "to extract from Dataverse. The output is a portable data package\n" +
            "(.zip) that can be imported into another environment.\n\n" +
            "Provide the schema XML and an output path, then press Ctrl+Enter.")
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill(1),
            Height = 5,
            ColorScheme = TuiColorPalette.Dimmed
        };

        // QA-IMP-3: Wire hint label visibility — show hint when field is empty, hide when it has text.
        // Terminal.Gui v1 TextField does not support built-in placeholder text, so we overlay a dimmed
        // Label that is hidden once the user types anything.
        WireFieldHint(_schemaPathField, _schemaPathHint);
        WireFieldHint(_outputPathField, _outputPathHint);
        WireFieldHint(_dataPathField, _dataPathHint);

        // QA-IMP-2: Terminal.Gui v1 does not provide a distinct visual focus indicator between text
        // fields in PTY mode — the cursor position is the only cue. Assigning TextInput ColorScheme
        // gives a DarkGray background on focus, which helps in terminals that support it. This is a
        // known framework limitation and cannot be fully resolved without upstream changes.
        _schemaPathField.ColorScheme = TuiColorPalette.TextInput;
        _outputPathField.ColorScheme = TuiColorPalette.TextInput;
        _dataPathField.ColorScheme = TuiColorPalette.TextInput;

        _progressFrame = new FrameView("Progress")
        {
            X = 0,
            Y = Pos.Bottom(_configFrame),
            Width = Dim.Fill(),
            Height = 6
        };

        _phaseLabel = new Label("") { X = 1, Y = 0, Width = Dim.Fill(1) };
        _entityLabel = new Label("") { X = 1, Y = 1, Width = Dim.Fill(1) };
        _progressBar = new ProgressBar { X = 1, Y = 2, Width = Dim.Fill(1) };
        _rateLabel = new Label("") { X = 1, Y = 3, Width = Dim.Fill(1) };
        _progressFrame.Add(_phaseLabel, _entityLabel, _progressBar, _rateLabel);

        _statusLabel = new Label("")
        {
            X = 0,
            Y = Pos.Bottom(_progressFrame),
            Width = Dim.Fill(),
            Height = 1
        };
        _resultsLabel = new Label("")
        {
            X = 0,
            Y = Pos.Bottom(_statusLabel),
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        ShowExportConfig();

        Content.Add(_modeRadio, _configFrame, _progressFrame, _statusLabel, _resultsLabel);

        if (EnvironmentUrl == null)
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
        else
        {
            _statusLabel.Text = "Ready. Configure options and press Ctrl+Enter to start.";
            _operationState = MigrationOperationState.Configuring;
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.Enter, "Start operation",
            () => ErrorService.FireAndForget(StartOperationAsync(), "Migration.Start"));
        RegisterHotkey(registry, Key.CtrlMask | Key.P, "Preview execution plan",
            () => ErrorService.FireAndForget(PreviewPlanAsync(), "Migration.PreviewPlan"));
        RegisterHotkey(registry, Key.Esc, "Cancel operation", () =>
        {
            if (_operationState == MigrationOperationState.Running)
                ConfirmCancel();
        });
    }

    private void OnModeChanged(SelectedItemChangedArgs args)
    {
        if (_operationState == MigrationOperationState.Running)
            return;

        _mode = args.SelectedItem == 0 ? MigrationMode.Export : MigrationMode.Import;

        if (_mode == MigrationMode.Export)
            ShowExportConfig();
        else
            ShowImportConfig();
    }

    private void ShowExportConfig()
    {
        _configFrame.RemoveAll();
        // QA-IMP-5: Include the export description to fill the empty space below the two fields.
        _configFrame.Add(
            _schemaPathLabel, _schemaPathField, _schemaPathHint,
            _outputPathLabel, _outputPathField, _outputPathHint,
            _exportDescriptionLabel);
        // Frame height stays at 10 — the description fills the space that import checkboxes would use.
        _configFrame.Height = 10;
        UpdateHintVisibility(_schemaPathField, _schemaPathHint);
        UpdateHintVisibility(_outputPathField, _outputPathHint);
    }

    private void ShowImportConfig()
    {
        _configFrame.RemoveAll();
        _configFrame.Add(
            _dataPathLabel, _dataPathField, _dataPathHint,
            _importModeLabel, _importModeRadio,
            _resolveLookupsCheckBox, _skipUnresolvedLookupsCheckBox,
            _impersonateOwnersCheckBox, _continueOnErrorCheckBox,
            _skipMissingColumnsCheckBox);
        _configFrame.Height = 10;
        UpdateHintVisibility(_dataPathField, _dataPathHint);
    }

    /// <summary>
    /// Wires a TextField to show/hide a hint label based on whether the field has text.
    /// </summary>
    private static void WireFieldHint(TextField field, Label hint)
    {
        field.TextChanged += _ => UpdateHintVisibility(field, hint);
    }

    private static void UpdateHintVisibility(TextField field, Label hint)
    {
        var text = field.Text?.ToString();
        hint.Visible = string.IsNullOrEmpty(text);
    }

    private async Task StartOperationAsync()
    {
        if (EnvironmentUrl == null || _operationState == MigrationOperationState.Running)
            return;

        if (_mode == MigrationMode.Export)
        {
            var schemaPath = _schemaPathField.Text?.ToString()?.Trim();
            var outputPath = _outputPathField.Text?.ToString()?.Trim();
            if (string.IsNullOrEmpty(schemaPath) || string.IsNullOrEmpty(outputPath))
            {
                _statusLabel.Text = "Schema path and output path are required.";
                return;
            }
            await RunExportAsync(schemaPath, outputPath);
        }
        else
        {
            var dataPath = _dataPathField.Text?.ToString()?.Trim();
            if (string.IsNullOrEmpty(dataPath))
            {
                _statusLabel.Text = "Data path is required.";
                return;
            }
            await RunImportAsync(dataPath);
        }
    }

    private async Task RunExportAsync(string schemaPath, string outputPath)
    {
        BeginOperation();
        var reporter = CreateProgressReporter();

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var exporter = provider.GetRequiredService<IExporter>();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ScreenCancellation, _operationCts!.Token);

            reporter.OperationName = "Export";
            var result = await exporter.ExportAsync(schemaPath, outputPath, progress: reporter,
                cancellationToken: linkedCts.Token);

            Application.MainLoop?.Invoke(() =>
            {
                _recordCount = result.RecordsExported;
                _successCount = result.RecordsExported;
                _failureCount = result.Errors.Count;
                _recordsPerSecond = result.RecordsPerSecond;
                _resultsLabel.Text = FormatExportResults(result);

                if (linkedCts.IsCancellationRequested)
                    CompleteOperation(MigrationOperationState.Cancelled);
                else if (result.Success)
                    CompleteOperation(MigrationOperationState.Completed);
                else
                    CompleteOperation(MigrationOperationState.Failed, "Export completed with errors.");
            });
        }
        catch (OperationCanceledException)
        {
            Application.MainLoop?.Invoke(() => CompleteOperation(MigrationOperationState.Cancelled));
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Export failed", ex, "Migration.Export");
                CompleteOperation(MigrationOperationState.Failed, ex.Message);
            });
        }
    }

    private async Task RunImportAsync(string dataPath)
    {
        BeginOperation();
        var reporter = CreateProgressReporter();

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var importer = provider.GetRequiredService<IImporter>();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                ScreenCancellation, _operationCts!.Token);

            var importMode = _importModeRadio.SelectedItem switch
            {
                0 => ImportMode.Create,
                1 => ImportMode.Update,
                2 => ImportMode.Upsert,
                3 => ImportMode.Skip,
                _ => ImportMode.Upsert
            };

            var options = new ImportOptions
            {
                Mode = importMode,
                ResolveExternalLookups = _resolveLookupsCheckBox.Checked,
                SkipUnresolvedLookups = _skipUnresolvedLookupsCheckBox.Checked,
                ImpersonateOwners = _impersonateOwnersCheckBox.Checked,
                ContinueOnError = _continueOnErrorCheckBox.Checked,
                SkipMissingColumns = _skipMissingColumnsCheckBox.Checked
            };

            reporter.OperationName = "Import";
            var result = await importer.ImportAsync(dataPath, options, reporter, linkedCts.Token);

            Application.MainLoop?.Invoke(() =>
            {
                _recordCount = result.RecordsImported;
                _successCount = result.EntityResults.Sum(e => e.SuccessCount);
                _failureCount = result.EntityResults.Sum(e => e.FailureCount);
                _warningCount = result.Warnings.Count;
                _recordsPerSecond = result.RecordsPerSecond;
                _resultsLabel.Text = FormatImportResults(result);

                if (linkedCts.IsCancellationRequested)
                    CompleteOperation(MigrationOperationState.Cancelled);
                else if (result.Success)
                    CompleteOperation(MigrationOperationState.Completed);
                else
                    CompleteOperation(MigrationOperationState.Failed, "Import completed with errors.");
            });
        }
        catch (OperationCanceledException)
        {
            Application.MainLoop?.Invoke(() => CompleteOperation(MigrationOperationState.Cancelled));
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Import failed", ex, "Migration.Import");
                CompleteOperation(MigrationOperationState.Failed, ex.Message);
            });
        }
    }

    private async Task PreviewPlanAsync()
    {
        if (EnvironmentUrl == null || _mode != MigrationMode.Import)
            return;

        var dataPath = _dataPathField.Text?.ToString()?.Trim();
        if (string.IsNullOrEmpty(dataPath))
        {
            _statusLabel.Text = "Data path is required to preview execution plan.";
            return;
        }

        _operationState = MigrationOperationState.PreviewingPlan;
        _statusLabel.Text = "Building execution plan...";

        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var dataReader = provider.GetRequiredService<ICmtDataReader>();
            var graphBuilder = provider.GetRequiredService<IDependencyGraphBuilder>();
            var planBuilder = provider.GetRequiredService<IExecutionPlanBuilder>();

            var data = await dataReader.ReadAsync(dataPath, cancellationToken: ScreenCancellation);
            var graph = graphBuilder.Build(data.Schema);
            var plan = planBuilder.Build(graph, data.Schema);

            Application.MainLoop?.Invoke(() =>
            {
                var dialog = new ExecutionPlanPreviewDialog(plan);
                Application.Run(dialog);

                _operationState = MigrationOperationState.Configuring;
                _statusLabel.Text = dialog.IsApproved
                    ? "Plan approved. Press Ctrl+Enter to start import."
                    : "Plan preview cancelled.";
            });
        }
        catch (OperationCanceledException)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _operationState = MigrationOperationState.Configuring;
                _statusLabel.Text = "Plan preview cancelled.";
            });
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to build execution plan", ex, "Migration.PreviewPlan");
                _operationState = MigrationOperationState.Configuring;
                _statusLabel.Text = "Error building execution plan.";
            });
        }
    }

    private void ConfirmCancel()
    {
        var n = MessageBox.Query("Cancel Operation",
            "Cancel the running operation? The current batch will complete before stopping.", "Yes", "No");
        if (n == 0)
        {
            _operationCts?.Cancel();
            _statusLabel.Text = "Cancelling...";
        }
    }

    private void BeginOperation()
    {
        _operationState = MigrationOperationState.Running;
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        _elapsed.Restart();

        _recordCount = 0;
        _successCount = 0;
        _failureCount = 0;
        _warningCount = 0;
        _skipCount = 0;
        _recordsPerSecond = null;
        _estimatedRemaining = null;
        _errorMessage = null;

        _phaseLabel.Text = "";
        _entityLabel.Text = "";
        _progressBar.Fraction = 0f;
        _rateLabel.Text = "";
        _resultsLabel.Text = "";
        _statusLabel.Text = $"{_mode} in progress...";
    }

    private void CompleteOperation(MigrationOperationState state, string? error = null)
    {
        _elapsed.Stop();
        _operationState = state;
        _errorMessage = error;

        _statusLabel.Text = state switch
        {
            MigrationOperationState.Completed => $"{_mode} completed successfully in {FormatTimeSpan(_elapsed.Elapsed)}.",
            MigrationOperationState.Failed => $"{_mode} failed: {error}",
            MigrationOperationState.Cancelled => $"{_mode} cancelled after {FormatTimeSpan(_elapsed.Elapsed)}.",
            _ => ""
        };
    }

    private TuiMigrationProgressReporter CreateProgressReporter()
    {
        return new TuiMigrationProgressReporter(
            _phaseLabel, _entityLabel, _progressBar, _rateLabel, _statusLabel,
            onComplete: result =>
            {
                _recordCount = result.RecordsProcessed;
                _successCount = result.SuccessCount;
                _failureCount = result.FailureCount;
                _recordsPerSecond = result.RecordsPerSecond;
            },
            onError: (ex, context) =>
            {
                _errorMessage = context != null ? $"{context}: {ex.Message}" : ex.Message;
            });
    }

    private static string FormatExportResults(ExportResult result)
    {
        var lines = new List<string>
        {
            $"Exported {result.RecordsExported} records from {result.EntitiesExported} entities in {FormatTimeSpan(result.Duration)} ({result.RecordsPerSecond:F0} rec/s)",
            ""
        };

        foreach (var entity in result.EntityResults)
        {
            var status = entity.Success ? "OK" : $"FAIL: {entity.ErrorMessage}";
            lines.Add($"  {entity.EntityLogicalName}: {entity.RecordCount} records [{status}]");
        }

        if (result.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add($"Errors ({result.Errors.Count}):");
            foreach (var err in result.Errors.Take(10))
                lines.Add($"  {err.EntityLogicalName}: {err.Message}");
            if (result.Errors.Count > 10)
                lines.Add($"  ... and {result.Errors.Count - 10} more");
        }

        return string.Join("\n", lines);
    }

    private static string FormatImportResults(ImportResult result)
    {
        var lines = new List<string>
        {
            $"Imported {result.RecordsImported} of {result.SourceRecordCount} records in {FormatTimeSpan(result.Duration)} ({result.RecordsPerSecond:F0} rec/s)",
            $"Tiers: {result.TiersProcessed}  |  M2M: {result.RelationshipsProcessed}  |  Deferred: {result.RecordsUpdated}",
            ""
        };

        foreach (var entity in result.EntityResults)
        {
            var status = entity.Success ? "OK" : "FAIL";
            lines.Add($"  {entity.EntityLogicalName} (tier {entity.TierNumber}): {entity.SuccessCount}/{entity.RecordCount} [{status}]");
        }

        if (result.Warnings.Count > 0)
        {
            lines.Add("");
            lines.Add($"Warnings ({result.Warnings.Count}):");
            foreach (var w in result.Warnings.Take(5))
                lines.Add($"  {w.Message}");
            if (result.Warnings.Count > 5)
                lines.Add($"  ... and {result.Warnings.Count - 5} more");
        }

        if (result.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add($"Errors ({result.Errors.Count}):");
            foreach (var err in result.Errors.Take(10))
                lines.Add($"  {err.EntityLogicalName}: {err.Message}");
            if (result.Errors.Count > 10)
                lines.Add($"  ... and {result.Errors.Count - 10} more");
        }

        return string.Join("\n", lines);
    }

    public MigrationScreenState CaptureState() => new(
        Mode: _mode.ToString(),
        OperationState: _operationState.ToString(),
        SchemaPath: _schemaPathField.Text?.ToString(),
        OutputPath: _outputPathField.Text?.ToString(),
        DataPath: _dataPathField.Text?.ToString(),
        CurrentPhase: _phaseLabel.Text?.ToString(),
        CurrentEntity: _entityLabel.Text?.ToString(),
        RecordCount: _recordCount,
        SuccessCount: _successCount,
        FailureCount: _failureCount,
        WarningCount: _warningCount,
        SkipCount: _skipCount,
        RecordsPerSecond: _recordsPerSecond,
        EstimatedRemaining: _estimatedRemaining,
        Elapsed: _elapsed.IsRunning ? FormatTimeSpan(_elapsed.Elapsed) : (_elapsed.ElapsedMilliseconds > 0 ? FormatTimeSpan(_elapsed.Elapsed) : null),
        ErrorMessage: _errorMessage,
        IsLoading: _operationState == MigrationOperationState.Running,
        StatusMessage: _statusLabel.Text?.ToString());

    protected override void OnDispose()
    {
        _modeRadio.SelectedItemChanged -= OnModeChanged;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        return $"{ts.Seconds}s";
    }
}

internal enum MigrationMode
{
    Export,
    Import
}

internal enum MigrationOperationState
{
    Idle,
    Configuring,
    PreviewingPlan,
    Running,
    Completed,
    Failed,
    Cancelled
}
