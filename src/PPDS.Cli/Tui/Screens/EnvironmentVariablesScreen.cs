using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Services.Settings;
using PPDS.Cli.Tui.Infrastructure;
using PPDS.Dataverse.Services;
using Terminal.Gui;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for viewing and editing environment variables.
/// </summary>
internal sealed class EnvironmentVariablesScreen : TuiScreenBase
{
    private readonly TableView _table;
    private readonly Label _statusLabel;
    private List<EnvironmentVariableInfo> _variables = [];
    private string? _solutionFilter;
    private Dialog? _detailDialog;
    private bool _isShowingDetail;

    public override string Title => "Environment Variables";

    public EnvironmentVariablesScreen(InteractiveSession session, string? environmentUrl = null)
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
            // Restore persisted filter state
            var savedState = Session.GetTuiStateStore()
                .LoadScreenState<SolutionFilterScreenState>("EnvironmentVariables", EnvironmentUrl);
            if (savedState != null)
                _solutionFilter = savedState.SolutionFilter;

            ErrorService.FireAndForget(LoadDataAsync(), "EnvironmentVariables.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.CtrlMask | Key.R, "Refresh", () => ErrorService.FireAndForget(LoadDataAsync(), "EnvironmentVariables.Refresh"));
        RegisterHotkey(registry, Key.CtrlMask | Key.E, "Export", () => ErrorService.FireAndForget(ExportAsync(), "EnvironmentVariables.Export"));
        RegisterHotkey(registry, Key.CtrlMask | Key.F, "Solution Filter", () => ErrorService.FireAndForget(ShowSolutionFilterDialogAsync(), "EnvironmentVariables.Filter"));
        RegisterHotkey(registry, Key.CtrlMask | Key.O, "Open in Maker", OpenInMaker);
    }

