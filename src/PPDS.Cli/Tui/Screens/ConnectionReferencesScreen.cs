using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for viewing connection references and their relationship with flows.
/// </summary>
internal sealed class ConnectionReferencesScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private List<ConnectionReferenceInfo> _references = [];
    private List<ConnectionInfo> _connections = [];
    private string? _solutionFilter;
    private bool _staleFilterChecked;
    private Dialog? _detailDialog;
    private bool _isShowingDetail;

    public override string Title => "Connection References";

    public ConnectionReferencesScreen(InteractiveSession session, string? environmentUrl = null)
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

        if (EnvironmentUrl != null)
        {
            var savedState = Session.GetTuiStateStore()
                .LoadScreenState<SolutionFilterScreenState>("ConnectionReferences", EnvironmentUrl);
            if (savedState != null)
                _solutionFilter = savedState.SolutionFilter;

            ErrorService.FireAndForget(LoadDataAsync(), "ConnectionReferences.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () => ErrorService.FireAndForget(LoadDataAsync(), "ConnectionReferences.Refresh"));
        RegisterHotkey(registry, Key.CtrlMask | Key.A, "Analyze", () => ErrorService.FireAndForget(ShowAnalyzeDialogAsync(), "ConnectionReferences.Analyze"));
        RegisterHotkey(registry, Key.CtrlMask | Key.F, "Solution Filter", () => ErrorService.FireAndForget(ShowSolutionFilterDialogAsync(), "ConnectionReferences.Filter"));
        RegisterHotkey(registry, Key.CtrlMask | Key.O, "Open in Maker", OpenInMaker);
    }

    private async Task LoadDataAsync()
    {
        try
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "Loading connection references...";
            });
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);

            // Stale filter check: verify persisted solution still exists (first load only)
            if (_solutionFilter != null && !_staleFilterChecked)
            {
                _staleFilterChecked = true;
                var solutionService = provider.GetRequiredService<ISolutionService>();
                var solutions = await solutionService.ListAsync(
                    includeManaged: false,
                    cancellationToken: ScreenCancellation);
                var exists = solutions.Items.Any(s =>
                    string.Equals(s.UniqueName, _solutionFilter, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    var staleName = _solutionFilter;
                    _solutionFilter = null;
                    ErrorService.FireAndForget(
                        Session.GetTuiStateStore().SaveScreenStateAsync("ConnectionReferences", EnvironmentUrl!,
                            new SolutionFilterScreenState { SolutionFilter = null }),
                        "ConnectionReferences.ClearStaleState");
                    Application.MainLoop.Invoke(() =>
                    {
                        _statusLabel.Text = $"Previously filtered solution '{staleName}' not found \u2014 showing all";
                    });
                }
            }

            var crService = provider.GetRequiredService<IConnectionReferenceService>();

            var crResult = await crService.ListAsync(
                solutionName: _solutionFilter,
                cancellationToken: ScreenCancellation);
            _references = crResult.Items.ToList();

            // Try to load connections for status enrichment (SPN graceful degradation)
            try
            {
                var connService = provider.GetService<IConnectionService>();
                if (connService != null)
                {
                    _connections = await connService.ListAsync(cancellationToken: ScreenCancellation);
                }
            }
            catch (Exception ex)
            {
                TuiDebugLog.Log($"ConnectionReferences: Failed to load connections for status enrichment: {ex.Message}");
                _connections = [];
            }

            var dt = new System.Data.DataTable();
            dt.Columns.Add("Display Name", typeof(string));
            dt.Columns.Add("Logical Name", typeof(string));
            dt.Columns.Add("Connector", typeof(string));
            dt.Columns.Add("Status", typeof(string));
            dt.Columns.Add("Managed", typeof(string));
            dt.Columns.Add("Modified On", typeof(string));

            foreach (var cr in _references)
            {
                var status = GetConnectionStatus(cr);

                dt.Rows.Add(
                    cr.DisplayName ?? "\u2014",
                    cr.LogicalName,
                    FormatConnectorId(cr.ConnectorId),
                    status,
                    cr.IsManaged ? "Yes" : "No",
                    cr.ModifiedOn?.ToString("g") ?? "\u2014");
            }

            Application.MainLoop.Invoke(() =>
            {
                _table.Table = dt;
                var bound = _references.Count(r => r.IsBound);
                var unbound = _references.Count(r => !r.IsBound);
                var statusParts = new List<string>
                {
                    $"{_references.Count} connection reference{(_references.Count != 1 ? "s" : "")}"
                };
                if (bound > 0) statusParts.Add($"{bound} bound");
                if (unbound > 0) statusParts.Add($"{unbound} unbound");
                if (_solutionFilter != null) statusParts.Add($"filtered: {_solutionFilter}");
                _statusLabel.Text = string.Join(" \u2014 ", statusParts);
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load connection references", ex, "ConnectionReferences.Load");
                _statusLabel.Text = "Error loading connection references";
            });
        }
    }

    private string GetConnectionStatus(ConnectionReferenceInfo cr)
    {
        if (!cr.IsBound) return "Unbound";

        // Try to find matching connection for richer status
        var conn = _connections.FirstOrDefault(c =>
            string.Equals(c.ConnectionId, cr.ConnectionId, StringComparison.OrdinalIgnoreCase));

        if (conn == null) return "Bound";

        return conn.Status switch
        {
            ConnectionStatus.Connected => "Connected",
            ConnectionStatus.Error => "Error",
            _ => "Bound"
        };
    }

    private static string FormatConnectorId(string? connectorId)
    {
        if (string.IsNullOrEmpty(connectorId)) return "\u2014";

        // Extract the connector name from the full path
        // e.g., "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps" -> "shared_commondataserviceforapps"
        var lastSlash = connectorId.LastIndexOf('/');
        return lastSlash >= 0 ? connectorId[(lastSlash + 1)..] : connectorId;
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (_isShowingDetail) return;
        if (args.Row < 0 || args.Row >= _references.Count) return;
        var cr = _references[args.Row];
        ErrorService.FireAndForget(ShowDetailDialogAsync(cr), "ConnectionReferences.ShowDetail");
    }

    private async Task ShowDetailDialogAsync(ConnectionReferenceInfo cr)
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IConnectionReferenceService>();

            var flows = await service.GetFlowsUsingAsync(cr.LogicalName, ScreenCancellation);

            // Find matching connection for additional details
            ConnectionInfo? conn = null;
            if (cr.IsBound)
            {
                conn = _connections.FirstOrDefault(c =>
                    string.Equals(c.ConnectionId, cr.ConnectionId, StringComparison.OrdinalIgnoreCase));
            }

            var lines = new List<string>
            {
                $"Display Name: {cr.DisplayName ?? "\u2014"}",
                $"Logical Name: {cr.LogicalName}",
                $"Description: {cr.Description ?? "\u2014"}",
                $"Connector: {FormatConnectorId(cr.ConnectorId)}",
                $"Connection ID: {cr.ConnectionId ?? "(none)"}",
                $"Status: {GetConnectionStatus(cr)}",
                $"Managed: {(cr.IsManaged ? "Yes" : "No")}",
                $"Created: {cr.CreatedOn?.ToString("g") ?? "\u2014"}",
                $"Modified: {cr.ModifiedOn?.ToString("g") ?? "\u2014"}",
            };

            if (conn != null)
            {
                lines.Add("");
                lines.Add("--- Connection Details ---");
                lines.Add($"Connection Name: {conn.DisplayName ?? "\u2014"}");
                lines.Add($"Connector Name: {conn.ConnectorDisplayName ?? "\u2014"}");
                lines.Add($"Connection Status: {conn.Status}");
                lines.Add($"Shared: {(conn.IsShared ? "Yes" : "No")}");
                lines.Add($"Created By: {conn.CreatedBy ?? "\u2014"}");
            }

            lines.Add("");
            lines.Add($"--- Dependent Flows ({flows.Count}) ---");
            if (flows.Count == 0)
            {
                lines.Add("  (no flows use this connection reference)");
            }
            else
            {
                foreach (var flow in flows)
                {
                    lines.Add($"  {flow.DisplayName ?? flow.UniqueName} [{flow.State}]");
                }
            }

            var text = string.Join(Environment.NewLine, lines);

            Application.MainLoop.Invoke(() =>
            {
                if (_isShowingDetail) return;
                _isShowingDetail = true;

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
                    $"Connection Reference: {cr.DisplayName ?? cr.LogicalName}",
                    new Button("Close", is_default: true))
                {
                    Width = Dim.Percent(80),
                    Height = Dim.Percent(80)
                };
                _detailDialog.Add(textView);
                Application.Run(_detailDialog);
                _detailDialog.Dispose();
                _detailDialog = null;
                _isShowingDetail = false;
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                _isShowingDetail = false;
                ErrorService.ReportError("Failed to load connection reference details", ex, "ConnectionReferences.Detail");
            });
        }
    }

    private async Task ShowAnalyzeDialogAsync()
    {
        try
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "Analyzing connection references...";
            });

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IConnectionReferenceService>();

            var analysis = await service.AnalyzeAsync(
                solutionName: _solutionFilter,
                cancellationToken: ScreenCancellation);

            var lines = new List<string>
            {
                "Connection Reference Analysis",
                new string('=', 40),
                "",
                $"Valid Relationships: {analysis.ValidCount}",
                $"Orphaned Flows: {analysis.OrphanedFlowCount}",
                $"Orphaned Connection References: {analysis.OrphanedConnectionReferenceCount}",
                ""
            };

            if (analysis.HasOrphans)
            {
                var orphanedFlows = analysis.Relationships
                    .Where(r => r.Type == RelationshipType.OrphanedFlow)
                    .ToList();

                if (orphanedFlows.Count > 0)
                {
                    lines.Add("--- Orphaned Flows (reference missing CRs) ---");
                    foreach (var r in orphanedFlows)
                    {
                        lines.Add($"  Flow: {r.FlowDisplayName ?? r.FlowUniqueName ?? "\u2014"}");
                        lines.Add($"    Missing CR: {r.ConnectionReferenceLogicalName ?? "\u2014"}");
                    }
                    lines.Add("");
                }

                var orphanedCRs = analysis.Relationships
                    .Where(r => r.Type == RelationshipType.OrphanedConnectionReference)
                    .ToList();

                if (orphanedCRs.Count > 0)
                {
                    lines.Add("--- Orphaned Connection References (unused) ---");
                    foreach (var r in orphanedCRs)
                    {
                        lines.Add($"  {r.ConnectionReferenceDisplayName ?? r.ConnectionReferenceLogicalName ?? "\u2014"}");
                        lines.Add($"    Connector: {FormatConnectorId(r.ConnectorId)}");
                        lines.Add($"    Bound: {(r.IsBound == true ? "Yes" : "No")}");
                    }
                    lines.Add("");
                }
            }
            else
            {
                lines.Add("No orphans detected. All connection references and flows are properly linked.");
            }

            var text = string.Join(Environment.NewLine, lines);

            Application.MainLoop.Invoke(() =>
            {
                // Restore status label
                var bound = _references.Count(r => r.IsBound);
                var unbound = _references.Count(r => !r.IsBound);
                var statusParts = new List<string>
                {
                    $"{_references.Count} connection reference{(_references.Count != 1 ? "s" : "")}"
                };
                if (bound > 0) statusParts.Add($"{bound} bound");
                if (unbound > 0) statusParts.Add($"{unbound} unbound");
                if (_solutionFilter != null) statusParts.Add($"filtered: {_solutionFilter}");
                _statusLabel.Text = string.Join(" \u2014 ", statusParts);

                var textView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    Text = text
                };

                var dialog = new Dialog(
                    "Connection Reference Analysis",
                    new Button("Close", is_default: true))
                {
                    Width = Dim.Percent(80),
                    Height = Dim.Percent(80)
                };
                dialog.Add(textView);
                Application.Run(dialog);
                dialog.Dispose();
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to analyze connection references", ex, "ConnectionReferences.Analyze");
                // Restore status
                _statusLabel.Text = "Analysis failed";
            });
        }
    }

    private async Task ShowSolutionFilterDialogAsync()
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var solutionService = provider.GetRequiredService<ISolutionService>();

            var solutionsResult = await solutionService.ListAsync(
                includeManaged: false,
                cancellationToken: ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                var names = new List<string> { "(All - no filter)" };
                names.AddRange(solutionsResult.Items.Select(s => s.UniqueName));

                var listView = new ListView(names)
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill()
                };

                // Pre-select current filter
                if (_solutionFilter != null)
                {
                    var idx = names.IndexOf(_solutionFilter);
                    if (idx >= 0) listView.SelectedItem = idx;
                }

                var selectButton = new Button("Select", is_default: true);
                var cancelButton = new Button("Cancel");

                var dialog = new Dialog("Filter by Solution", selectButton, cancelButton)
                {
                    Width = Dim.Percent(60),
                    Height = Dim.Percent(70)
                };
                dialog.Add(listView);

                var confirmed = false;
                selectButton.Clicked += () =>
                {
                    confirmed = true;
                    Application.RequestStop();
                };
                cancelButton.Clicked += () => Application.RequestStop();
                listView.OpenSelectedItem += _ =>
                {
                    confirmed = true;
                    Application.RequestStop();
                };

                Application.Run(dialog);
                dialog.Dispose();

                if (confirmed)
                {
                    var idx = listView.SelectedItem;
                    _solutionFilter = idx == 0 ? null : names[idx];
                    ErrorService.FireAndForget(
                        Session.GetTuiStateStore().SaveScreenStateAsync("ConnectionReferences", EnvironmentUrl!,
                            new SolutionFilterScreenState { SolutionFilter = _solutionFilter }),
                        "ConnectionReferences.SaveState");
                    ErrorService.FireAndForget(LoadDataAsync(), "ConnectionReferences.FilterApply");
                }
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load solutions for filter", ex, "ConnectionReferences.FilterLoad");
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
        dialog.Add(new Label { X = 1, Y = 2, Text = EnvironmentUrl + "/connectors" });
        Application.Run(dialog);
        dialog.Dispose();
    }

    protected override void OnDispose()
    {
        _table.CellActivated -= OnCellActivated;
        _detailDialog?.Dispose();
    }
}
