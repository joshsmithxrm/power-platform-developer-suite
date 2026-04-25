using Microsoft.Extensions.DependencyInjection;
using PPDS.Cli.Plugins.Registration;
using PPDS.Cli.Services;
using PPDS.Cli.Tui.Infrastructure;
using Terminal.Gui;
using Terminal.Gui.Trees;

namespace PPDS.Cli.Tui.Screens;

/// <summary>
/// TUI screen for browsing and managing plugin registrations, service endpoints,
/// custom APIs, and virtual entity data providers.
/// </summary>
internal sealed class PluginRegistrationScreen : TuiScreenBase
{
    private readonly FrameView _treeFrame;
    private readonly TreeView _tree;
    private readonly FrameView _detailPanel;
    private readonly Label _detailLabel;
    private readonly Label _statusLabel;
    private readonly bool _includeHidden = false;
    private readonly bool _includeMicrosoft = false;
    private CancellationTokenSource? _loadCts;
    private bool _isShowingDialog;

    public override string Title => EnvironmentDisplayName != null
        ? $"Plugin Registration - {EnvironmentDisplayName}"
        : "Plugin Registration";

    public PluginRegistrationScreen(InteractiveSession session, string? environmentUrl = null)
        : base(session, environmentUrl)
    {
        // Left pane: tree view (60% width)
        _treeFrame = new FrameView("Registrations")
        {
            X = 0,
            Y = 0,
            Width = Dim.Percent(60),
            Height = Dim.Fill(1)
        };

        _tree = new TreeView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = TuiColorPalette.Default
        };

        _treeFrame.Add(_tree);

        // Right pane: detail panel (40% width)
        _detailPanel = new FrameView("Details")
        {
            X = Pos.Right(_treeFrame),
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
            ColorScheme = TuiColorPalette.Default
        };

