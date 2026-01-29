# TUI Plugin Registration

**Status:** Draft
**Version:** 1.0
**Last Updated:** 2026-01-28
**Code:** None

---

## Overview

The Plugin Registration screen provides a hierarchical browser for Dataverse plugin registrations, serving as the TUI equivalent of the Plugin Registration Tool. It uses a tree view to navigate the Package/Assembly → Type → Step → Image hierarchy, with a detail panel showing properties of the selected node. Users can toggle step enabled state, unregister entities with cascade preview, and download assembly/package binaries.

### Goals

- **Hierarchical Navigation**: TreeView of Package/Assembly → Type → Step → Image with lazy-loading children
- **Quick Inspection**: Detail panel shows all properties of the selected node without leaving the tree
- **Step Management**: Toggle enabled/disabled state, update filtering attributes and execution order
- **Safe Unregistration**: Cascade preview via UnregisterResult before confirming destructive operations

### Non-Goals

- Plugin deployment from registrations.json (handled by CLI `ppds plugins deploy`)
- Assembly extraction from DLLs (handled by CLI `ppds plugins extract`)
- Plugin trace inspection (handled by [tui-plugin-traces.md](./tui-plugin-traces.md))
- Creating new registrations from scratch (use CLI imperative commands)

---

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        TuiShell                                  │
│          Tools > Plugin Registration (new menu item)             │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│                PluginRegistrationScreen                           │
│           (ITuiScreen + ITuiStateCapture)                        │
│                                                                  │
│  ┌────────────────────┬─────────────────────────────────────┐   │
│  │ TreeView            │ Detail Panel (FrameView)             │   │
│  │ ├─ Package A        │  Name: MyPlugin.AccountHandler       │   │
│  │ │  └─ Assembly A1   │  Message: Update                     │   │
│  │ │     ├─ Type T1    │  Entity: account                     │   │
│  │ │     │  ├─ Step S1 │  Stage: PostOperation                │   │
│  │ │     │  │  └─ Img  │  Mode: Synchronous                   │   │
│  │ │     │  └─ Step S2 │  Enabled: Yes                        │   │
│  │ │     └─ Type T2    │  FilteringAttributes: name,phone     │   │
│  │ ├─ Assembly B       │  ...                                 │   │
│  │ └─ Package C        │                                      │   │
│  ├────────────────────┴─────────────────────────────────────┤   │
│  │ Status: 3 packages, 5 assemblies, 12 types, 28 steps      │   │
│  └──────────────────────────────────────────────────────────┘   │
└──────────┬──────────────────────────────────────────────────────┘
           │
           ▼
┌──────────────────────────┐
│ ConfirmDestructiveAction │
│ Dialog (shared)          │
│ "Unregister Assembly A1? │
│  Will delete: 2 types,   │
│  4 steps, 3 images"      │
└──────────────────────────┘
           │
           ▼
┌─────────────────────────────────────────────────────────────────┐
│                IPluginRegistrationService                         │
│   37 methods: Query (7), Lookup (12), Create/Upsert (5),        │
│   Delete (3), Unregister (5), Download (2), Update (2),         │
│   Solution (1)                                                   │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│              IDataverseConnectionPool                             │
└─────────────────────────────────────────────────────────────────┘
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `PluginRegistrationScreen` | Main screen: tree view, detail panel, status line, hotkey registration |
| `ConfirmDestructiveActionDialog` | Shared dialog: configurable severity confirmation for destructive operations |

### Dependencies

- Depends on: [plugins.md](./plugins.md) for `IPluginRegistrationService` and all Info/Result types
- Depends on: [tui.md](./tui.md) for `ITuiScreen`, `TuiDialog`, `IHotkeyRegistry`, `ITuiErrorService`
- Uses patterns from: [architecture.md](./architecture.md) for Application Service boundary

---

## Specification

### Core Requirements

1. Screen implements `ITuiScreen` and `ITuiStateCapture<PluginRegistrationScreenState>`
2. Left pane shows `TreeView<PluginTreeNode>` with Package/Assembly → Type → Step → Image hierarchy
3. Right pane shows `FrameView` with detail properties of selected node, layout varies by node type
4. Tree nodes load children lazily on expand (avoid loading full hierarchy upfront)
5. Status line shows aggregate counts (packages, assemblies, types, steps)
6. PluginListOptions (IncludeHidden, IncludeMicrosoft) configurable via menu
7. All service calls run on background thread; UI updates via `Application.MainLoop.Invoke()`

### Primary Flows

**Browse Plugin Hierarchy:**

