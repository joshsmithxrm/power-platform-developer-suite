using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.History;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Cli.Tui.Testing;
using PPDS.Cli.Tui.Testing.States;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for browsing and selecting from query history.
/// </summary>
internal sealed class QueryHistoryDialog : TuiDialog, ITuiStateCapture<QueryHistoryDialogState>
{
    private readonly IQueryHistoryService _historyService;
    private readonly ITuiErrorService? _errorService;
    private readonly string _environmentUrl;
    private readonly ListView _listView;
    private readonly TextView _previewText;
    private readonly TextField _searchField;
    private readonly Label _statusLabel;

    private IReadOnlyList<QueryHistoryEntry> _entries = Array.Empty<QueryHistoryEntry>();
    private QueryHistoryEntry? _selectedEntry;

    /// <summary>
    /// Gets the selected history entry, or null if cancelled.
    /// </summary>
    public QueryHistoryEntry? SelectedEntry => _selectedEntry;

    /// <summary>
    /// Creates a new query history dialog.
    /// </summary>
    /// <param name="historyService">The query history service.</param>
    /// <param name="environmentUrl">The environment URL to load history for.</param>
    /// <param name="session">Optional session for hotkey registry integration.</param>
    public QueryHistoryDialog(IQueryHistoryService historyService, string environmentUrl, InteractiveSession? session = null)
        : base("Query History", session)
    {
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _environmentUrl = environmentUrl ?? throw new ArgumentNullException(nameof(environmentUrl));
        _errorService = session?.GetErrorService();

        Width = 80;
        Height = 22;

        // Search field
        var searchLabel = new Label("Search:")
        {
            X = 1,
            Y = 1
        };

        _searchField = new TextField
        {
            X = Pos.Right(searchLabel) + 1,
            Y = 1,
            Width = Dim.Fill() - 2,
            ColorScheme = TuiColorPalette.TextInput
        };
        _searchField.TextChanged += OnSearchChanged;

        // History list
        var listFrame = new FrameView("Recent Queries")
        {
            X = 1,
            Y = 3,
            Width = Dim.Fill() - 2,
            Height = 8
        };

        _listView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            AllowsMultipleSelection = false,
            ColorScheme = TuiColorPalette.Default
        };
        _listView.SelectedItemChanged += OnSelectedItemChanged;
        _listView.OpenSelectedItem += OnItemActivated;
        listFrame.Add(_listView);

        // Preview panel
        var previewFrame = new FrameView("Preview")
        {
            X = 1,
            Y = Pos.Bottom(listFrame),
            Width = Dim.Fill() - 2,
            Height = 5
        };

