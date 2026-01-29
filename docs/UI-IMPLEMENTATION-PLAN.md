# UI Implementation Plan

**Generated:** 2026-01-28
**Source Specs:** specs/tui-plugin-traces.md, specs/tui-plugin-registration.md, specs/tui-solutions.md, specs/tui-environment-dashboard.md, specs/tui-migration.md
**Framework Reference:** specs/tui.md

---

## Implementation Order

| Priority | Screen | Complexity | Dependencies |
|----------|--------|------------|--------------|
| 0 | ConfirmDestructiveActionDialog | Low | None (shared component) |
| 1 | PluginTraceScreen | Medium | IPluginTraceService |
| 2 | PluginRegistrationScreen | High | IPluginRegistrationService |
| 3 | SolutionScreen | Medium | ISolutionService, IImportJobService |
| 4a | EnvironmentDashboardScreen | High | 5 services |
| 4b | MigrationScreen | High | IExporter, IImporter, executors |

---

## Shared Component: ConfirmDestructiveActionDialog

**Purpose:** Reusable confirmation dialog with configurable severity levels.

### Files

```
src/PPDS.Cli/Tui/
├── Dialogs/ConfirmDestructiveActionDialog.cs
├── Models/ConfirmationSeverity.cs
└── Testing/States/ConfirmDestructiveActionDialogState.cs
```

### Implementation

```csharp
public enum ConfirmationSeverity { Normal, High }

public sealed record ConfirmDestructiveActionDialogState(
    string Title,
    string Message,
    string? ImpactSummary,
    ConfirmationSeverity Severity,
    string? ConfirmationText,
    bool IsConfirmed);

internal sealed class ConfirmDestructiveActionDialog
    : TuiDialog, ITuiStateCapture<ConfirmDestructiveActionDialogState>
{
    public ConfirmDestructiveActionDialog(
        string title,
        string message,
        string? impactSummary = null,
        ConfirmationSeverity severity = ConfirmationSeverity.Normal,
        InteractiveSession? session = null);

    public bool IsConfirmed { get; }
    public ConfirmDestructiveActionDialogState CaptureState();
}
```

### Behavior

- **Normal severity:** OK/Cancel buttons
- **High severity:** Must type "DELETE" to enable OK button

### Tests

```csharp
[Trait("Category", "TuiUnit")]
public void ConfirmDestructiveActionDialog_NormalSeverity_ConfirmsOnOk()
public void ConfirmDestructiveActionDialog_HighSeverity_RequiresTypedConfirmation()
```

---

## Priority 1: Plugin Traces Screen

**Spec:** specs/tui-plugin-traces.md
**Backend:** specs/plugin-traces.md (IPluginTraceService)

### Files

```
src/PPDS.Cli/Tui/
├── Screens/PluginTraceScreen.cs
├── Dialogs/
│   ├── PluginTraceDetailDialog.cs
│   ├── PluginTraceFilterDialog.cs
│   └── PluginTraceTimelineDialog.cs
└── Testing/States/
    ├── PluginTraceScreenState.cs
    ├── PluginTraceDetailDialogState.cs
    ├── PluginTraceFilterDialogState.cs
    └── PluginTraceTimelineDialogState.cs
```

### State Records

```csharp
public sealed record PluginTraceScreenState(
    int TraceCount,
    Guid? SelectedTraceId,
    string? SelectedTypeName,
    bool IsLoading,
    bool IsErrorsOnly,
    string? QuickFilterType,
    string? QuickFilterMessage,
    string? QuickFilterEntity,
    bool HasAdvancedFilter,
    string? StatusText,
    string? ErrorMessage);

public sealed record PluginTraceDetailDialogState(
    Guid TraceId, string TypeName, string? MessageName, string? PrimaryEntity,
    int? DurationMs, bool HasException, string? ExceptionText, string? MessageBlock,
    Guid? CorrelationId, int Depth);

public sealed record PluginTraceFilterDialogState(
    string? TypeName, string? MessageName, string? PrimaryEntity, string? Mode,
    string? OperationType, int? MinDepth, int? MaxDepth, DateTime? CreatedAfter,
    DateTime? CreatedBefore, int? MinDurationMs, int? MaxDurationMs, bool? HasException,
    Guid? CorrelationId, Guid? RequestId, Guid? PluginStepId, string? OrderBy, bool IsApplied);

public sealed record PluginTraceTimelineDialogState(
    int RootCount, int TotalNodeCount, Guid? SelectedTraceId,
    string? SelectedTypeName, long TotalDurationMs);
```