    private async Task LoadDataAsync()
    {
        try
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "Loading environment variables...";
            });
            Application.Refresh();

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IEnvironmentVariableService>();

            // Validate persisted solution filter still exists
            if (_solutionFilter != null)
            {
                var solutionService = provider.GetRequiredService<ISolutionService>();
                var solutionsResult = await solutionService.ListAsync(
                    includeManaged: false,
                    cancellationToken: ScreenCancellation);

                var solutionNames = solutionsResult.Items.Select(s => s.UniqueName).ToList();
                if (!solutionNames.Contains(_solutionFilter))
                {
                    var staleName = _solutionFilter;
                    _solutionFilter = null;

                    // Persist cleared state
                    ErrorService.FireAndForget(
                        Session.GetTuiStateStore().SaveScreenStateAsync("EnvironmentVariables", EnvironmentUrl!,
                            new SolutionFilterScreenState { SolutionFilter = null }),
                        "EnvironmentVariables.ClearStaleState");

                    Application.MainLoop.Invoke(() =>
                    {
                        _statusLabel.Text = $"Solution '{staleName}' no longer exists — filter cleared";
                    });
                }
            }

            var evResult = await service.ListAsync(
                solutionName: _solutionFilter,
                cancellationToken: ScreenCancellation);
            _variables = evResult.Items.ToList();

            var dt = new System.Data.DataTable();
            dt.Columns.Add("Schema Name", typeof(string));
            dt.Columns.Add("Display Name", typeof(string));
            dt.Columns.Add("Type", typeof(string));
            dt.Columns.Add("Default Value", typeof(string));
            dt.Columns.Add("Current Value", typeof(string));
            dt.Columns.Add("Managed", typeof(string));

            foreach (var v in _variables)
            {
                dt.Rows.Add(
                    v.SchemaName,
                    v.DisplayName ?? "\u2014",
                    v.Type,
                    TruncateValue(v.DefaultValue),
                    TruncateValue(v.CurrentValue),
                    v.IsManaged ? "Yes" : "No");
            }

            Application.MainLoop.Invoke(() =>
            {
                _table.Table = dt;
                var overridden = _variables.Count(v => v.CurrentValue != null);
                var missing = _variables.Count(v => v.IsRequired && v.DefaultValue == null && v.CurrentValue == null);
                var statusParts = new List<string>
                {
                    $"{_variables.Count} environment variable{(_variables.Count != 1 ? "s" : "")}"
                };
                if (overridden > 0) statusParts.Add($"{overridden} overridden");
                if (missing > 0) statusParts.Add($"{missing} missing");
                if (_solutionFilter != null) statusParts.Add($"filtered: {_solutionFilter}");
                _statusLabel.Text = string.Join(" \u2014 ", statusParts);
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load environment variables", ex, "EnvironmentVariables.Load");
                _statusLabel.Text = "Error loading environment variables";
            });
        }
    }

    private static string TruncateValue(string? value)
    {
        if (value == null) return "\u2014";
        return value.Length > 50 ? value[..47] + "..." : value;
    }

    private void OnCellActivated(TableView.CellActivatedEventArgs args)
    {
        if (_isShowingDetail) return;
        if (args.Row < 0 || args.Row >= _variables.Count) return;
        var variable = _variables[args.Row];
        ErrorService.FireAndForget(ShowEditDialogAsync(variable), "EnvironmentVariables.ShowEdit");
    }

    private async Task ShowEditDialogAsync(EnvironmentVariableInfo variable)
    {
        try
        {
            await Task.CompletedTask; // Ensure async context for consistency

            Application.MainLoop.Invoke(() =>
            {
                if (_isShowingDetail) return;
                _isShowingDetail = true;

                _detailDialog?.Dispose();

                var isReadOnly = variable.Type is "DataSource" or "Secret";

                // Build detail content area
                var y = 0;
                var detailViews = new List<View>();

                var schemaLabel = new Label($"Schema Name: {variable.SchemaName}") { X = 1, Y = y++ };
                detailViews.Add(schemaLabel);
                var displayLabel = new Label($"Display Name: {variable.DisplayName ?? "\u2014"}") { X = 1, Y = y++ };
                detailViews.Add(displayLabel);
                var typeLabel = new Label($"Type: {variable.Type}") { X = 1, Y = y++ };
                detailViews.Add(typeLabel);
                var managedLabel = new Label($"Managed: {(variable.IsManaged ? "Yes" : "No")}") { X = 1, Y = y++ };
                detailViews.Add(managedLabel);
                var requiredLabel = new Label($"Required: {(variable.IsRequired ? "Yes" : "No")}") { X = 1, Y = y++ };
                detailViews.Add(requiredLabel);
                var descLabel = new Label($"Description: {variable.Description ?? "\u2014"}") { X = 1, Y = y++ };
                detailViews.Add(descLabel);

                y++;
                var defaultLabel = new Label($"Default Value: {variable.DefaultValue ?? "(none)"}") { X = 1, Y = y++ };
                detailViews.Add(defaultLabel);

                y++;
                var currentLabel = new Label("Current Value:") { X = 1, Y = y++ };
                detailViews.Add(currentLabel);

                View? valueEditor = null;
                RadioGroup? boolGroup = null;

                if (isReadOnly)
                {
                    var readOnlyLabel = new Label(variable.CurrentValue ?? "(not set - read only)")
                    {
                        X = 1, Y = y
                    };
                    detailViews.Add(readOnlyLabel);
                }
                else if (variable.Type == "Boolean")
                {
                    boolGroup = new RadioGroup(new NStack.ustring[] { "true", "false" })
                    {
                        X = 1,
                        Y = y
                    };
                    // Set initial selection
                    if (string.Equals(variable.CurrentValue, "true", StringComparison.OrdinalIgnoreCase)
                        || (variable.CurrentValue == null && string.Equals(variable.DefaultValue, "true", StringComparison.OrdinalIgnoreCase)))
                    {
                        boolGroup.SelectedItem = 0;
                    }
                    else
                    {
                        boolGroup.SelectedItem = 1;
                    }
                    detailViews.Add(boolGroup);
                }
                else if (variable.Type == "JSON")
                {
                    var textView = new TextView
                    {
                        X = 1,
                        Y = y,
                        Width = Dim.Fill(2),
                        Height = 6,
                        Text = variable.CurrentValue ?? variable.DefaultValue ?? "",
                        ReadOnly = false
                    };
                    valueEditor = textView;
                    detailViews.Add(textView);
                }
                else
                {
                    // String or Number
                    var textField = new TextField(variable.CurrentValue ?? "")
                    {
                        X = 1,
                        Y = y,
                        Width = Dim.Fill(2)
                    };
                    valueEditor = textField;
                    detailViews.Add(textField);
                }

                var buttons = new List<Button>();
                Button? saveButton = null;

                if (!isReadOnly)
                {
                    saveButton = new Button("Save");
                    buttons.Add(saveButton);
                }

                var closeButton = new Button("Close", is_default: isReadOnly);
                buttons.Add(closeButton);

                _detailDialog = new Dialog(
                    $"Environment Variable: {variable.DisplayName ?? variable.SchemaName}",
                    buttons.ToArray())
                {
                    Width = Dim.Percent(70),
                    Height = Dim.Percent(70)
                };

                foreach (var view in detailViews)
                {
                    _detailDialog.Add(view);
                }

                closeButton.Clicked += () => Application.RequestStop();

                if (saveButton != null)
                {
                    saveButton.Clicked += () =>
                    {
                        string? newValue = null;

                        if (variable.Type == "Boolean" && boolGroup != null)
                        {
                            newValue = boolGroup.SelectedItem == 0 ? "true" : "false";
                        }
                        else if (variable.Type == "JSON" && valueEditor is TextView tv)
                        {
                            newValue = tv.Text?.ToString();

                            // Validate JSON syntax
                            if (!string.IsNullOrEmpty(newValue))
                            {
                                try
                                {
                                    System.Text.Json.JsonDocument.Parse(newValue);
                                }
                                catch (System.Text.Json.JsonException)
                                {
                                    MessageBox.ErrorQuery("Validation Error", "Value must be valid JSON.", "OK");
                                    return;
                                }
                            }
                        }
                        else if (valueEditor is TextField tf)
                        {
                            newValue = tf.Text?.ToString();

                            // Validate numeric input
                            if (variable.Type == "Number" && !string.IsNullOrEmpty(newValue))
                            {
                                if (!decimal.TryParse(newValue, out _))
                                {
                                    MessageBox.ErrorQuery("Validation Error", "Value must be a valid number.", "OK");
                                    return;
                                }
                            }
                        }

                        if (newValue != null)
                        {
                            Application.RequestStop();
                            ErrorService.FireAndForget(
                                SaveValueAsync(variable.SchemaName, newValue),
                                "EnvironmentVariables.Save");
                        }
                    };
                }

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
                ErrorService.ReportError("Failed to show environment variable details", ex, "EnvironmentVariables.Detail");
            });
        }
    }

    private async Task SaveValueAsync(string schemaName, string value)
    {
        try
        {
            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IEnvironmentVariableService>();

            var result = await service.SetValueAsync(schemaName, value, ScreenCancellation);

            Application.MainLoop.Invoke(() =>
            {
                if (result)
                {
                    _statusLabel.Text = $"Saved value for {schemaName}";
                }
                else
                {
                    _statusLabel.Text = $"Failed to save value for {schemaName} (variable not found)";
                }
            });

            // Reload data to reflect the change
            await LoadDataAsync();
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to save environment variable value", ex, "EnvironmentVariables.Save");
            });
        }
    }

    private async Task ExportAsync()
    {
        try
        {
            Application.MainLoop.Invoke(() =>
            {
                _statusLabel.Text = "Exporting environment variables...";
            });

            var provider = await Session.GetServiceProviderAsync(EnvironmentUrl!, ScreenCancellation);
            var service = provider.GetRequiredService<IEnvironmentVariableService>();

            var export = await service.ExportAsync(
                solutionName: _solutionFilter,
                cancellationToken: ScreenCancellation);

            var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            var fileName = _solutionFilter != null
                ? $"{_solutionFilter}-env-variables.json"
                : "env-variables.json";

            Application.MainLoop.Invoke(() =>
            {
                var textView = new TextView
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill(),
                    Height = Dim.Fill(),
                    ReadOnly = true,
                    Text = json
                };

                var copyButton = new Button("Copy Path");
                var closeButton = new Button("Close", is_default: true);

                var dialog = new Dialog(
                    $"Export: {fileName}",
                    copyButton, closeButton)
                {
                    Width = Dim.Percent(80),
                    Height = Dim.Percent(80)
                };
                dialog.Add(textView);

                var filePath = System.IO.Path.Combine(Environment.CurrentDirectory, fileName);

                copyButton.Clicked += () =>
                {
                    try
                    {
                        System.IO.File.WriteAllText(filePath, json);
                        _statusLabel.Text = $"Exported to {filePath}";
                    }
                    catch (Exception ex)
                    {
                        ErrorService.ReportError("Failed to write export file", ex, "EnvironmentVariables.ExportWrite");
                    }
                    Application.RequestStop();
                };

                closeButton.Clicked += () => Application.RequestStop();

                Application.Run(dialog);
                dialog.Dispose();

                // Restore status
                var overridden = _variables.Count(v => v.CurrentValue != null);
                var missing = _variables.Count(v => v.IsRequired && v.DefaultValue == null && v.CurrentValue == null);
                var statusParts = new List<string>
                {
                    $"{_variables.Count} environment variable{(_variables.Count != 1 ? "s" : "")}"
                };
                if (overridden > 0) statusParts.Add($"{overridden} overridden");
                if (missing > 0) statusParts.Add($"{missing} missing");
                if (_solutionFilter != null) statusParts.Add($"filtered: {_solutionFilter}");
                _statusLabel.Text = string.Join(" \u2014 ", statusParts);
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to export environment variables", ex, "EnvironmentVariables.Export");
                _statusLabel.Text = "Export failed";
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

                    // Persist updated filter state
                    ErrorService.FireAndForget(
                        Session.GetTuiStateStore().SaveScreenStateAsync("EnvironmentVariables", EnvironmentUrl!,
                            new SolutionFilterScreenState { SolutionFilter = _solutionFilter }),
                        "EnvironmentVariables.SaveState");

                    ErrorService.FireAndForget(LoadDataAsync(), "EnvironmentVariables.FilterApply");
                }
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load solutions for filter", ex, "EnvironmentVariables.FilterLoad");
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
        dialog.Add(new Label { X = 1, Y = 2, Text = EnvironmentUrl + "/environmentvariables" });
        Application.Run(dialog);
        dialog.Dispose();
    }

    protected override void OnDispose()
    {
        _table.CellActivated -= OnCellActivated;
        _detailDialog?.Dispose();
    }
}
