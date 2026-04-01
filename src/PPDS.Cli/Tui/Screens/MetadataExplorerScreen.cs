using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Models;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for browsing Dataverse entity metadata including attributes,
/// relationships, keys, privileges, and choices.
/// </summary>
internal sealed class MetadataExplorerScreen : TuiScreenBase
{
    private readonly FrameView _entitiesFrame;
    private readonly TextField _searchField;
    private readonly ListView _entityList;
    private readonly FrameView _detailsFrame;
    private readonly TableView _detailTable;
    private readonly Label _statusLabel;
    private readonly Button[] _tabButtons;
    private readonly Action[] _tabClickHandlers;

    private List<EntitySummary> _allEntities = [];
    private List<EntitySummary> _filteredEntities = [];
    private EntityMetadataDto? _selectedEntity;
    private IReadOnlyList<OptionSetMetadataDto>? _globalOptionSets;
    private int _activeTabIndex;
    private object? _filterDebounceToken;
    private CancellationTokenSource? _loadCts;
    private int _lastSelectedIndex = -1;

    private const int FilterDebounceMs = 300;

    private static readonly string[] TabNames = ["Attributes", "Relationships", "Keys", "Privileges", "Choices"];

    public override string Title => "Metadata";

    public MetadataExplorerScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        // Left pane: entity list with search
        _entitiesFrame = new FrameView("Entities")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(35),
            Height = Dim.Fill(1) // leave room for status label
        };

        _searchField = new TextField("")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            ColorScheme = TuiColorPalette.TextInput
        };

        _entityList = new ListView
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = TuiColorPalette.Default
        };

        _entitiesFrame.Add(_searchField, _entityList);

        // Right pane: details with tab buttons and table
        _detailsFrame = new FrameView("Details")
        {
            X = Pos.Right(_entitiesFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1)
        };

        // Create tab buttons
        _tabButtons = new Button[TabNames.Length];
        _tabClickHandlers = new Action[TabNames.Length];
        Button? previousButton = null;
        for (int i = 0; i < TabNames.Length; i++)
        {
            var idx = i;
            _tabButtons[i] = new Button(TabNames[i])
            {
                X = previousButton is null ? 0 : Pos.Right(previousButton) + 1,
                Y = 0,
                ColorScheme = i == 0 ? TuiColorPalette.TabActive : TuiColorPalette.TabInactive
            };
            _tabClickHandlers[i] = () => SwitchTab(idx);
            _tabButtons[i].Clicked += _tabClickHandlers[i];
            previousButton = _tabButtons[i];
        }

        _detailTable = new TableView
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
            Style = { ShowHorizontalHeaderOverline = false, ShowHorizontalHeaderUnderline = true }
        };

        foreach (var btn in _tabButtons) _detailsFrame.Add(btn);
        _detailsFrame.Add(_detailTable);

        // Status label at bottom
        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(_entitiesFrame),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Loading...",
            ColorScheme = TuiColorPalette.Default
        };

        Content.Add(_entitiesFrame, _detailsFrame, _statusLabel);

        // Wire up events
        _searchField.TextChanged += OnSearchTextChanged;
        _entityList.SelectedItemChanged += OnEntitySelectionChanged;

        // Kick off initial load
        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(LoadEntitiesAsync(), "Metadata.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () => ErrorService.FireAndForget(LoadEntitiesAsync(), "Metadata.Refresh"));
        RegisterHotkey(registry, Key.CtrlMask | Key.F, "Focus search", () => _searchField.SetFocus());
        RegisterHotkey(registry, Key.CtrlMask | Key.O, "Open in Maker", OpenInMaker);
    }

    #region Data Loading

    private async Task LoadEntitiesAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            });
            return;
        }

        var oldCts = _loadCts;
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ScreenCancellation);
        oldCts?.Cancel();
        oldCts?.Dispose();
        var ct = _loadCts.Token;

        try
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "Loading entities...";
            });

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ct);
            var metadataService = provider.GetRequiredService<IMetadataQueryService>();
            var entities = await metadataService.GetEntitiesAsync(cancellationToken: ct);

            ct.ThrowIfCancellationRequested();

            Application.MainLoop.Invoke(() =>
            {
                _allEntities = entities.OrderBy(e => e.LogicalName).ToList();
                _selectedEntity = null;
                _globalOptionSets = null;
                _lastSelectedIndex = -1;
                ApplyFilterOnUiThread();
            });
        }
        catch (OperationCanceledException) { /* screen closing or superseded */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load entities", ex, "Metadata.LoadEntities");
                _statusLabel.Text = "Error loading entities";
            });
        }
    }

    private async Task LoadEntityDetailAsync(string logicalName)
    {
        var oldCts = _loadCts;
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ScreenCancellation);
        oldCts?.Cancel();
        oldCts?.Dispose();
        var ct = _loadCts.Token;

        try
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = $"Loading {logicalName}...";
            });

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ct);
            var metadataService = provider.GetRequiredService<IMetadataQueryService>();

            var (entity, globalOptionSets) = await metadataService.GetEntityWithGlobalOptionSetsAsync(
                logicalName, includeGlobalOptionSets: true, ct);

            ct.ThrowIfCancellationRequested();

            Application.MainLoop.Invoke(() =>
            {
                _selectedEntity = entity;
                _globalOptionSets = globalOptionSets;
                RefreshDetailTable();
                UpdateStatusLabel();
            });
        }
        catch (OperationCanceledException) { /* screen closing or superseded */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError($"Failed to load entity: {logicalName}", ex, "Metadata.LoadEntity");
                _statusLabel.Text = $"Error loading {logicalName}";
            });
        }
    }

    #endregion

    #region Filtering

    private void OnSearchTextChanged(NStack.ustring _)
    {
        if (_filterDebounceToken != null && Application.MainLoop != null)
        {
            Application.MainLoop.RemoveTimeout(_filterDebounceToken);
            _filterDebounceToken = null;
        }

        if (Application.MainLoop != null)
        {
            _filterDebounceToken = Application.MainLoop.AddTimeout(
                TimeSpan.FromMilliseconds(FilterDebounceMs),
                _ =>
                {
                    _filterDebounceToken = null;
                    ApplyFilterOnUiThread();
                    return false;
                });
        }
    }

    private void ApplyFilterOnUiThread()
    {
        var filterText = _searchField.Text?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(filterText))
        {
            _filteredEntities = new List<EntitySummary>(_allEntities);
        }
        else
        {
            _filteredEntities = _allEntities
                .Where(e => e.LogicalName.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                    || e.DisplayName.Contains(filterText, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var displayNames = _filteredEntities
            .Select(e =>
            {
                var prefix = e.IsCustomEntity ? "\u25c6 " : "  ";
                return $"{prefix}{e.LogicalName}";
            })
            .ToList();

        _lastSelectedIndex = -1;
        _entityList.SetSource(displayNames);
        UpdateStatusLabel();
    }

    #endregion

    #region Entity Selection

    private void OnEntitySelectionChanged(ListViewItemEventArgs args)
    {
        var index = args.Item;
        if (index < 0 || index >= _filteredEntities.Count) return;
        if (index == _lastSelectedIndex) return;
        _lastSelectedIndex = index;

        var entity = _filteredEntities[index];
        ErrorService.FireAndForget(LoadEntityDetailAsync(entity.LogicalName), "Metadata.SelectEntity");
    }

    #endregion

    #region Tab Switching

    private void SwitchTab(int tabIndex)
    {
        if (tabIndex == _activeTabIndex) return;
        _activeTabIndex = tabIndex;

        for (int i = 0; i < _tabButtons.Length; i++)
        {
            _tabButtons[i].ColorScheme = i == _activeTabIndex
                ? TuiColorPalette.TabActive
                : TuiColorPalette.TabInactive;
        }

        RefreshDetailTable();
    }

    #endregion

    #region Detail Table Population

    private void RefreshDetailTable()
    {
        if (_selectedEntity == null)
        {
            (_detailTable.Table as IDisposable)?.Dispose();
            _detailTable.Table = new System.Data.DataTable();
            return;
        }

        var dt = _activeTabIndex switch
        {
            0 => BuildAttributesTable(),
            1 => BuildRelationshipsTable(),
            2 => BuildKeysTable(),
            3 => BuildPrivilegesTable(),
            4 => BuildChoicesTable(),
            _ => new System.Data.DataTable()
        };

        (_detailTable.Table as IDisposable)?.Dispose();
        _detailTable.Table = dt;
    }

    private System.Data.DataTable BuildAttributesTable()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("LogicalName", typeof(string));
        dt.Columns.Add("DisplayName", typeof(string));
        dt.Columns.Add("Type", typeof(string));
        dt.Columns.Add("Required", typeof(string));
        dt.Columns.Add("Custom", typeof(string));
        dt.Columns.Add("MaxLength", typeof(string));

        foreach (var attr in _selectedEntity!.Attributes.OrderBy(a => a.LogicalName))
        {
            dt.Rows.Add(
                attr.LogicalName,
                attr.DisplayName,
                attr.AttributeType,
                attr.RequiredLevel ?? "\u2014",
                attr.IsCustomAttribute ? "\u2713" : "\u2014",
                attr.MaxLength?.ToString() ?? "\u2014");
        }

        return dt;
    }

    private System.Data.DataTable BuildRelationshipsTable()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("SchemaName", typeof(string));
        dt.Columns.Add("Type", typeof(string));
        dt.Columns.Add("RelatedEntity", typeof(string));
        dt.Columns.Add("LookupField", typeof(string));
        dt.Columns.Add("CascadeDelete", typeof(string));

        // One-to-Many
        foreach (var rel in _selectedEntity!.OneToManyRelationships.OrderBy(r => r.SchemaName))
        {
            dt.Rows.Add(
                rel.SchemaName,
                "1:N",
                rel.ReferencingEntity,
                rel.ReferencingAttribute,
                rel.CascadeDelete ?? "\u2014");
        }

        // Many-to-One
        foreach (var rel in _selectedEntity.ManyToOneRelationships.OrderBy(r => r.SchemaName))
        {
            dt.Rows.Add(
                rel.SchemaName,
                "N:1",
                rel.ReferencedEntity,
                rel.ReferencingAttribute,
                rel.CascadeDelete ?? "\u2014");
        }

        // Many-to-Many
        foreach (var rel in _selectedEntity.ManyToManyRelationships.OrderBy(r => r.SchemaName))
        {
            var relatedEntity = rel.Entity1LogicalName == _selectedEntity.LogicalName
                ? rel.Entity2LogicalName
                : rel.Entity1LogicalName;

            dt.Rows.Add(
                rel.SchemaName,
                "N:N",
                relatedEntity,
                rel.IntersectEntityName,
                "\u2014");
        }

        return dt;
    }

    private System.Data.DataTable BuildKeysTable()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("DisplayName", typeof(string));
        dt.Columns.Add("SchemaName", typeof(string));
        dt.Columns.Add("KeyAttributes", typeof(string));
        dt.Columns.Add("IndexStatus", typeof(string));

        foreach (var key in _selectedEntity!.Keys.OrderBy(k => k.SchemaName))
        {
            dt.Rows.Add(
                key.DisplayName,
                key.SchemaName,
                string.Join(", ", key.KeyAttributes),
                key.EntityKeyIndexStatus ?? "\u2014");
        }

        return dt;
    }

    private System.Data.DataTable BuildPrivilegesTable()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("Name", typeof(string));
        dt.Columns.Add("Type", typeof(string));
        dt.Columns.Add("Basic", typeof(string));
        dt.Columns.Add("Local", typeof(string));
        dt.Columns.Add("Deep", typeof(string));
        dt.Columns.Add("Global", typeof(string));

        foreach (var priv in _selectedEntity!.Privileges.OrderBy(p => p.Name))
        {
            dt.Rows.Add(
                priv.Name,
                priv.PrivilegeType,
                priv.CanBeBasic ? "\u2713" : "\u2014",
                priv.CanBeLocal ? "\u2713" : "\u2014",
                priv.CanBeDeep ? "\u2713" : "\u2014",
                priv.CanBeGlobal ? "\u2713" : "\u2014");
        }

        return dt;
    }

    private System.Data.DataTable BuildChoicesTable()
    {
        var dt = new System.Data.DataTable();
        dt.Columns.Add("AttributeName", typeof(string));
        dt.Columns.Add("OptionSetName", typeof(string));
        dt.Columns.Add("Global", typeof(string));
        dt.Columns.Add("ValuesCount", typeof(int));

        var choiceAttributes = _selectedEntity!.Attributes
            .Where(a => a.OptionSetName != null)
            .OrderBy(a => a.LogicalName)
            .ToList();

        foreach (var attr in choiceAttributes)
        {
            var valueCount = 0;
            if (attr.Options != null)
            {
                valueCount = attr.Options.Count;
            }
            else if (attr.IsGlobalOptionSet && _globalOptionSets != null)
            {
                var globalOs = _globalOptionSets.FirstOrDefault(
                    os => os.Name.Equals(attr.OptionSetName, StringComparison.OrdinalIgnoreCase));
                if (globalOs != null)
                {
                    valueCount = globalOs.Options.Count;
                }
            }

            dt.Rows.Add(
                attr.LogicalName,
                attr.OptionSetName ?? "\u2014",
                attr.IsGlobalOptionSet ? "\u2713" : "\u2014",
                valueCount);
        }

        return dt;
    }

    #endregion

    #region Status and Navigation

    private void UpdateStatusLabel()
    {
        var entityCount = _filteredEntities.Count;
        var parts = new List<string> { $"{entityCount} entit{(entityCount != 1 ? "ies" : "y")}" };

        if (_selectedEntity != null)
        {
            parts.Add($"Selected: {_selectedEntity.LogicalName} ({_selectedEntity.Attributes.Count} attributes)");
        }

        _statusLabel.Text = string.Join(" | ", parts);
    }

    private void OpenInMaker()
    {
        if (EnvironmentUrl == null)
        {
            ErrorService.ReportError("No environment URL available");
            return;
        }

        var url = EnvironmentUrl;
        if (_selectedEntity != null)
        {
            url = DataverseUrlBuilder.BuildEntityListUrl(EnvironmentUrl, _selectedEntity.LogicalName);
        }

        using var dialog = new Dialog("Open in Maker", new Button("OK", is_default: true))
        {
            Width = 70,
            Height = 7
        };
        dialog.Add(new Label { X = 1, Y = 1, Text = "Open this URL in your browser:" });
        dialog.Add(new Label { X = 1, Y = 2, Text = url });
        Application.Run(dialog);
    }

    #endregion

    protected override void OnDispose()
    {
        _searchField.TextChanged -= OnSearchTextChanged;
        _entityList.SelectedItemChanged -= OnEntitySelectionChanged;
        for (int i = 0; i < _tabButtons.Length; i++)
        {
            _tabButtons[i].Clicked -= _tabClickHandlers[i];
        }
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        if (_filterDebounceToken != null)
        {
            Application.MainLoop?.RemoveTimeout(_filterDebounceToken);
        }
    }
}