1. **Screen opens**: Load root nodes — packages via `ListPackagesAsync()` and standalone assemblies via `ListAssembliesAsync()`
2. **Expand package**: Loads assemblies via `ListAssembliesForPackageAsync(packageId)`
3. **Expand assembly**: Loads types via `ListTypesForAssemblyAsync(assemblyId)` or `ListTypesForPackageAsync(packageId)`
4. **Expand type**: Loads steps via `ListStepsForTypeAsync(typeId)`
5. **Expand step**: Loads images via `ListImagesForStepAsync(stepId)`
6. **Select node**: Right panel updates with full properties of selected node
7. **Refresh**: F5 reloads tree from root, preserving expanded state where possible

**Inspect Entity Details:**

1. **Select package node**: Detail shows PluginPackageInfo (7 properties)
2. **Select assembly node**: Detail shows PluginAssemblyInfo (10 properties)
3. **Select type node**: Detail shows PluginTypeInfo (8 properties)
4. **Select step node**: Detail shows PluginStepInfo (22 properties) — most detailed view
5. **Select image node**: Detail shows PluginImageInfo (12 properties)

**Toggle Step State:**

1. **Select step node**: Step detail shows Enabled status prominently
2. **Toggle**: Space or dedicated hotkey toggles enabled/disabled
3. **Update**: Calls `UpdateStepAsync(stepId, new StepUpdateRequest(Mode: ...))` on background thread
4. **Refresh node**: Tree icon and detail panel update to reflect new state

**Unregister Entity:**

1. **Select node**: Any node type in the tree
2. **Delete key**: Opens `ConfirmDestructiveActionDialog`
3. **Preview**: Dialog shows what will be deleted (cascade counts from `UnregisterResult` type shape)
4. **Confirm**: For high-impact (assembly/package), requires typing "DELETE" to confirm
5. **Execute**: Calls appropriate `Unregister*Async(id, force: true)`
6. **Refresh**: Removed node and its children disappear from tree

**Download Binary:**

1. **Select assembly or package node**: Download option available in menu
2. **Trigger**: Ctrl+D or menu item
3. **Save dialog**: Prompts for output path (defaults to `{name}.dll` or `{name}.nupkg`)
4. **Download**: Calls `DownloadAssemblyAsync` or `DownloadPackageAsync`
5. **Status**: Shows download progress in status line

### Constraints

- Lazy-load children: never load full hierarchy at startup (environments may have hundreds of assemblies)
- Cache loaded children per session; invalidate on manual refresh (F5)
- Managed components (IsManaged=true) should display differently (dimmed or with icon) and block unregistration
- Tree operations must not block the UI thread

### Validation Rules

| Field | Rule | Error |
|-------|------|-------|
| Unregister managed | IsManaged must be false | "Cannot unregister managed components" |
| Update step rank | 1 ≤ value ≤ 999999 | "Execution order must be between 1 and 999999" |
| Download path | Valid file path | "Invalid output path" |

---

## Core Types

### PluginRegistrationScreen

Main screen for plugin registration browsing and management.

```csharp
internal sealed class PluginRegistrationScreen
    : ITuiScreen, ITuiStateCapture<PluginRegistrationScreenState>
{
    public PluginRegistrationScreen(
        InteractiveSession session,
        ITuiErrorService errorService,
        Action<DeviceCodeInfo>? deviceCodeCallback = null);

    // ITuiScreen
    public View Content { get; }
    public string Title { get; }  // "Plugin Registration - {environment}"
    public MenuBarItem[]? ScreenMenuItems { get; }
    public Action? ExportAction { get; }

    // ITuiStateCapture
    public PluginRegistrationScreenState CaptureState();
}
```

### PluginRegistrationScreenState

```csharp
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

### PluginNodeType

Enum for tree node types.

```csharp
public enum PluginNodeType
{
    Package,
    Assembly,
    Type,
    Step,
    Image
}
```

### PluginTreeNode

Internal node model for the TreeView.

```csharp
internal sealed class PluginTreeNode
{
    public PluginNodeType NodeType { get; init; }
    public Guid Id { get; init; }
    public string DisplayName { get; init; }
    public bool IsManaged { get; init; }
    public bool IsEnabled { get; init; }  // Steps only
    public bool IsLoaded { get; init; }   // Children loaded?
    public object Info { get; init; }     // Underlying Info type
}
```

### ConfirmDestructiveActionDialog

Shared reusable dialog for confirming destructive operations across all TUI screens. Configurable severity levels control the confirmation UX.

```csharp
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

### ConfirmationSeverity

```csharp
public enum ConfirmationSeverity
{
    Normal,     // OK/Cancel buttons
    High        // Must type "DELETE" to confirm
}
```

### ConfirmDestructiveActionDialogState

```csharp
public sealed record ConfirmDestructiveActionDialogState(
    string Title,
    string Message,
    string? ImpactSummary,
    ConfirmationSeverity Severity,
    string? ConfirmationText,   // Text the user has typed (High severity)
    bool IsConfirmed);
```