        _previewText = new TextView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true,
            WordWrap = true,
            ColorScheme = TuiColorPalette.ReadOnlyText
        };
        previewFrame.Add(_previewText);

        // Status label
        _statusLabel = new Label
        {
            X = 1,
            Y = Pos.Bottom(previewFrame),
            Width = Dim.Fill() - 2,
            Height = 1,
            Text = "Loading history..."
        };

        // Buttons
        var executeButton = new Button("_Run")
        {
            X = Pos.Center() - 20,
            Y = Pos.AnchorEnd(1)
        };
        executeButton.Clicked += OnExecuteClicked;

        var copyButton = new Button("_Copy")
        {
            X = Pos.Center() - 8,
            Y = Pos.AnchorEnd(1)
        };
        copyButton.Clicked += OnCopyClicked;

        var deleteButton = new Button("_Delete")
        {
            X = Pos.Center() + 5,
            Y = Pos.AnchorEnd(1)
        };
        deleteButton.Clicked += OnDeleteClicked;

        var cancelButton = new Button("Cancel")
        {
            X = Pos.Center() + 17,
            Y = Pos.AnchorEnd(1)
        };
        cancelButton.Clicked += () => { Application.RequestStop(); };

        Add(searchLabel, _searchField, listFrame, previewFrame, _statusLabel,
            executeButton, copyButton, deleteButton, cancelButton);

        _errorService?.FireAndForget(LoadHistoryAsync(null), "LoadHistory");
    }

    private async Task LoadHistoryAsync(string? searchPattern)
    {
        try
        {
            _entries = string.IsNullOrWhiteSpace(searchPattern)
                ? await _historyService.GetHistoryAsync(_environmentUrl, 50)
                : await _historyService.SearchHistoryAsync(_environmentUrl, searchPattern, 50);

            UpdateListView();
        }
        catch (PpdsException ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Error loading history: {ex.UserMessage}";
            });
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Error loading history: {ex.Message}";
            });
        }
    }

    private void UpdateListView()
    {
        Application.MainLoop?.Invoke(() =>
        {
            var items = new List<string>();

            foreach (var entry in _entries)
            {
                var time = entry.ExecutedAt.LocalDateTime.ToString("MM/dd HH:mm");
                var preview = entry.Sql.Replace('\n', ' ').Replace('\r', ' ');
                if (preview.Length > 50)
                {
                    preview = preview[..47] + "...";
                }

                var rowInfo = entry.RowCount.HasValue ? $" ({entry.RowCount} rows)" : "";
                items.Add($"[{time}]{rowInfo} {preview}");
            }

            if (items.Count == 0)
            {
                items.Add("(No query history)");
            }

            _listView.SetSource(items);
            _statusLabel.Text = $"{_entries.Count} queries";

            UpdatePreview();
        });
    }

    private void OnSearchChanged(NStack.ustring obj)
    {
        var searchText = _searchField.Text?.ToString();

        _errorService?.FireAndForget(LoadHistoryAsync(searchText), "SearchHistory");
    }

    private void OnSelectedItemChanged(ListViewItemEventArgs args)
    {
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        if (_listView.SelectedItem < 0 || _listView.SelectedItem >= _entries.Count)
        {
            _previewText.Text = string.Empty;
            return;
        }

        var entry = _entries[_listView.SelectedItem];
        _previewText.Text = entry.Sql;
    }

    private void OnItemActivated(ListViewItemEventArgs args)
    {
        OnExecuteClicked();
    }

    private void OnExecuteClicked()
    {
        if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _entries.Count)
        {
            _selectedEntry = _entries[_listView.SelectedItem];
            Application.RequestStop();
        }
    }

    private void OnCopyClicked()
    {
        if (_listView.SelectedItem >= 0 && _listView.SelectedItem < _entries.Count)
        {
            var entry = _entries[_listView.SelectedItem];

            if (Clipboard.TrySetClipboardData(entry.Sql))
            {
                _statusLabel.Text = "Query copied to clipboard";
            }
            else
            {
                _statusLabel.Text = "Failed to copy to clipboard";
            }
        }
    }

    private void OnDeleteClicked()
    {
        if (_listView.SelectedItem < 0 || _listView.SelectedItem >= _entries.Count)
            return;

        var entry = _entries[_listView.SelectedItem];
        var result = MessageBox.Query("Delete", "Delete this query from history?", "Yes", "No");

        if (result == 0)
        {
            _errorService?.FireAndForget(DeleteEntryAsync(entry.Id), "DeleteHistoryEntry");
        }
    }

    private async Task DeleteEntryAsync(string entryId)
    {
        await _historyService.DeleteEntryAsync(_environmentUrl, entryId);
        await LoadHistoryAsync(_searchField.Text?.ToString());
    }

    /// <inheritdoc />
    public QueryHistoryDialogState CaptureState()
    {
        var items = _entries.Select(e => new QueryHistoryItem(
            QueryText: e.Sql.Length > 50 ? e.Sql[..47] + "..." : e.Sql,
            ExecutedAt: e.ExecutedAt,
            RowCount: e.RowCount)).ToList();

        var selectedQuery = _listView.SelectedItem >= 0 && _listView.SelectedItem < _entries.Count
            ? _entries[_listView.SelectedItem].Sql
            : null;

        return new QueryHistoryDialogState(
            Title: Title?.ToString() ?? string.Empty,
            HistoryItems: items,
            SelectedIndex: _listView.SelectedItem,
            SelectedQueryText: selectedQuery,
            IsEmpty: _entries.Count == 0);
    }
}
