using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for viewing import jobs.
/// </summary>
internal sealed class ImportJobsScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private List<ImportJobInfo> _jobs = [];
    private Dialog? _detailDialog;

    public override string Title => "Import Jobs";

    public ImportJobsScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        _table = new TableView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            FullRowSelect = true,
            Style = { ShowHorizontalHeaderOverline = false, ShowHorizontalHeaderUnderline = true }
        };

        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(_table),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Loading...",
            ColorScheme = TuiColorPalette.Default
        };

        _table.CellActivated += OnCellActivated;

        Content.Add(_table, _statusLabel);

        // Kick off initial data load (fire-and-forget to avoid async-in-constructor)
        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(LoadDataAsync(), "ImportJobs.InitialLoad");
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () => ErrorService.FireAndForget(LoadDataAsync(), "ImportJobs.Refresh"));
        RegisterHotkey(registry, Key.CtrlMask | Key.O, "Open in Maker", OpenInMaker);
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _statusLabel.Text = "Loading import jobs...";
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IImportJobService>();

            _jobs = await service.ListAsync(top: 50, cancellationToken: ScreenCancellation);

            var dt = new System.Data.DataTable();
            dt.Columns.Add("Solution", typeof(string));
            dt.Columns.Add("Status", typeof(string));
            dt.Columns.Add("Progress", typeof(string));
            dt.Columns.Add("Created By", typeof(string));
            dt.Columns.Add("Created On", typeof(string));
            dt.Columns.Add("Duration", typeof(string));

            foreach (var job in _jobs)
            {
                dt.Rows.Add(
                    job.SolutionName ?? "\u2014",
                    job.Status,
                    $"{job.Progress:F0}%",
                    job.CreatedByName ?? "\u2014",
                    job.CreatedOn?.ToString("g") ?? "\u2014",
                    job.FormattedDuration ?? "\u2014");
            }

            Application.MainLoop.Invoke(() =>
            {
                _table.Table = dt;
                var succeeded = _jobs.Count(j => j.Status == "Succeeded");
                var failed = _jobs.Count(j => j.Status == "Failed");
                var inProgress = _jobs.Count(j => j.Status == "In Progress");
                _statusLabel.Text = $"{_jobs.Count} import job{(_jobs.Count != 1 ? "s" : "")} \u2014 {succeeded} succeeded, {failed} failed, {inProgress} in progress";
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load import jobs", ex, "ImportJobs.Load");
                _statusLabel.Text = "Error loading import jobs";
            });
        }
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (args.Row < 0 || args.Row >= _jobs.Count) return;
        var job = _jobs[args.Row];
        ErrorService.FireAndForget(ShowDetailDialogAsync(job), "ImportJobs.ShowDetail");
    }

    private async Task ShowDetailDialogAsync(ImportJobInfo job)
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IImportJobService>();

            var data = await service.GetDataAsync(job.Id, ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                _detailDialog?.Dispose();

                var textView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    Text = data ?? "(No import log data available)"
                };

                _detailDialog = new Dialog(
                    $"Import Log: {job.SolutionName ?? "Unknown"}",
                    new Button("Close", is_default: true))
                {
                    Width = Dim.Percent(80),
                    Height = Dim.Percent(80)
                };
                _detailDialog.Add(textView);
                Application.Run(_detailDialog);
                _detailDialog.Dispose();
                _detailDialog = null;
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load import log", ex, "ImportJobs.Detail");
            });
        }
    }

    private void OpenInMaker()
    {
        if (EnvironmentUrl == null)
        {
            ErrorService.ReportError("No environment URL available");
            return;
        }
        var dialog = new Dialog("Open in Maker", new Button("OK", is_default: true))
        {
            Width = 60,
            Height = 7
        };
        dialog.Add(new Label { X = 1, Y = 1, Text = "Open this URL in your browser:" });
        dialog.Add(new Label { X = 1, Y = 2, Text = EnvironmentUrl + "/solutionsHistory" });
        Application.Run(dialog);
        dialog.Dispose();
    }

    protected override void OnDispose()
    {
        _table.CellActivated -= OnCellActivated;
        _detailDialog?.Dispose();
    }
}