### Usage Pattern

```csharp
// TuiShell creates and navigates to the screen
var screen = new PluginRegistrationScreen(_session, _errorService, _deviceCodeCallback);
NavigateTo(screen);

// Screen loads root nodes:
var provider = await _session.GetServiceProviderAsync(_environmentUrl);
var regService = provider.GetRequiredService<IPluginRegistrationService>();
var packages = await regService.ListPackagesAsync(options: listOptions);
var assemblies = await regService.ListAssembliesAsync(options: listOptions);

// Lazy-load on expand:
var types = await regService.ListTypesForAssemblyAsync(assemblyId);

// Unregister with confirmation:
var dialog = new ConfirmDestructiveActionDialog(
    "Unregister Assembly",
    $"Unregister '{assemblyName}'?",
    impactSummary: "Will delete: 3 types, 8 steps, 4 images",
    severity: ConfirmationSeverity.High);
Application.Run(dialog);
if (dialog.IsConfirmed)
{
    var result = await regService.UnregisterAssemblyAsync(assemblyId, force: true);
}
```

---

## Error Handling

### Error Types

| Error | Condition | Recovery |
|-------|-----------|----------|
| Service unavailable | No connection to environment | Status line error; retry with F5 |
| Authentication expired | Token expired mid-session | Re-authentication dialog |
| Managed component | Attempted unregister of managed component | Block action; show explanation |
| Cascade constraint | Unregister without force (should not happen in TUI — always uses force) | N/A |
| Download failed | Binary too large or network error | Status line error with retry hint |

### Recovery Strategies

- **Connection errors**: Display error in status line, F5 to retry
- **Auth errors**: Intercept `PpdsAuthException`, show `ReAuthenticationDialog`, retry
- **Managed blocks**: Show inline message explaining managed components cannot be modified
- **Partial load failures**: Show loaded nodes, report error for failed expansion

### Edge Cases

| Scenario | Expected Behavior |
|----------|-------------------|
| No plugins registered | Empty tree with "No plugin registrations found" message |
| Assembly with 0 types | Assembly node expandable but shows "(no types)" placeholder |
| Step with IsEnabled=false | Dimmed display with strikethrough or "(disabled)" suffix |
| Package-deployed assembly | Assembly shown under package node, not at root |
| Very deep hierarchy | TreeView scrolls; no depth limit |

---

## Design Decisions

### Why TreeView for Plugin Hierarchy?

**Context:** Plugin registrations form a natural tree: Package → Assembly → Type → Step → Image. Could display as flat tables or nested tables.

**Decision:** Use Terminal.Gui `TreeView<T>` control for hierarchical navigation.

**Alternatives considered:**
- Flat DataTableView with drill-down (click row to see children): Rejected — loses parent context, requires back-navigation
- Multiple linked DataTableViews (master-detail chain): Rejected — too complex for 5-level hierarchy
- Expandable list with indentation: Possible but TreeView provides collapse/expand semantics natively

**Consequences:**
- Positive: Natural mapping from data model to UI
- Positive: Collapse/expand preserves context and orientation
- Positive: First TreeView usage establishes pattern for future tree-based screens
- Negative: Terminal.Gui TreeView<T> API requires custom ITreeBuilder<T> implementation

### Why Master-Detail Split Layout?

**Context:** Detail properties vary by node type (7 to 22 properties). Need to show details without leaving the tree.

**Decision:** Horizontal split — TreeView on left (~40% width), FrameView detail panel on right (~60% width).

**Alternatives considered:**
- Detail as modal dialog: Rejected — too much friction for browsing, requires open/close per node
- Detail below tree (vertical split): Rejected — tree needs vertical space for hierarchy depth
- Tooltip/popup on hover: Rejected — Terminal.Gui hover support is limited

**Consequences:**
- Positive: Select a node, see its details instantly
- Positive: Can compare parent/child by scrolling tree while detail is visible
- Negative: Horizontal space split may be tight on 80-column terminals
- Mitigation: Minimum terminal width documented; detail panel wraps gracefully

### Why Lazy-Loading Children?

**Context:** Large environments may have hundreds of assemblies, thousands of types. Loading the full tree upfront would be slow and wasteful.

**Decision:** Load children on first expand. Cache per session. F5 clears cache and reloads.

**Alternatives considered:**
- Eager load everything: Rejected — could take 30+ seconds on large environments
- Background pre-fetch: Possible optimization for later; adds complexity

**Consequences:**
- Positive: Fast initial load (only root nodes)
- Positive: Only fetches data user actually browses
- Negative: Slight delay on first expand of each node
- Mitigation: Spinner icon on expanding node, replace with children when loaded