### Service Usage

```csharp
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var traceService = provider.GetRequiredService<IPluginTraceService>();

// List traces
var traces = await traceService.ListAsync(filter, top: 100);

// Get detail
var detail = await traceService.GetAsync(traceId);

// Build timeline
var timeline = await traceService.BuildTimelineAsync(correlationId);

// Delete
await traceService.DeleteAsync(traceIds, progress);
```

### Hotkeys

| Key | Scope | Action |
|-----|-------|--------|
| F5 | Screen | Refresh trace list |
| Ctrl+F | Screen | Open advanced filter dialog |
| Ctrl+T | Screen | Open timeline for selected trace |
| Enter | Screen | Open detail dialog |
| Delete | Screen | Delete selected traces (with confirmation) |

### Menu Integration

```csharp
// TuiShell.BuildToolsMenu()
new MenuItem("Plugin _Traces", "", () => NavigateTo(new PluginTraceScreen(_session, _errorService, _deviceCodeCallback)))
```

### Tests

```csharp
[Trait("Category", "TuiUnit")]
public void PluginTraceScreen_CapturesInitialState()
public void PluginTraceScreen_AppliesQuickFilter()
public void PluginTraceDetailDialog_CapturesExceptionState()
public void PluginTraceFilterDialog_DefaultsAllNull()
public void PluginTraceTimelineDialog_CapturesNodeCounts()
```

---

## Priority 2: Plugin Registration Screen

**Spec:** specs/tui-plugin-registration.md
**Backend:** specs/plugins.md (IPluginRegistrationService, 37 methods)

### Files

```
src/PPDS.Cli/Tui/
├── Screens/PluginRegistrationScreen.cs
├── Models/
│   ├── PluginNodeType.cs
│   └── PluginTreeNode.cs
└── Testing/States/PluginRegistrationScreenState.cs
```

### State Records

```csharp
public enum PluginNodeType { Package, Assembly, Type, Step, Image }

public sealed record PluginRegistrationScreenState(
    int PackageCount,
    int AssemblyCount,
    int ExpandedNodeCount,
    PluginNodeType? SelectedNodeType,
    Guid? SelectedNodeId,
    string? SelectedNodeName,
    bool IsLoading,
    bool IncludeHidden,
    bool IncludeMicrosoft,
    string? ErrorMessage);
```

### Service Usage

```csharp
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var regService = provider.GetRequiredService<IPluginRegistrationService>();

// Root nodes
var packages = await regService.ListPackagesAsync(options);
var assemblies = await regService.ListAssembliesAsync(options);

// Lazy load children
var types = await regService.ListTypesForAssemblyAsync(assemblyId);
var steps = await regService.ListStepsForTypeAsync(typeId);
var images = await regService.ListImagesForStepAsync(stepId);

// Toggle step
await regService.UpdateStepAsync(stepId, new StepUpdateRequest { Mode = newMode });

// Unregister (uses shared ConfirmDestructiveActionDialog)
var result = await regService.UnregisterAssemblyAsync(assemblyId, force: true);
```

### TreeView Pattern

```csharp
internal sealed class PluginTreeNode
{
    public PluginNodeType NodeType { get; init; }
    public Guid Id { get; init; }
    public string DisplayName { get; init; }
    public bool IsManaged { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsLoaded { get; init; }
    public object Info { get; init; }
}
```

### Hotkeys

