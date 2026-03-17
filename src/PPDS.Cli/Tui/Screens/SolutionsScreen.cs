using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for browsing Dataverse solutions and their components.
/// </summary>
internal sealed class SolutionsScreen : TuiScreenBase, ITuiStateCapture<SolutionsScreenState>
{
    private readonly TextField _filterField;
    private readonly CheckBox _managedCheckBox;
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private readonly FrameView _filterFrame;

    private List<SolutionInfo> _allSolutions = [];
    private List<SolutionInfo> _filteredSolutions = [];
    private int? _lastComponentCount;
    private bool _isLoading;
    private string? _errorMessage;
    private Dialog? _detailDialog;

    public override string Title => "Solutions";

    public SolutionsScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        _filterFrame = new FrameView("Filter")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 3
        };

        _filterField = new TextField("")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(20)
        };

        _managedCheckBox = new CheckBox("Include Managed", true)
        {
            X = Pos.Right(_filterField) + 1,
            Y = 0
        };

        _filterFrame.Add(_filterField, _managedCheckBox);

        _table = new TableView
        {
            X = 0,
            Y = Pos.Bottom(_filterFrame),
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

        _filterField.TextChanged += OnFilterTextChanged;
        _managedCheckBox.Toggled += OnManagedToggled;
        _table.CellActivated += OnCellActivated;

        Content.Add(_filterFrame, _table, _statusLabel);

        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(LoadDataAsync(), "Solutions.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () => ErrorService.FireAndForget(LoadDataAsync(), "Solutions.Refresh"));
        RegisterHotkey(registry, Key.CtrlMask | Key.O, "Open in Maker", OpenInMaker);
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _isLoading = true;
            _errorMessage = null;
            _statusLabel.Text = "Loading solutions...";
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<ISolutionService>();

            var includeManaged = _managedCheckBox.Checked;
            _allSolutions = await service.ListAsync(
                filter: null,
                includeManaged: includeManaged,
                cancellationToken: ScreenCancellation);

            _isLoading = false;
            ApplyClientFilter();
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            _isLoading = false;
            Application.MainLoop.Invoke(() =>
            {
                _errorMessage = ex.Message;
                ErrorService.ReportError("Failed to load solutions", ex, "Solutions.Load");
                _statusLabel.Text = "Error loading solutions";
            });
        }
    }

    private void ApplyClientFilter()
    {
        var filterText = _filterField.Text?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(filterText))
        {
            _filteredSolutions = new List<SolutionInfo>(_allSolutions);
        }
        else
        {
            _filteredSolutions = _allSolutions
                .Where(s => s.FriendlyName.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                    || s.UniqueName.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var dt = new System.Data.DataTable();
        dt.Columns.Add("Name", typeof(string));
        dt.Columns.Add("Unique Name", typeof(string));
        dt.Columns.Add("Version", typeof(string));
        dt.Columns.Add("Publisher", typeof(string));
        dt.Columns.Add("Managed", typeof(string));
        dt.Columns.Add("Modified", typeof(string));

        foreach (var sol in _filteredSolutions)
        {
            dt.Rows.Add(
                sol.FriendlyName,
                sol.UniqueName,
                sol.Version ?? "\u2014",
                sol.PublisherName ?? "\u2014",
                sol.IsManaged ? "Yes" : "No",
                sol.ModifiedOn?.ToString("g") ?? "\u2014");
        }

        Application.MainLoop.Invoke(() =>
        {
            _table.Table = dt;
            var unmanaged = _filteredSolutions.Count(s => !s.IsManaged);
            _statusLabel.Text = $"{_filteredSolutions.Count} solution{(_filteredSolutions.Count != 1 ? "s" : "")} \u2014 {unmanaged} unmanaged";
        });
    }

    private void OnFilterTextChanged(NStack.ustring _)
    {
        ApplyClientFilter();
    }

    private void OnManagedToggled(bool _)
    {
        ErrorService.FireAndForget(LoadDataAsync(), "Solutions.ManagedToggle");
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (args.Row < 0 || args.Row >= _filteredSolutions.Count) return;
        var solution = _filteredSolutions[args.Row];
        ErrorService.FireAndForget(ShowComponentsDialogAsync(solution), "Solutions.ShowComponents");
    }

    private async Task ShowComponentsDialogAsync(SolutionInfo solution)
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<ISolutionService>();

            var components = await service.GetComponentsAsync(solution.Id, cancellationToken: ScreenCancellation);
            _lastComponentCount = components.Count;

            var grouped = components
                .GroupBy(c => c.ComponentTypeName)
                .OrderBy(g => g.Key)
                .ToList();

            var lines = new List<string>
            {
                $"Solution: {solution.FriendlyName} ({solution.UniqueName})",
                $"Version: {solution.Version ?? "\u2014"}",
                $"Total Components: {components.Count}",
                ""
            };

            foreach (var group in grouped)
            {
                lines.Add($"--- {group.Key} ({group.Count()}) ---");
                foreach (var comp in group.OrderBy(c => c.DisplayName ?? c.LogicalName ?? c.ObjectId.ToString()))
                {
                    var name = comp.DisplayName ?? comp.LogicalName ?? comp.SchemaName ?? comp.ObjectId.ToString();
                    lines.Add($"  {name}");
                }
                lines.Add("");
            }

            var text = string.Join(Environment.NewLine, lines);

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
                    Text = text
                };

                _detailDialog = new Dialog(
                    $"Components: {solution.FriendlyName}",
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
                ErrorService.ReportError("Failed to load solution components", ex, "Solutions.Components");
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
        dialog.Add(new Label { X = 1, Y = 2, Text = EnvironmentUrl + "/solutions" });
        Application.Run(dialog);
        dialog.Dispose();
    }

    /// <inheritdoc />
    public SolutionsScreenState CaptureState()
    {
        string? selectedName = null;
        string? selectedVersion = null;
        bool? selectedIsManaged = null;

        var selectedRow = _table.SelectedRow;
        if (selectedRow >= 0 && selectedRow < _filteredSolutions.Count)
        {
            var sol = _filteredSolutions[selectedRow];
            selectedName = sol.FriendlyName;
            selectedVersion = sol.Version;
            selectedIsManaged = sol.IsManaged;
        }

        return new SolutionsScreenState(
            SolutionCount: _filteredSolutions.Count,
            SelectedSolutionName: selectedName,
            SelectedSolutionVersion: selectedVersion,
            SelectedIsManaged: selectedIsManaged,
            ComponentCount: _lastComponentCount,
            IsLoading: _isLoading,
            ShowManaged: _managedCheckBox.Checked,
            FilterText: _filterField.Text?.ToString() ?? "",
            ErrorMessage: _errorMessage);
    }

    protected override void OnDispose()
    {
        _filterField.TextChanged -= OnFilterTextChanged;
        _managedCheckBox.Toggled -= OnManagedToggled;
        _table.CellActivated -= OnCellActivated;
        _detailDialog?.Dispose();
    }
}
