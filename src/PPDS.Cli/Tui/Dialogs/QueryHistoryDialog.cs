using PPDS.Cli.Infrastructure.Errors;
using PPDS.Cli.Services.History;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Dialogs;

/// <summary>
/// Dialog for browsing and selecting from query history.
/// </summary>
internal sealed class QueryHistoryDialog : Dialog
{
    private readonly IQueryHistoryService _historyService;
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
    public QueryHistoryDialog(IQueryHistoryService historyService, string environmentUrl) : base("Query History")
    {
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _environmentUrl = environmentUrl ?? throw new ArgumentNullException(nameof(environmentUrl));

        Width = 80;
        Height = 22;
        ColorScheme = TuiColorPalette.Default;

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
            Width = Dim.Fill() - 2
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
            AllowsMultipleSelection = false
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
            WordWrap = true
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
        var executeButton = new Button("_Execute")
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

        // Load history asynchronously
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = LoadHistoryAsync(null).ContinueWith(t =>
        {
            if (t.IsFaulted && t.Exception != null)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _statusLabel.Text = $"Error: {t.Exception.InnerException?.Message ?? t.Exception.Message}";
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
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

#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
        _ = LoadHistoryAsync(searchText).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Application.MainLoop?.Invoke(() =>
                {
                    _statusLabel.Text = "Search failed";
                });
            }
        }, TaskScheduler.Default);
#pragma warning restore PPDS013
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

            if (PPDS.Cli.Infrastructure.ClipboardHelper.CopyToClipboard(entry.Sql))
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
#pragma warning disable PPDS013 // Fire-and-forget with explicit error handling
            _ = DeleteEntryAsync(entry.Id).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    Application.MainLoop?.Invoke(() =>
                    {
                        _statusLabel.Text = "Failed to delete entry";
                    });
                }
            }, TaskScheduler.Default);
#pragma warning restore PPDS013
        }
    }

    private async Task DeleteEntryAsync(string entryId)
    {
        await _historyService.DeleteEntryAsync(_environmentUrl, entryId);
        await LoadHistoryAsync(_searchField.Text?.ToString());
    }
}