| Key | Scope | Action |
|-----|-------|--------|
| F5 | Screen | Refresh tree |
| Space | Screen | Toggle step enabled state |
| Delete | Screen | Unregister with cascade preview |
| Ctrl+D | Screen | Download assembly/package |

### Menu Integration

```csharp
new MenuItem("Plugin _Registration", "", () => NavigateTo(new PluginRegistrationScreen(...)))
```

### Tests

```csharp
[Trait("Category", "TuiUnit")]
public void PluginRegistrationScreen_CapturesInitialState()
public void PluginRegistrationScreen_ExpandsPackageLoadsAssemblies()
```

---

## Priority 3: Solutions Screen

**Spec:** specs/tui-solutions.md
**Backend:** specs/dataverse-services.md (ISolutionService, IImportJobService)

### Files

```
src/PPDS.Cli/Tui/
├── Screens/SolutionScreen.cs
└── Testing/States/SolutionScreenState.cs
```

### State Record

```csharp
public sealed record SolutionScreenState(
    int SolutionCount,
    string? SelectedSolutionName,
    string? SelectedSolutionVersion,
    bool? SelectedIsManaged,
    int? ComponentCount,
    bool IsLoading,
    bool IsExporting,
    bool IsMonitoringImport,
    double? ImportProgress,
    bool ShowManaged,
    string? FilterText,
    string? ErrorMessage);
```

### Service Usage

```csharp
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var solutionService = provider.GetRequiredService<ISolutionService>();
var importJobService = provider.GetRequiredService<IImportJobService>();

// List solutions
var solutions = await solutionService.ListAsync();

// Get components
var components = await solutionService.GetComponentsAsync(solutionId);

// Export
var bytes = await solutionService.ExportAsync(uniqueName, managed: false);
File.WriteAllBytes(outputPath, bytes);

// Monitor import
await importJobService.WaitForCompletionAsync(jobId, onProgress: p => UpdateProgress(p));
```

### Hotkeys

| Key | Scope | Action |
|-----|-------|--------|
| F5 | Screen | Refresh solution list |
| Enter | Screen | View solution components |
| Ctrl+E | Screen | Export solution |
| Ctrl+I | Screen | Monitor active imports |

### Menu Integration

```csharp
new MenuItem("_Solutions", "", () => NavigateTo(new SolutionScreen(...)))
```

### Tests

```csharp
[Trait("Category", "TuiUnit")]
public void SolutionScreen_CapturesInitialState()
public void SolutionScreen_CapturesSelectedSolution()
```

---

## Priority 4a: Environment Dashboard Screen

**Spec:** specs/tui-environment-dashboard.md
**Backend:** specs/dataverse-services.md (5 services)

### Files

```
src/PPDS.Cli/Tui/
├── Screens/EnvironmentDashboardScreen.cs
├── Dialogs/
│   ├── RoleAssignmentDialog.cs
│   └── EnvVarEditDialog.cs
├── Models/DashboardSubView.cs
└── Testing/States/
    ├── EnvironmentDashboardScreenState.cs
    ├── RoleAssignmentDialogState.cs
    └── EnvVarEditDialogState.cs
```

### State Records

```csharp
public enum DashboardSubView { Users = 1, Flows = 2, EnvironmentVariables = 3, ConnectionReferences = 4 }

public sealed record EnvironmentDashboardScreenState(
    DashboardSubView ActiveSubView,
    int UserCount, int FlowCount, int EnvVarCount, int ConnRefCount,
    Guid? SelectedItemId, string? SelectedItemName,
    bool IsLoading, string? FilterText,
    bool ShowDisabledUsers, string? FlowStateFilter, bool UnboundOnly,
    string? ErrorMessage);

public sealed record RoleAssignmentDialogState(
    string UserName, int AvailableRoleCount, string? SelectedRoleName, string? FilterText);

public sealed record EnvVarEditDialogState(
    string SchemaName, string DisplayName, string Type,
    string? CurrentValue, string? NewValue, bool IsSecret, bool IsConfirmed);
```

