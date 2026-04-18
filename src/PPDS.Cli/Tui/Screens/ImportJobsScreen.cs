using System.Xml.Linq;
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
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
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

            var result = await service.ListAsync(cancellationToken: ScreenCancellation);
            _jobs = result.Items.ToList();

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
                var statusParts = new List<string> { $"{_jobs.Count} import job{(_jobs.Count != 1 ? "s" : "")}" };
                if (succeeded > 0) statusParts.Add($"{succeeded} succeeded");
                if (failed > 0) statusParts.Add($"{failed} failed");
                if (inProgress > 0) statusParts.Add($"{inProgress} in progress");
                _statusLabel.Text = string.Join(" \u2014 ", statusParts);
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
            var displayText = FormatImportJobData(data);

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
                    Text = displayText
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

    /// <summary>
    /// Pretty-prints import job XML data for display in the detail dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Dataverse <c>importjob.data</c> attribute returns XML on a single line,
    /// which is unreadable in the TUI text view (#763). This method attempts to
    /// parse the value as XML and re-serialize it with standard whitespace
    /// formatting via <see cref="XDocument.Parse(string, LoadOptions)"/>.
    /// </para>
    /// <para>
    /// <see cref="LoadOptions.PreserveWhitespace"/> is intentionally <em>not</em>
    /// used — we want whitespace reflow. CDATA and XML comments are preserved by
    /// <see cref="XDocument"/>'s object model and round-trip through <c>ToString()</c>.
    /// </para>
    /// <para>
    /// If the value is not well-formed XML (or is empty/null), the raw string is
    /// returned unmodified so the user at least sees something. A null input is
    /// surfaced as a human-readable placeholder.
    /// </para>
    /// </remarks>
    /// <param name="rawXml">The raw <c>importjob.data</c> value (may be null/empty).</param>
    /// <returns>Formatted text suitable for a <see cref="TextView"/>.</returns>
    internal static string FormatImportJobData(string? rawXml)
    {
        if (string.IsNullOrWhiteSpace(rawXml))
        {
            return "(No import log data available)";
        }

        try
        {
            // LoadOptions.None → default whitespace handling (reflow on ToString()).
            var doc = XDocument.Parse(rawXml, LoadOptions.None);
            return doc.ToString(SaveOptions.None);
        }
        catch (System.Xml.XmlException)
        {
            // Malformed XML — fall back to raw so the user can still inspect it.
            return rawXml;
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