### Why Shared ConfirmDestructiveActionDialog?

**Context:** Multiple screens need destructive action confirmation: plugin unregistration (with cascade), trace deletion, solution operations.

**Decision:** Single reusable dialog with configurable severity (Normal: OK/Cancel, High: type-to-confirm).

**Alternatives considered:**
- Per-screen confirmation dialogs: Rejected — duplication, inconsistent UX
- MessageBox.Query (Terminal.Gui built-in): Rejected — no type-to-confirm support for high-impact actions
- Existing `ClearAllProfilesDialog` as base: Narrow implementation; generic version is more reusable

**Consequences:**
- Positive: Consistent confirmation UX across all screens
- Positive: High-severity mode prevents accidental destructive operations
- Negative: Generic dialog may need customization hooks for screen-specific impact summaries

---

## Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| IncludeHidden | bool | false | Show hidden plugin types |
| IncludeMicrosoft | bool | false | Show Microsoft-published components |
| TreeSplitPosition | int | 40 | Tree pane width as percentage |

---

## Testing

### Acceptance Criteria

- [ ] Screen loads root nodes (packages and standalone assemblies) on activation
- [ ] Expanding a package node loads its assemblies
- [ ] Expanding an assembly node loads its types
- [ ] Expanding a type node loads its steps
- [ ] Expanding a step node loads its images
- [ ] Selecting a node shows its properties in the detail panel
- [ ] Detail panel layout changes based on node type (Package/Assembly/Type/Step/Image)
- [ ] Space on step node toggles enabled state via UpdateStepAsync
- [ ] Delete key opens ConfirmDestructiveActionDialog with cascade preview
- [ ] High severity confirmation requires typing "DELETE"
- [ ] Managed components cannot be unregistered (action blocked with message)
- [ ] F5 refreshes tree from root
- [ ] State capture returns accurate PluginRegistrationScreenState

### Edge Cases

| Scenario | Input | Expected Output |
|----------|-------|-----------------|
| Empty environment | No plugins registered | Empty tree, status "0 packages, 0 assemblies" |
| Package with no assemblies | ListAssembliesForPackage returns empty | Package shows "(empty)" placeholder child |
| Unregister root package | Package with deep hierarchy | High severity confirmation with total cascade count |
| Rapid expand-collapse | Expand then immediately collapse | Cancel pending load, don't show stale children |
| Concurrent modification | Another user deletes a type | Node shows error on expand; refresh resolves |

### Test Examples

```csharp
[Fact]
[Trait("Category", "TuiUnit")]
public void PluginRegistrationScreen_CapturesInitialState()
{
    var session = CreateMockSession();
    var screen = new PluginRegistrationScreen(session, new TuiErrorService());

    var state = screen.CaptureState();

    Assert.Equal(0, state.PackageCount);
    Assert.Equal(0, state.AssemblyCount);
    Assert.Null(state.SelectedNodeType);
    Assert.False(state.IsLoading);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void ConfirmDestructiveActionDialog_NormalSeverity_ConfirmsOnOk()
{
    var dialog = new ConfirmDestructiveActionDialog(
        "Delete Item", "Are you sure?", severity: ConfirmationSeverity.Normal);

    var state = dialog.CaptureState();

    Assert.Equal(ConfirmationSeverity.Normal, state.Severity);
    Assert.False(state.IsConfirmed);
}

[Fact]
[Trait("Category", "TuiUnit")]
public void ConfirmDestructiveActionDialog_HighSeverity_RequiresTypedConfirmation()
{
    var dialog = new ConfirmDestructiveActionDialog(
        "Unregister Assembly", "Delete all children?",
        impactSummary: "3 types, 8 steps, 4 images",
        severity: ConfirmationSeverity.High);

    var state = dialog.CaptureState();

    Assert.Equal(ConfirmationSeverity.High, state.Severity);
    Assert.Null(state.ConfirmationText);
    Assert.False(state.IsConfirmed);
}
```

---

## Related Specs

- [plugins.md](./plugins.md) - Backend service: IPluginRegistrationService (37 methods), all Info/Config types
- [tui.md](./tui.md) - TUI framework: ITuiScreen, TuiDialog, IHotkeyRegistry, state capture
- [tui-plugin-traces.md](./tui-plugin-traces.md) - Related screen: plugin trace investigation
- [architecture.md](./architecture.md) - Application Service boundary pattern
- [connection-pooling.md](./connection-pooling.md) - Pooled clients for Dataverse queries

---

## Roadmap

- Inline step editing (filtering attributes, execution order) without separate dialog
- Drag-and-drop reordering of step execution order
- Right-click context menu for node actions
- Direct navigation from step to its trace history (link to PluginTraceScreen with filter)
- Diff view comparing local registrations.json against live environment