### Service Usage

```csharp
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var userService = provider.GetRequiredService<IUserService>();
var roleService = provider.GetRequiredService<IRoleService>();
var flowService = provider.GetRequiredService<IFlowService>();
var envVarService = provider.GetRequiredService<IEnvironmentVariableService>();
var connRefService = provider.GetRequiredService<IConnectionReferenceService>();

// Users sub-view
var users = await userService.ListAsync(filter);
var roles = await userService.GetUserRolesAsync(userId);

// Flows sub-view
var flows = await flowService.ListAsync(state: flowStateFilter);

// Env vars sub-view
await envVarService.SetValueAsync(schemaName, newValue);

// Conn refs sub-view
var analysis = await connRefService.AnalyzeAsync();
```

### Sub-View Architecture

```
┌──────────────────────────────────────────────────────────┐
│ [1] Users   [2] Flows   [3] Env Vars   [4] Conn Refs    │ ← RadioGroup
├──────────────────────────────────────────────────────────┤
│ DataTableView (content varies by active sub-view)        │
├──────────────────────────────────────────────────────────┤
│ Detail Panel: properties of selected row                 │
└──────────────────────────────────────────────────────────┘
```

### Hotkeys

| Key | Scope | Action |
|-----|-------|--------|
| 1-4 | Screen | Switch sub-view |
| F5 | Screen | Refresh current sub-view |
| Ctrl+A | Screen | Assign role (Users sub-view) |
| Space | Screen | Toggle flow state (Flows sub-view) |
| Enter | Screen | Edit env var (EnvVars sub-view) |
| Ctrl+Shift+A | Screen | Analyze conn refs |

### Menu Integration

```csharp
new MenuItem("_Environment Dashboard", "", () => NavigateTo(new EnvironmentDashboardScreen(...)))
```

### Tests

```csharp
[Trait("Category", "TuiUnit")]
public void EnvironmentDashboardScreen_DefaultsToUsersSubView()
public void EnvironmentDashboardScreen_SwitchesSubView()
public void RoleAssignmentDialog_CapturesAvailableRoles()
public void EnvVarEditDialog_CapturesSecretFlag()
```

---

## Priority 4b: Data Migration Screen

**Spec:** specs/tui-migration.md
**Backend:** specs/migration.md, specs/bulk-operations.md

### Files

```
src/PPDS.Cli/Tui/
├── Screens/MigrationScreen.cs
├── Dialogs/ExecutionPlanPreviewDialog.cs
├── Infrastructure/TuiMigrationProgressReporter.cs
├── Models/
│   ├── MigrationMode.cs
│   └── MigrationOperationState.cs
└── Testing/States/
    ├── MigrationScreenState.cs
    └── ExecutionPlanPreviewDialogState.cs
```

### State Records

```csharp
public enum MigrationMode { Export, Import }
public enum MigrationOperationState { Idle, Configuring, PreviewingPlan, Running, Completed, Failed, Cancelled }

public sealed record MigrationScreenState(
    MigrationMode Mode,
    MigrationOperationState OperationState,
    string? SchemaPath, string? DataPath, string? OutputPath,
    string? CurrentPhase, string? CurrentEntity,
    int EntitiesProcessed, int EntitiesTotal,
    int RecordsProcessed, int RecordsTotal,
    double? ProgressPercent, double? RecordsPerSecond,
    TimeSpan? Elapsed, TimeSpan? EstimatedRemaining,
    int ErrorCount, int WarningCount, string? ErrorMessage);

public sealed record ExecutionPlanPreviewDialogState(
    int TierCount, int EntityCount, int DeferredFieldCount,
    int M2MRelationshipCount, bool IsApproved,
    IReadOnlyList<string> TierSummaries);
```

### TuiMigrationProgressReporter