        _detailLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Text = "Select a node to view details.",
            ColorScheme = TuiColorPalette.Default
        };

        _detailPanel.Add(_detailLabel);

        // Status bar at bottom
        _statusLabel = new Label
        {
            X = 0,
            Y = Pos.Bottom(_treeFrame),
            Width = Dim.Fill(),
            Height = 1,
            Text = "Loading...",
            ColorScheme = TuiColorPalette.Default
        };

        Content.Add(_treeFrame, _detailPanel, _statusLabel);

        // Wire tree events
        _tree.SelectionChanged += OnSelectionChanged;
        _tree.ObjectActivated += OnNodeActivated;

        if (EnvironmentUrl != null)
        {
            ErrorService.FireAndForget(LoadRootAsync(), "PluginReg.InitialLoad");
        }
        else
        {
            _statusLabel.Text = "No environment selected. Use the status bar to connect.";
        }
    }

    protected override void RegisterHotkeys(IHotkeyRegistry registry)
    {
        RegisterHotkey(registry, Key.F5, "Refresh", () =>
            ErrorService.FireAndForget(LoadRootAsync(), "PluginReg.Refresh"));

        RegisterHotkey(registry, Key.Space, "Toggle step enabled/disabled", () =>
            ErrorService.FireAndForget(ToggleSelectedStepAsync(), "PluginReg.Toggle"));

        RegisterHotkey(registry, Key.DeleteChar, "Unregister", () =>
            ErrorService.FireAndForget(UnregisterSelectedAsync(), "PluginReg.Unregister"));

        RegisterHotkey(registry, Key.CtrlMask | Key.D, "Download binary", () =>
            ErrorService.FireAndForget(DownloadSelectedAsync(), "PluginReg.Download"));
    }

    #region Root Loading

    private async Task LoadRootAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl))
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = "No environment selected. Use the status bar to connect.";
            });
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(ScreenCancellation);
        var ct = _loadCts.Token;

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = "Loading plugin registrations...";
                _tree.ClearObjects();
            });

            var provider = await GetProviderAsync(ct);
            var regService = provider.GetRequiredService<IPluginRegistrationService>();
            var endpointService = provider.GetRequiredService<IServiceEndpointService>();
            var customApiService = provider.GetRequiredService<ICustomApiService>();
            var dataProviderService = provider.GetRequiredService<IDataProviderService>();

            var options = new PluginListOptions(
                IncludeHidden: _includeHidden,
                IncludeMicrosoft: _includeMicrosoft);

            // Load all data in parallel
            var packagesTask = regService.ListPackagesAsync(options: options, cancellationToken: ct);
            var assembliesTask = regService.ListAssembliesAsync(options: options, cancellationToken: ct);
            var endpointsTask = endpointService.ListAsync(ct);
            var customApisTask = customApiService.ListAsync(ct);
            var dataSourcesTask = dataProviderService.ListDataSourcesAsync(ct);

            await Task.WhenAll(packagesTask, assembliesTask, endpointsTask, customApisTask, dataSourcesTask);
            ct.ThrowIfCancellationRequested();

            var packages = await packagesTask;
            var allAssemblies = await assembliesTask;
            var endpoints = await endpointsTask;
            var customApis = await customApisTask;
            var dataSources = await dataSourcesTask;

            // Standalone assemblies = those not belonging to any package
            var standaloneAssemblies = allAssemblies
                .Where(a => !a.PackageId.HasValue)
                .ToList();

            // Webhooks vs service bus endpoints
            var webhooks = endpoints.Where(e => e.IsWebhook).ToList();
            var serviceEndpoints = endpoints.Where(e => !e.IsWebhook).ToList();

            Application.MainLoop?.Invoke(() =>
            {
                _tree.ClearObjects();

                // Plugin Packages
                foreach (var pkg in packages.OrderBy(p => p.Name))
                {
                    var node = new PluginTreeNode
                    {
                        NodeType = "package",
                        Id = pkg.Id,
                        DisplayName = pkg.Name,
                        IsManaged = pkg.IsManaged,
                        Info = pkg
                    };
                    // Add placeholder so node shows expand arrow
                    node.Children.Add(new PluginTreeNode { NodeType = "loading", DisplayName = "Loading..." });
                    _tree.AddObject(node);
                }

                // Standalone Assemblies
                foreach (var asm in standaloneAssemblies.OrderBy(a => a.Name))
                {
                    var node = new PluginTreeNode
                    {
                        NodeType = "assembly",
                        Id = asm.Id,
                        DisplayName = asm.Name,
                        IsManaged = asm.IsManaged,
                        Info = asm
                    };
                    node.Children.Add(new PluginTreeNode { NodeType = "loading", DisplayName = "Loading..." });
                    _tree.AddObject(node);
                }

                // Webhooks
                foreach (var wh in webhooks.OrderBy(w => w.Name))
                {
                    var node = new PluginTreeNode
                    {
                        NodeType = "webhook",
                        Id = wh.Id,
                        DisplayName = wh.Name,
                        IsManaged = wh.IsManaged,
                        IsLoaded = true,
                        Info = wh
                    };
                    _tree.AddObject(node);
                }

                // Service Endpoints
                foreach (var ep in serviceEndpoints.OrderBy(e => e.Name))
                {
                    var node = new PluginTreeNode
                    {
                        NodeType = "serviceEndpoint",
                        Id = ep.Id,
                        DisplayName = ep.Name,
                        IsManaged = ep.IsManaged,
                        IsLoaded = true,
                        Info = ep
                    };
                    _tree.AddObject(node);
                }

                // Custom APIs
                foreach (var api in customApis.OrderBy(a => a.UniqueName))
                {
                    var node = new PluginTreeNode
                    {
                        NodeType = "customApi",
                        Id = api.Id,
                        DisplayName = $"{api.UniqueName} ({api.DisplayName})",
                        IsManaged = api.IsManaged,
                        IsLoaded = true,
                        Info = api
                    };
                    _tree.AddObject(node);
                }

                // Data Sources (virtual entity providers)
                foreach (var ds in dataSources.OrderBy(d => d.Name))
                {
                    var node = new PluginTreeNode
                    {
                        NodeType = "dataSource",
                        Id = ds.Id,
                        DisplayName = ds.Name,
                        Info = ds
                    };
                    node.Children.Add(new PluginTreeNode { NodeType = "loading", DisplayName = "Loading..." });
                    _tree.AddObject(node);
                }

                var parts = new List<string>();
                if (packages.Count > 0) parts.Add($"{packages.Count} package{(packages.Count != 1 ? "s" : "")}");
                if (standaloneAssemblies.Count > 0) parts.Add($"{standaloneAssemblies.Count} standalone assembl{(standaloneAssemblies.Count != 1 ? "ies" : "y")}");
                if (webhooks.Count > 0) parts.Add($"{webhooks.Count} webhook{(webhooks.Count != 1 ? "s" : "")}");
                if (serviceEndpoints.Count > 0) parts.Add($"{serviceEndpoints.Count} endpoint{(serviceEndpoints.Count != 1 ? "s" : "")}");
                if (customApis.Count > 0) parts.Add($"{customApis.Count} custom API{(customApis.Count != 1 ? "s" : "")}");
                if (dataSources.Count > 0) parts.Add($"{dataSources.Count} data source{(dataSources.Count != 1 ? "s" : "")}");

                _statusLabel.Text = parts.Count > 0
                    ? string.Join(" \u2014 ", parts)
                    : "No plugin registrations found. F5 to refresh.";
            });
        }
        catch (OperationCanceledException) { /* screen closing or superseded load */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to load plugin registrations", ex, "PluginReg.Load");
                _statusLabel.Text = "Error loading plugin registrations";
            });
        }
    }

    #endregion

    #region Lazy Children Loading

    private async Task LoadChildrenAsync(PluginTreeNode node)
    {
        if (node.IsLoaded) return;
        if (string.IsNullOrEmpty(EnvironmentUrl)) return;

        try
        {
            var provider = await GetProviderAsync(ScreenCancellation);
            var regService = provider.GetRequiredService<IPluginRegistrationService>();
            var dataProviderService = provider.GetRequiredService<IDataProviderService>();
            var options = new PluginListOptions(
                IncludeHidden: _includeHidden,
                IncludeMicrosoft: _includeMicrosoft);

            IList<ITreeNode>? children = null;

            switch (node.NodeType)
            {
                case "package":
                {
                    var assemblies = await regService.ListAssembliesForPackageAsync(node.Id, ScreenCancellation);
                    children = assemblies.OrderBy(a => a.Name).Select(a =>
                    {
                        var asmNode = new PluginTreeNode
                        {
                            NodeType = "assembly",
                            Id = a.Id,
                            DisplayName = a.Name,
                            IsManaged = a.IsManaged,
                            Info = a
                        };
                        // Add placeholder so assembly shows expand arrow
                        asmNode.Children.Add(new PluginTreeNode { NodeType = "loading", DisplayName = "Loading..." });
                        return (ITreeNode)asmNode;
                    }).ToList();
                    break;
                }

                case "assembly":
                {
                    var types = await regService.ListTypesForAssemblyAsync(node.Id, ScreenCancellation);
                    children = types.OrderBy(t => t.TypeName).Select(t =>
                    {
                        var typeNode = new PluginTreeNode
                        {
                            NodeType = "type",
                            Id = t.Id,
                            DisplayName = t.TypeName,
                            IsManaged = t.IsManaged,
                            Info = t
                        };
                        typeNode.Children.Add(new PluginTreeNode { NodeType = "loading", DisplayName = "Loading..." });
                        return (ITreeNode)typeNode;
                    }).ToList();
                    break;
                }

                case "type":
                {
                    var steps = await regService.ListStepsForTypeAsync(node.Id, options: options, cancellationToken: ScreenCancellation);
                    children = steps.OrderBy(s => s.Name).Select(s =>
                    {
                        var stepNode = new PluginTreeNode
                        {
                            NodeType = "step",
                            Id = s.Id,
                            DisplayName = s.Name,
                            IsManaged = s.IsManaged,
                            IsEnabled = s.IsEnabled,
                            Info = s
                        };
                        stepNode.Children.Add(new PluginTreeNode { NodeType = "loading", DisplayName = "Loading..." });
                        return (ITreeNode)stepNode;
                    }).ToList();
                    break;
                }

                case "step":
                {
                    var images = await regService.ListImagesForStepAsync(node.Id, ScreenCancellation);
                    children = images.OrderBy(i => i.Name).Select(i =>
                        (ITreeNode)new PluginTreeNode
                        {
                            NodeType = "image",
                            Id = i.Id,
                            DisplayName = $"{i.Name} ({i.ImageType})",
                            IsManaged = i.IsManaged,
                            IsLoaded = true,
                            Info = i
                        }).ToList();
                    break;
                }

                case "dataSource":
                {
                    var providers = await dataProviderService.ListDataProvidersAsync(node.Id, ScreenCancellation);
                    children = providers.OrderBy(p => p.Name).Select(p =>
                        (ITreeNode)new PluginTreeNode
                        {
                            NodeType = "dataProvider",
                            Id = p.Id,
                            DisplayName = p.Name,
                            IsManaged = p.IsManaged,
                            IsLoaded = true,
                            Info = p
                        }).ToList();
                    break;
                }

                default:
                    return;
            }

            node.IsLoaded = true;

            Application.MainLoop?.Invoke(() =>
            {
                node.Children.Clear();
                if (children != null)
                {
                    foreach (var child in children)
                        node.Children.Add(child);
                }
                _tree.RefreshObject(node);
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError($"Failed to load children for {node.DisplayName}", ex, "PluginReg.LoadChildren");
            });
        }
    }

    #endregion

    #region Tree Events

    private void OnNodeActivated(ObjectActivatedEventArgs<ITreeNode> args)
    {
        if (args.ActivatedObject is PluginTreeNode node && !node.IsLoaded)
        {
            ErrorService.FireAndForget(LoadChildrenAsync(node), "PluginReg.LoadChildren");
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs<ITreeNode> args)
    {
        if (args.NewValue is PluginTreeNode node)
        {
            UpdateDetailPanel(node);

            // Trigger lazy-load when a node is selected that has only the placeholder child
            if (!node.IsLoaded && node.Children.Count == 1 &&
                node.Children[0] is PluginTreeNode placeholder && placeholder.NodeType == "loading")
            {
                ErrorService.FireAndForget(LoadChildrenAsync(node), "PluginReg.LazyLoad");
            }
        }
        else
        {
            _detailLabel.Text = "Select a node to view details.";
        }
    }

    #endregion

    #region Detail Panel

    private void UpdateDetailPanel(PluginTreeNode node)
    {
        var sb = new System.Text.StringBuilder();

        switch (node.NodeType)
        {
            case "package" when node.Info is PPDS.Cli.Plugins.Registration.PluginPackageInfo pkg:
                sb.AppendLine($"Type:       Package");
                sb.AppendLine($"Name:       {pkg.Name}");
                sb.AppendLine($"Unique:     {pkg.UniqueName ?? "\u2014"}");
                sb.AppendLine($"Version:    {pkg.Version ?? "\u2014"}");
                sb.AppendLine($"Managed:    {FormatBool(pkg.IsManaged)}");
                sb.AppendLine($"Created:    {FormatDate(pkg.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(pkg.ModifiedOn)}");
                break;

            case "assembly" when node.Info is PPDS.Cli.Plugins.Registration.PluginAssemblyInfo asm:
                sb.AppendLine($"Type:       Assembly");
                sb.AppendLine($"Name:       {asm.Name}");
                sb.AppendLine($"Version:    {asm.Version ?? "\u2014"}");
                sb.AppendLine($"PubKey:     {asm.PublicKeyToken ?? "\u2014"}");
                sb.AppendLine($"Isolation:  {FormatIsolationMode(asm.IsolationMode)}");
                sb.AppendLine($"Source:     {FormatSourceType(asm.SourceType)}");
                sb.AppendLine($"Managed:    {FormatBool(asm.IsManaged)}");
                sb.AppendLine($"Created:    {FormatDate(asm.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(asm.ModifiedOn)}");
                break;

            case "type" when node.Info is PPDS.Cli.Plugins.Registration.PluginTypeInfo pt:
                sb.AppendLine($"Type:       Plugin Type");
                sb.AppendLine($"TypeName:   {pt.TypeName}");
                sb.AppendLine($"FriendlyName: {pt.FriendlyName ?? "\u2014"}");
                sb.AppendLine($"Assembly:   {pt.AssemblyName ?? "\u2014"}");
                sb.AppendLine($"Managed:    {FormatBool(pt.IsManaged)}");
                sb.AppendLine($"Created:    {FormatDate(pt.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(pt.ModifiedOn)}");
                break;

            case "step" when node.Info is PPDS.Cli.Plugins.Registration.PluginStepInfo step:
                sb.AppendLine($"Type:       Step");
                sb.AppendLine($"Name:       {step.Name}");
                sb.AppendLine($"Message:    {step.Message}");
                sb.AppendLine($"Entity:     {step.PrimaryEntity}");
                sb.AppendLine($"Stage:      {step.Stage}");
                sb.AppendLine($"Mode:       {step.Mode}");
                sb.AppendLine($"Order:      {step.ExecutionOrder}");
                sb.AppendLine($"Enabled:    {FormatBool(step.IsEnabled)}");
                sb.AppendLine($"Managed:    {FormatBool(step.IsManaged)}");
                if (!string.IsNullOrEmpty(step.FilteringAttributes))
                    sb.AppendLine($"Filter:     {step.FilteringAttributes}");
                if (!string.IsNullOrEmpty(step.Description))
                    sb.AppendLine($"Desc:       {step.Description}");
                sb.AppendLine($"Created:    {FormatDate(step.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(step.ModifiedOn)}");
                break;

            case "image" when node.Info is PPDS.Cli.Plugins.Registration.PluginImageInfo img:
                sb.AppendLine($"Type:       Image");
                sb.AppendLine($"Name:       {img.Name}");
                sb.AppendLine($"ImageType:  {img.ImageType}");
                sb.AppendLine($"Alias:      {img.EntityAlias ?? "\u2014"}");
                sb.AppendLine($"Attributes: {img.Attributes ?? "(all)"}");
                sb.AppendLine($"MsgProp:    {img.MessagePropertyName ?? "\u2014"}");
                sb.AppendLine($"Managed:    {FormatBool(img.IsManaged)}");
                sb.AppendLine($"Created:    {FormatDate(img.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(img.ModifiedOn)}");
                break;

            case "webhook" when node.Info is ServiceEndpointInfo webhook:
                sb.AppendLine($"Type:       Webhook");
                sb.AppendLine($"Name:       {webhook.Name}");
                sb.AppendLine($"URL:        {webhook.Url ?? "\u2014"}");
                sb.AppendLine($"AuthType:   {webhook.AuthType}");
                sb.AppendLine($"Managed:    {FormatBool(webhook.IsManaged)}");
                if (!string.IsNullOrEmpty(webhook.Description))
                    sb.AppendLine($"Desc:       {webhook.Description}");
                sb.AppendLine($"Created:    {FormatDate(webhook.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(webhook.ModifiedOn)}");
                break;

            case "serviceEndpoint" when node.Info is ServiceEndpointInfo ep:
                sb.AppendLine($"Type:       Service Endpoint");
                sb.AppendLine($"Name:       {ep.Name}");
                sb.AppendLine($"Contract:   {ep.ContractType}");
                sb.AppendLine($"Namespace:  {ep.NamespaceAddress ?? "\u2014"}");
                sb.AppendLine($"Path:       {ep.Path ?? "\u2014"}");
                sb.AppendLine($"AuthType:   {ep.AuthType}");
                sb.AppendLine($"Format:     {ep.MessageFormat ?? "\u2014"}");
                sb.AppendLine($"Managed:    {FormatBool(ep.IsManaged)}");
                if (!string.IsNullOrEmpty(ep.Description))
                    sb.AppendLine($"Desc:       {ep.Description}");
                sb.AppendLine($"Created:    {FormatDate(ep.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(ep.ModifiedOn)}");
                break;

            case "customApi" when node.Info is CustomApiInfo api:
                sb.AppendLine($"Type:       Custom API");
                sb.AppendLine($"UniqueName: {api.UniqueName}");
                sb.AppendLine($"Display:    {api.DisplayName}");
                sb.AppendLine($"Binding:    {api.BindingType}");
                if (!string.IsNullOrEmpty(api.BoundEntity))
                    sb.AppendLine($"Entity:     {api.BoundEntity}");
                sb.AppendLine($"Function:   {FormatBool(api.IsFunction)}");
                sb.AppendLine($"Private:    {FormatBool(api.IsPrivate)}");
                sb.AppendLine($"Steps:      {api.AllowedProcessingStepType}");
                sb.AppendLine($"Plugin:     {api.PluginTypeName ?? "\u2014"}");
                sb.AppendLine($"Managed:    {FormatBool(api.IsManaged)}");
                if (api.RequestParameters.Count > 0)
                    sb.AppendLine($"Req Params: {api.RequestParameters.Count}");
                if (api.ResponseProperties.Count > 0)
                    sb.AppendLine($"Resp Props: {api.ResponseProperties.Count}");
                sb.AppendLine($"Created:    {FormatDate(api.CreatedOn)}");
                sb.AppendLine($"Modified:   {FormatDate(api.ModifiedOn)}");
                break;

            case "dataSource" when node.Info is DataSourceInfo ds:
                sb.AppendLine($"Type:       Data Source");
                sb.AppendLine($"Name:       {ds.Name}");
                break;

            case "dataProvider" when node.Info is DataProviderInfo dp:
                sb.AppendLine($"Type:       Data Provider");
                sb.AppendLine($"Name:       {dp.Name}");
                sb.AppendLine($"DataSource: {dp.DataSourceName ?? "\u2014"}");
                sb.AppendLine($"Retrieve:   {FormatGuid(dp.RetrievePlugin)}");
                sb.AppendLine($"RetrieveMult: {FormatGuid(dp.RetrieveMultiplePlugin)}");
                sb.AppendLine($"Create:     {FormatGuid(dp.CreatePlugin)}");
                sb.AppendLine($"Update:     {FormatGuid(dp.UpdatePlugin)}");
                sb.AppendLine($"Delete:     {FormatGuid(dp.DeletePlugin)}");
                sb.AppendLine($"Managed:    {FormatBool(dp.IsManaged)}");
                break;

            default:
                sb.AppendLine($"Node: {node.DisplayName}");
                break;
        }

        _detailLabel.Text = sb.ToString().TrimEnd();
    }

    private static string FormatBool(bool value) => value ? "Yes" : "No";
    private static string FormatDate(DateTime? dt) => dt?.ToString("G") ?? "\u2014";
    private static string FormatGuid(Guid? g) => g.HasValue ? g.Value.ToString("D")[..8] + "..." : "\u2014";

    private static string FormatIsolationMode(int mode) => mode switch
    {
        1 => "None",
        2 => "Sandbox",
        _ => mode.ToString()
    };

    private static string FormatSourceType(int type) => type switch
    {
        0 => "Database",
        1 => "Disk",
        2 => "Normal",
        _ => type.ToString()
    };

    #endregion

    #region Hotkey Actions

    private async Task ToggleSelectedStepAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl)) return;

        var selected = _tree.SelectedObject as PluginTreeNode;
        if (selected == null || selected.NodeType != "step") return;

        if (selected.Info is not PPDS.Cli.Plugins.Registration.PluginStepInfo step) return;

        if (step.IsManaged && !step.IsCustomizable)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("This step is managed and cannot be modified.");
            });
            return;
        }

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = step.IsEnabled
                    ? $"Disabling step: {step.Name}..."
                    : $"Enabling step: {step.Name}...";
            });

            var provider = await GetProviderAsync(ScreenCancellation);
            var regService = provider.GetRequiredService<IPluginRegistrationService>();

            if (step.IsEnabled)
            {
                await regService.DisableStepAsync(selected.Id, ScreenCancellation);
            }
            else
            {
                await regService.EnableStepAsync(selected.Id, ScreenCancellation);
            }

            // Reload step info and update node
            var updatedStep = new PPDS.Cli.Plugins.Registration.PluginStepInfo
            {
                Id = step.Id,
                Name = step.Name,
                Message = step.Message,
                PrimaryEntity = step.PrimaryEntity,
                SecondaryEntity = step.SecondaryEntity,
                Stage = step.Stage,
                Mode = step.Mode,
                ExecutionOrder = step.ExecutionOrder,
                FilteringAttributes = step.FilteringAttributes,
                Configuration = step.Configuration,
                IsEnabled = !step.IsEnabled,
                Description = step.Description,
                Deployment = step.Deployment,
                ImpersonatingUserId = step.ImpersonatingUserId,
                ImpersonatingUserName = step.ImpersonatingUserName,
                AsyncAutoDelete = step.AsyncAutoDelete,
                PluginTypeId = step.PluginTypeId,
                PluginTypeName = step.PluginTypeName,
                IsManaged = step.IsManaged,
                IsCustomizable = step.IsCustomizable,
                CreatedOn = step.CreatedOn,
                ModifiedOn = step.ModifiedOn
            };

            Application.MainLoop?.Invoke(() =>
            {
                selected.IsEnabled = updatedStep.IsEnabled;
                selected.Info = updatedStep;
                _tree.RefreshObject(selected);
                UpdateDetailPanel(selected);
                _statusLabel.Text = updatedStep.IsEnabled
                    ? $"Step enabled: {step.Name}"
                    : $"Step disabled: {step.Name}";
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to toggle step", ex, "PluginReg.Toggle");
                _statusLabel.Text = "Error toggling step";
            });
        }
    }

    private async Task UnregisterSelectedAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl)) return;

        var selected = _tree.SelectedObject as PluginTreeNode;
        if (selected == null) return;

        var (entityLabel, isSupported) = selected.NodeType switch
        {
            "package" => ("package", true),
            "assembly" => ("assembly", true),
            "type" => ("plugin type", true),
            "step" => ("step", true),
            "image" => ("image", true),
            "webhook" => ("webhook", true),
            "serviceEndpoint" => ("service endpoint", true),
            "customApi" => ("custom API", true),
            "dataSource" => ("data source", true),
            "dataProvider" => ("data provider", true),
            _ => ("", false)
        };

        if (!isSupported) return;

        var confirmResult = false;
        Application.MainLoop?.Invoke(() =>
        {
            if (_isShowingDialog) return;
            _isShowingDialog = true;

            var result = MessageBox.Query(
                "Confirm Unregister",
                $"Unregister {entityLabel} '{selected.DisplayName}'?\nThis will cascade-delete all child registrations.",
                "Unregister", "Cancel");

            _isShowingDialog = false;
            confirmResult = result == 0;
        });

        if (!confirmResult) return;

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Unregistering {entityLabel}: {selected.DisplayName}...";
            });

            var provider = await GetProviderAsync(ScreenCancellation);
            var regService = provider.GetRequiredService<IPluginRegistrationService>();
            var endpointService = provider.GetRequiredService<IServiceEndpointService>();
            var customApiService = provider.GetRequiredService<ICustomApiService>();
            var dataProviderService = provider.GetRequiredService<IDataProviderService>();

            string resultMessage;

            switch (selected.NodeType)
            {
                case "package":
                    var pkgResult = await regService.UnregisterPackageAsync(selected.Id, force: true, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered package. {pkgResult.TotalDeleted} item(s) removed.";
                    break;
                case "assembly":
                    var asmResult = await regService.UnregisterAssemblyAsync(selected.Id, force: true, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered assembly. {asmResult.TotalDeleted} item(s) removed.";
                    break;
                case "type":
                    var typeResult = await regService.UnregisterPluginTypeAsync(selected.Id, force: true, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered plugin type. {typeResult.TotalDeleted} item(s) removed.";
                    break;
                case "step":
                    var stepResult = await regService.UnregisterStepAsync(selected.Id, force: true, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered step. {stepResult.TotalDeleted} item(s) removed.";
                    break;
                case "image":
                    var imgResult = await regService.UnregisterImageAsync(selected.Id, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered image '{imgResult.EntityName}'.";
                    break;
                case "webhook":
                case "serviceEndpoint":
                    await endpointService.UnregisterAsync(selected.Id, force: true, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered {entityLabel} '{selected.DisplayName}'.";
                    break;
                case "customApi":
                    await customApiService.UnregisterAsync(selected.Id, force: true, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered custom API '{selected.DisplayName}'.";
                    break;
                case "dataSource":
                    await dataProviderService.UnregisterDataSourceAsync(selected.Id, cancellationToken: ScreenCancellation);
                    resultMessage = $"Unregistered data source '{selected.DisplayName}'.";
                    break;
                case "dataProvider":
                    await dataProviderService.UnregisterDataProviderAsync(selected.Id, ScreenCancellation);
                    resultMessage = $"Unregistered data provider '{selected.DisplayName}'.";
                    break;
                default:
                    return;
            }

            // Refresh tree after deletion
            await LoadRootAsync();

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = resultMessage;
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError($"Failed to unregister {entityLabel}", ex, "PluginReg.Unregister");
                _statusLabel.Text = $"Error unregistering {entityLabel}";
            });
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (string.IsNullOrEmpty(EnvironmentUrl)) return;

        var selected = _tree.SelectedObject as PluginTreeNode;
        if (selected == null) return;

        bool isPackage = selected.NodeType == "package";
        bool isAssembly = selected.NodeType == "assembly";

        if (!isPackage && !isAssembly)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Select a package or assembly node to download.");
            });
            return;
        }

        try
        {
            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Downloading {selected.DisplayName}...";
            });

            var provider = await GetProviderAsync(ScreenCancellation);
            var regService = provider.GetRequiredService<IPluginRegistrationService>();

            byte[] content;
            string fileName;

            if (isPackage)
            {
                (content, fileName) = await regService.DownloadPackageAsync(selected.Id, ScreenCancellation);
            }
            else
            {
                (content, fileName) = await regService.DownloadAssemblyAsync(selected.Id, ScreenCancellation);
            }

            // Show save dialog on UI thread
            string? savePath = null;
            Application.MainLoop?.Invoke(() =>
            {
                if (_isShowingDialog) return;
                _isShowingDialog = true;

                using var saveDialog = new SaveDialog("Save Binary", fileName)
                {
                    ColorScheme = TuiColorPalette.Default
                };
                Application.Run(saveDialog);
                savePath = saveDialog.FilePath?.ToString();

                _isShowingDialog = false;
            });

            if (string.IsNullOrEmpty(savePath)) return;

            await File.WriteAllBytesAsync(savePath, content, ScreenCancellation);

            Application.MainLoop?.Invoke(() =>
            {
                _statusLabel.Text = $"Saved {fileName} ({content.Length:N0} bytes) to {Path.GetFileName(savePath)}";
            });
        }
        catch (OperationCanceledException) { /* screen closing */ }
        catch (Exception ex)
        {
            Application.MainLoop?.Invoke(() =>
            {
                ErrorService.ReportError("Failed to download binary", ex, "PluginReg.Download");
                _statusLabel.Text = "Error downloading binary";
            });
        }
    }

    #endregion

    protected override void OnDispose()
    {
        _tree.SelectionChanged -= OnSelectionChanged;
        _tree.ObjectActivated -= OnNodeActivated;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}

