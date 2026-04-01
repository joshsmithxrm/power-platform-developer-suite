using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Infrastructure;
using PPDS.Cli.Tui.Dialogs.Metadata;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Metadata;
using PPDS.Dataverse.Metadata.Authoring;
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

    // Action bar buttons
    private readonly Button _newButton;
    private readonly Button _editButton;
    private readonly Button _deleteButton;
    private readonly Action _newClickHandler;
    private readonly Action _editClickHandler;
    private readonly Action _deleteClickHandler;

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

    /// <summary>Tab indices for action bar context sensitivity.</summary>
    private const int TabAttributes = 0;
    private const int TabRelationships = 1;
    private const int TabKeys = 2;
    private const int TabPrivileges = 3;
    private const int TabChoices = 4;

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

        // Right pane: details with tab buttons, action bar, and table
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

        // Action bar: New, Edit, Delete buttons (row below tabs)
        _newButton = new Button("[New]") { X = 0, Y = 1 };
        _editButton = new Button("[Edit]") { X = Pos.Right(_newButton) + 1, Y = 1 };
        _deleteButton = new Button("[Delete]") { X = Pos.Right(_editButton) + 1, Y = 1 };

        _newClickHandler = OnNewClicked;
        _editClickHandler = OnEditClicked;
        _deleteClickHandler = OnDeleteClicked;

        _newButton.Clicked += _newClickHandler;
        _editButton.Clicked += _editClickHandler;
        _deleteButton.Clicked += _deleteClickHandler;

        _detailTable = new TableView
        {
            X = 0,
            Y = 3, // shifted down for action bar
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            FullRowSelect = true,
            Style = { ShowHorizontalHeaderOverline = false, ShowHorizontalHeaderUnderline = true }
        };

        foreach (var btn in _tabButtons) _detailsFrame.Add(btn);
        _detailsFrame.Add(_newButton, _editButton, _deleteButton);
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

        // Update action bar for initial tab state
        UpdateActionBarVisibility();

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
        RegisterHotkey(registry, Key.CtrlMask | Key.N, "New", OnNewClicked);
        RegisterHotkey(registry, Key.CtrlMask | Key.E, "Edit", OnEditClicked);
        RegisterHotkey(registry, Key.CtrlMask | Key.D, "Delete", OnDeleteClicked);
    }

    #region Action Bar

    /// <summary>
    /// Updates action bar button visibility based on the active tab.
    /// Privileges tab is read-only, so all action buttons are hidden.
    /// </summary>
    internal void UpdateActionBarVisibility()
    {
        bool showActions = _activeTabIndex != TabPrivileges;
        _newButton.Visible = showActions;
        _editButton.Visible = showActions;
        _deleteButton.Visible = showActions;
    }

    private void OnNewClicked()
    {
        if (EnvironmentUrl == null) return;

        switch (_activeTabIndex)
        {
            case TabAttributes:
                if (_selectedEntity == null) return;
                OpenCreateColumnDialog();
                break;
            case TabRelationships:
                if (_selectedEntity == null) return;
                OpenCreateRelationshipDialog();
                break;
            case TabKeys:
                if (_selectedEntity == null) return;
                OpenCreateKeyDialog();
                break;
            case TabChoices:
                OpenCreateChoiceDialog();
                break;
            default:
                // Entity-level new (no tab or tab==Attributes with no entity selected)
                OpenCreateTableDialog();
                break;
        }
    }

    private void OnEditClicked()
    {
        if (EnvironmentUrl == null || _selectedEntity == null) return;

        switch (_activeTabIndex)
        {
            case TabAttributes:
                EditSelectedAttribute();
                break;
            case TabRelationships:
            case TabKeys:
            case TabChoices:
                // For these tabs, editing means updating the display name via generic property editor
                EditSelectedItemDisplayName();
                break;
        }
    }

    private void OnDeleteClicked()
    {
        if (EnvironmentUrl == null || _selectedEntity == null) return;

        switch (_activeTabIndex)
        {
            case TabAttributes:
                DeleteSelectedAttribute();
                break;
            case TabRelationships:
                DeleteSelectedRelationship();
                break;
            case TabKeys:
                DeleteSelectedKey();
                break;
            case TabChoices:
                DeleteSelectedChoice();
                break;
        }
    }

    #endregion

    #region Create Dialogs

    private void OpenCreateTableDialog()
    {
        using var dialog = new CreateTableDialog(Session);
        Application.Run(dialog);
        if (dialog.Result is { } request)
        {
            _statusLabel.Text = "Creating table...";
            ErrorService.FireAndForget(ExecuteCreateTableAsync(request), "Metadata.CreateTable");
        }
    }

    private async Task ExecuteCreateTableAsync(CreateTableRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            var result = await service.CreateTableAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Table created: {result.LogicalName}";
            });

            await LoadEntitiesAsync();
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to create table", ex, "Metadata.CreateTable");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void OpenCreateColumnDialog()
    {
        using var dialog = new CreateColumnDialog(_selectedEntity!.LogicalName, Session);
        Application.Run(dialog);
        if (dialog.Result is { } request)
        {
            _statusLabel.Text = "Creating column...";
            ErrorService.FireAndForget(ExecuteCreateColumnAsync(request), "Metadata.CreateColumn");
        }
    }

    private async Task ExecuteCreateColumnAsync(CreateColumnRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            var result = await service.CreateColumnAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Column created: {result.LogicalName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to create column", ex, "Metadata.CreateColumn");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void OpenCreateRelationshipDialog()
    {
        using var dialog = new CreateRelationshipDialog(_selectedEntity!.LogicalName, Session);
        Application.Run(dialog);

        if (dialog.OneToManyResult is { } oneToMany)
        {
            _statusLabel.Text = "Creating relationship...";
            ErrorService.FireAndForget(ExecuteCreateOneToManyAsync(oneToMany), "Metadata.CreateRelationship");
        }
        else if (dialog.ManyToManyResult is { } manyToMany)
        {
            _statusLabel.Text = "Creating relationship...";
            ErrorService.FireAndForget(ExecuteCreateManyToManyAsync(manyToMany), "Metadata.CreateRelationship");
        }
    }

    private async Task ExecuteCreateOneToManyAsync(CreateOneToManyRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.CreateOneToManyAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Relationship created: {request.SchemaName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to create relationship", ex, "Metadata.CreateRelationship");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private async Task ExecuteCreateManyToManyAsync(CreateManyToManyRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.CreateManyToManyAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Relationship created: {request.SchemaName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to create relationship", ex, "Metadata.CreateRelationship");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void OpenCreateChoiceDialog()
    {
        using var dialog = new CreateChoiceDialog(Session);
        Application.Run(dialog);
        if (dialog.Result is { } request)
        {
            _statusLabel.Text = "Creating global choice...";
            ErrorService.FireAndForget(ExecuteCreateChoiceAsync(request), "Metadata.CreateChoice");
        }
    }

    private async Task ExecuteCreateChoiceAsync(CreateGlobalChoiceRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            var result = await service.CreateGlobalChoiceAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Global choice created: {result.Name}";
            });

            if (_selectedEntity != null)
            {
                await LoadEntityDetailAsync(_selectedEntity.LogicalName);
            }
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to create global choice", ex, "Metadata.CreateChoice");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void OpenCreateKeyDialog()
    {
        using var dialog = new CreateKeyDialog(_selectedEntity!.LogicalName, Session);
        Application.Run(dialog);
        if (dialog.Result is { } request)
        {
            _statusLabel.Text = "Creating alternate key...";
            ErrorService.FireAndForget(ExecuteCreateKeyAsync(request), "Metadata.CreateKey");
        }
    }

    private async Task ExecuteCreateKeyAsync(CreateKeyRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            var result = await service.CreateKeyAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Key created: {result.SchemaName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to create key", ex, "Metadata.CreateKey");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    #endregion

    #region Edit Operations

    private void EditSelectedAttribute()
    {
        if (_selectedEntity == null || _detailTable.Table == null) return;
        var row = _detailTable.SelectedRow;
        if (row < 0 || row >= _detailTable.Table.Rows.Count) return;

        var displayName = _detailTable.Table.Rows[row]["DisplayName"]?.ToString() ?? "";
        var logicalName = _detailTable.Table.Rows[row]["LogicalName"]?.ToString() ?? "";

        using var dialog = new EditPropertyDialog("Display Name", displayName, Session);
        Application.Run(dialog);

        if (dialog.UpdatedValue is { } newValue && newValue != displayName)
        {
            var request = new UpdateColumnRequest
            {
                SolutionUniqueName = "", // user must provide via a solution-aware flow in the future
                EntityLogicalName = _selectedEntity.LogicalName,
                ColumnLogicalName = logicalName,
                DisplayName = newValue
            };
            _statusLabel.Text = "Updating column...";
            ErrorService.FireAndForget(ExecuteUpdateColumnAsync(request), "Metadata.EditColumn");
        }
    }

    private async Task ExecuteUpdateColumnAsync(UpdateColumnRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.UpdateColumnAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Column updated: {request.ColumnLogicalName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to update column", ex, "Metadata.EditColumn");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void EditSelectedItemDisplayName()
    {
        if (_detailTable.Table == null) return;
        var row = _detailTable.SelectedRow;
        if (row < 0 || row >= _detailTable.Table.Rows.Count) return;

        // Try common column names for display
        string currentName = "";
        string identifier = "";

        if (_detailTable.Table.Columns.Contains("DisplayName"))
        {
            currentName = _detailTable.Table.Rows[row]["DisplayName"]?.ToString() ?? "";
        }

        if (_detailTable.Table.Columns.Contains("SchemaName"))
        {
            identifier = _detailTable.Table.Rows[row]["SchemaName"]?.ToString() ?? "";
        }
        else if (_detailTable.Table.Columns.Contains("OptionSetName"))
        {
            identifier = _detailTable.Table.Rows[row]["OptionSetName"]?.ToString() ?? "";
        }

        using var dialog = new EditPropertyDialog("Display Name", currentName, Session);
        Application.Run(dialog);

        if (dialog.UpdatedValue is { } newValue && newValue != currentName)
        {
            _statusLabel.Text = $"Updated display name for {identifier}";
        }
    }

    #endregion

    #region Delete Operations

    private void DeleteSelectedAttribute()
    {
        if (_selectedEntity == null || _detailTable.Table == null) return;
        var row = _detailTable.SelectedRow;
        if (row < 0 || row >= _detailTable.Table.Rows.Count) return;

        var logicalName = _detailTable.Table.Rows[row]["LogicalName"]?.ToString() ?? "";

        using var dialog = new DeleteConfirmDialog("Column", logicalName, session: Session);
        Application.Run(dialog);

        if (dialog.Confirmed)
        {
            var request = new DeleteColumnRequest
            {
                EntityLogicalName = _selectedEntity.LogicalName,
                ColumnLogicalName = logicalName
            };
            _statusLabel.Text = "Deleting column...";
            ErrorService.FireAndForget(ExecuteDeleteColumnAsync(request), "Metadata.DeleteColumn");
        }
    }

    private async Task ExecuteDeleteColumnAsync(DeleteColumnRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.DeleteColumnAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Column deleted: {request.ColumnLogicalName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to delete column", ex, "Metadata.DeleteColumn");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void DeleteSelectedRelationship()
    {
        if (_selectedEntity == null || _detailTable.Table == null) return;
        var row = _detailTable.SelectedRow;
        if (row < 0 || row >= _detailTable.Table.Rows.Count) return;

        var schemaName = _detailTable.Table.Rows[row]["SchemaName"]?.ToString() ?? "";

        using var dialog = new DeleteConfirmDialog("Relationship", schemaName, session: Session);
        Application.Run(dialog);

        if (dialog.Confirmed)
        {
            var request = new DeleteRelationshipRequest { SchemaName = schemaName };
            _statusLabel.Text = "Deleting relationship...";
            ErrorService.FireAndForget(ExecuteDeleteRelationshipAsync(request), "Metadata.DeleteRelationship");
        }
    }

    private async Task ExecuteDeleteRelationshipAsync(DeleteRelationshipRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.DeleteRelationshipAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Relationship deleted: {request.SchemaName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to delete relationship", ex, "Metadata.DeleteRelationship");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void DeleteSelectedKey()
    {
        if (_selectedEntity == null || _detailTable.Table == null) return;
        var row = _detailTable.SelectedRow;
        if (row < 0 || row >= _detailTable.Table.Rows.Count) return;

        var schemaName = _detailTable.Table.Rows[row]["SchemaName"]?.ToString() ?? "";

        using var dialog = new DeleteConfirmDialog("Key", schemaName, session: Session);
        Application.Run(dialog);

        if (dialog.Confirmed)
        {
            var request = new DeleteKeyRequest
            {
                EntityLogicalName = _selectedEntity.LogicalName,
                KeyLogicalName = schemaName
            };
            _statusLabel.Text = "Deleting key...";
            ErrorService.FireAndForget(ExecuteDeleteKeyAsync(request), "Metadata.DeleteKey");
        }
    }

    private async Task ExecuteDeleteKeyAsync(DeleteKeyRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.DeleteKeyAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Key deleted: {request.KeyLogicalName}";
            });

            await LoadEntityDetailAsync(_selectedEntity!.LogicalName);
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to delete key", ex, "Metadata.DeleteKey");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    private void DeleteSelectedChoice()
    {
        if (_selectedEntity == null || _detailTable.Table == null) return;
        var row = _detailTable.SelectedRow;
        if (row < 0 || row >= _detailTable.Table.Rows.Count) return;

        var optionSetName = _detailTable.Table.Rows[row]["OptionSetName"]?.ToString() ?? "";
        var isGlobal = _detailTable.Table.Rows[row]["Global"]?.ToString() == "\u2713";

        if (!isGlobal)
        {
            MessageBox.ErrorQuery("Not Supported", "Only global choices can be deleted from this view.", "OK");
            return;
        }

        using var dialog = new DeleteConfirmDialog("Global Choice", optionSetName, session: Session);
        Application.Run(dialog);

        if (dialog.Confirmed)
        {
            var request = new DeleteGlobalChoiceRequest { Name = optionSetName };
            _statusLabel.Text = "Deleting global choice...";
            ErrorService.FireAndForget(ExecuteDeleteChoiceAsync(request), "Metadata.DeleteChoice");
        }
    }

    private async Task ExecuteDeleteChoiceAsync(DeleteGlobalChoiceRequest request)
    {
        try
        {
            var reporter = new TuiMetadataAuthoringProgressReporter(_statusLabel);
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IMetadataAuthoringService>();
            await service.DeleteGlobalChoiceAsync(request, reporter, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Global choice deleted: {request.Name}";
            });

            if (_selectedEntity != null)
            {
                await LoadEntityDetailAsync(_selectedEntity.LogicalName);
            }
        }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to delete global choice", ex, "Metadata.DeleteChoice");
                _statusLabel.Text = $"Error: {ex.Message}";
            });
        }
    }

    #endregion

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

        UpdateActionBarVisibility();
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
        _newButton.Clicked -= _newClickHandler;
        _editButton.Clicked -= _editClickHandler;
        _deleteButton.Clicked -= _deleteClickHandler;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        if (_filterDebounceToken != null)
        {
            Application.MainLoop?.RemoveTimeout(_filterDebounceToken);
        }
    }
}