```csharp
internal sealed class TuiMigrationProgressReporter : IProgressReporter
{
    public TuiMigrationProgressReporter(
        Action<ProgressEventArgs> onProgress,
        Action<MigrationResult> onComplete,
        Action<Exception, string?> onError);

    public string OperationName { get; set; }

    public void Report(ProgressEventArgs args)
    {
        Application.MainLoop.Invoke(() => _onProgress(args));
    }

    public void Complete(MigrationResult result)
    {
        Application.MainLoop.Invoke(() => _onComplete(result));
    }

    public void Error(Exception ex, string? context)
    {
        Application.MainLoop.Invoke(() => _onError(ex, context));
    }

    public void Reset() { /* Reset UI state */ }
}
```

### Service Usage

```csharp
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var exporter = provider.GetRequiredService<IExporter>();
var importer = provider.GetRequiredService<IImporter>();
var dataReader = provider.GetRequiredService<ICmtDataReader>();
var graphBuilder = provider.GetRequiredService<IDependencyGraphBuilder>();
var planBuilder = provider.GetRequiredService<IExecutionPlanBuilder>();

// Export
var progressReporter = new TuiMigrationProgressReporter(...);
using var cts = new CancellationTokenSource();
await exporter.ExportAsync(schemaPath, outputPath, exportOptions, progressReporter, cts.Token);

// Import with plan preview
var data = await dataReader.ReadAsync(dataPath);
var graph = graphBuilder.Build(data.Schema);
var plan = planBuilder.Build(graph, data.Schema);
// Show ExecutionPlanPreviewDialog
await importer.ImportAsync(data, plan, importOptions, progressReporter, cts.Token);
```

### Hotkeys

| Key | Scope | Action |
|-----|-------|--------|
| Ctrl+P | Screen | Preview execution plan (Import mode) |
| Ctrl+Enter | Screen | Start export/import |
| Escape | Screen | Cancel running operation |

### Menu Integration

```csharp
new MenuItem("Data _Migration", "", () => NavigateTo(new MigrationScreen(...)))
```

### Tests

```csharp
[Trait("Category", "TuiUnit")]
public void MigrationScreen_DefaultsToExportMode()
public void MigrationScreen_ValidatesSchemaPath()
public void ExecutionPlanPreviewDialog_CapturesTierCounts()
public void TuiMigrationProgressReporter_InvokesOnProgress()
```

---

## TuiShell Integration

### Menu Updates

In `TuiShell.BuildToolsMenu()`, replace placeholders with actual screens:

```csharp
private MenuItem[] BuildToolsMenu()
{
    return new MenuItem[]
    {
        new MenuItem("_SQL Query", "", () => NavigateTo(new SqlQueryScreen(...))),
        new MenuItem("Plugin _Traces", "", () => NavigateTo(new PluginTraceScreen(...))),
        new MenuItem("Plugin _Registration", "", () => NavigateTo(new PluginRegistrationScreen(...))),
        new MenuItem("_Solutions", "", () => NavigateTo(new SolutionScreen(...))),
        new MenuItem("_Environment Dashboard", "", () => NavigateTo(new EnvironmentDashboardScreen(...))),
        new MenuItem("Data _Migration", "", () => NavigateTo(new MigrationScreen(...))),
    };
}
```

---

## Testing Checklist

For each screen:

- [ ] Screen implements `ITuiScreen`
- [ ] Screen implements `ITuiStateCapture<TState>`
- [ ] All dialogs implement `ITuiStateCapture<TState>`
- [ ] State records cover all testable properties
- [ ] TuiUnit tests pass without Terminal.Gui initialization
- [ ] Menu item added to TuiShell
- [ ] Hotkeys registered in `OnActivated()`
- [ ] Background operations use `Application.MainLoop.Invoke()`
- [ ] Error handling via `ITuiErrorService.ReportError()`

---

## File Summary

| Category | Count |
|----------|-------|
| Screen classes | 5 |
| Dialog classes | 7 |
| State records | 12 |
| Model types | 6 |
| Infrastructure | 1 |
| **Total new files** | **31** |