/// <summary>
/// Tree node for the PluginRegistrationScreen tree view.
/// </summary>
internal sealed class PluginTreeNode : ITreeNode
{
    /// <summary>
    /// Node type: package, assembly, type, step, image, webhook, serviceEndpoint, customApi, dataSource, dataProvider, loading.
    /// </summary>
    public string NodeType { get; set; } = "";

    /// <summary>The Dataverse entity ID.</summary>
    public Guid Id { get; set; }

    /// <summary>Display name for this node.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Whether this entity is part of a managed solution.</summary>
    public bool IsManaged { get; set; }

    /// <summary>Whether this step is enabled (steps only; null for non-steps).</summary>
    public bool? IsEnabled { get; set; }

    /// <summary>Whether this node's children have been loaded from Dataverse.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>The underlying info record for the detail panel.</summary>
    public object? Info { get; set; }

    // ITreeNode implementation
    // ITreeNode.Text requires get+set; we compute on get and ignore set (display is dynamic)
    public string Text
    {
        get => FormatDisplayText();
        set { /* display text is computed from node state, setter is a no-op */ }
    }

    public IList<ITreeNode> Children { get; } = new List<ITreeNode>();

    // ITreeNode Tag (optional metadata storage)
    public object? Tag { get; set; }

    private string FormatDisplayText()
    {
        if (NodeType == "loading") return "  Loading...";

        var icon = NodeType switch
        {
            "package" => "[PKG]",
            "assembly" => "[ASM]",
            "type" => "[TYPE]",
            "step" => "[STEP]",
            "image" => "[IMG]",
            "webhook" => "[WH]",
            "serviceEndpoint" => "[SVC]",
            "customApi" => "[API]",
            "dataSource" => "[DS]",
            "dataProvider" => "[DP]",
            _ => "[?]"
        };

        var suffix = IsEnabled == false ? " [disabled]" : "";
        suffix += IsManaged ? " (managed)" : "";

        return $"{icon} {DisplayName}{suffix}";
    }
}
