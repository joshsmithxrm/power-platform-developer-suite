# PPDS.TUI: Dialogs

## Overview

The Dialogs subsystem provides modal dialog components for the TUI, all inheriting from the `TuiDialog` base class. Dialogs implement standardized behaviors including escape handling, color scheme application, and hotkey registry integration. Each dialog implements `ITuiStateCapture<TState>` for autonomous testing. Dialogs cover authentication, profile management, environment selection, export, query history, and informational displays.

## Public API

### Base Class

| Class | Purpose |
|-------|---------|
| `TuiDialog` | Base class providing escape handling, color scheme, hotkey registry integration |

### Dialog Catalog

| Dialog | Purpose | State Record |
|--------|---------|--------------|
| `AboutDialog` | Product info, version, links | `AboutDialogState` |
| `KeyboardShortcutsDialog` | Displays all keyboard shortcuts | `KeyboardShortcutsDialogState` |
| `PreAuthenticationDialog` | Prompts before interactive auth (browser vs device code) | `PreAuthenticationDialogState` |
| `ReAuthenticationDialog` | Handles token refresh when 401 occurs | `ReAuthenticationDialogState` |
| `ProfileSelectorDialog` | List/select/rename/delete profiles | `ProfileSelectorDialogState` |
| `ProfileCreationDialog` | Create new auth profile | `ProfileCreationDialogState` |
| `ProfileDetailsDialog` | View active profile details | `ProfileDetailsDialogState` |
| `ClearAllProfilesDialog` | Confirm deletion of all profiles | `ClearAllProfilesDialogState` |
| `EnvironmentSelectorDialog` | List/select environments | `EnvironmentSelectorDialogState` |
| `EnvironmentDetailsDialog` | View environment details | `EnvironmentDetailsDialogState` |
| `ExportDialog` | Export results to CSV/TSV/JSON/clipboard | `ExportDialogState` |
| `QueryHistoryDialog` | Browse/search query history | `QueryHistoryDialogState` |
| `FetchXmlPreviewDialog` | View transpiled FetchXML | `FetchXmlPreviewDialogState` |
| `ErrorDetailsDialog` | View errors and debug log | `ErrorDetailsDialogState` |

## Behaviors

### TuiDialog Base Class

The base class provides standardized behavior:

```csharp
internal abstract class TuiDialog : Dialog
{
    protected TuiDialog(string title, InteractiveSession? session = null)
    {
        ColorScheme = TuiColorPalette.Default;
        _hotkeyRegistry?.SetActiveDialog(this);
        // Escape key handling
    }

    protected virtual void OnEscapePressed() => Application.RequestStop();

    protected override void Dispose(bool disposing)
    {
        _hotkeyRegistry?.SetActiveDialog(null);
        base.Dispose(disposing);
    }
}
```

### Standard Features

| Feature | Behavior |
|---------|----------|
| Color scheme | `TuiColorPalette.Default` applied automatically |
| Escape handling | Closes dialog by default, overridable |
| Hotkey registry | Blocks screen-scope hotkeys while dialog is open |
| Disposal | Clears active dialog from hotkey registry |

### Dialog Categories

#### Authentication Dialogs

| Dialog | Trigger | Actions |
|--------|---------|---------|
| `PreAuthenticationDialog` | Before interactive auth | Open Browser, Use Device Code, Cancel |
| `ReAuthenticationDialog` | Token expired (401) | Re-authenticate, Cancel |

#### Profile Management Dialogs

| Dialog | Features |
|--------|----------|
| `ProfileSelectorDialog` | List profiles, select, rename (F2), delete (Del), create new, clear all |
| `ProfileCreationDialog` | Auth method selection, environment picker, profile naming |
| `ProfileDetailsDialog` | Shows active profile: auth method, identity, token expiration |
| `ClearAllProfilesDialog` | Confirmation with count, clears all profiles |

#### Environment Dialogs

| Dialog | Features |
|--------|----------|
| `EnvironmentSelectorDialog` | Discover environments, select, manual URL entry |
| `EnvironmentDetailsDialog` | Environment URL, display name, type |

#### Data Dialogs

| Dialog | Features |
|--------|----------|
| `ExportDialog` | Format selection (CSV/TSV/JSON/Clipboard), include headers option |
| `QueryHistoryDialog` | List history, search, preview, run, copy, delete |
| `FetchXmlPreviewDialog` | Read-only FetchXML view, copy to clipboard |

#### Informational Dialogs

| Dialog | Features |
|--------|----------|
| `AboutDialog` | Product name, version, docs URL, GitHub URL, copyright |
| `KeyboardShortcutsDialog` | Global and screen-specific shortcuts table |
| `ErrorDetailsDialog` | Recent errors list, copy error, open log file |

### Async Patterns

Dialogs use fire-and-forget with `ContinueWith` for error handling:

```csharp
_ = LoadProfilesAsync().ContinueWith(t =>
{
    if (t.IsFaulted)
    {
        Application.MainLoop?.Invoke(() =>
        {
            _statusLabel.Text = $"Error: {t.Exception?.InnerException?.Message}";
        });
    }
}, TaskScheduler.Default);
```

### State Capture

All dialogs implement `ITuiStateCapture<TState>`:

```csharp
public ProfileSelectorDialogState CaptureState()
{
    return new ProfileSelectorDialogState(
        Title: Title?.ToString() ?? string.Empty,
        Profiles: profileNames,
        SelectedIndex: selectedIndex,
        IsLoading: _isLoading,
        ErrorMessage: _errorMessage);
}
```

## Edge Cases

| Scenario | Behavior | Notes |
|----------|----------|-------|
| Empty list | Shows "(No items)" placeholder | ListView displays message |
| Loading state | Shows spinner or "Loading..." | Prevents interaction until ready |
| Error during load | Shows error in status label | Dialog remains open |
| Escape while loading | Closes dialog | Cancels any pending operations |
| Delete active profile | Extra warning shown | "You will be signed out" |
| No session provided | Details button disabled | Some features require session |
| Clipboard failure | Shows error message | Platform-specific behavior |

## Error Handling

### Dialog Error Display

| Error Source | Display Location |
|--------------|------------------|
| Load failure | Status label at bottom |
| Action failure | MessageBox.ErrorQuery |
| Validation | Inline message or MessageBox |

### Exception Types

| Exception | Handling |
|-----------|----------|
| `PpdsException` | Shows `UserMessage` property |
| Other exceptions | Shows `Message` property |

## Dependencies

- **Internal**:
  - `PPDS.Cli.Tui.Infrastructure.TuiColorPalette` - Color schemes
  - `PPDS.Cli.Tui.Infrastructure.IHotkeyRegistry` - Hotkey management
  - `PPDS.Cli.Services.*` - Application services
- **External**:
  - `Terminal.Gui.Dialog` - Base dialog class
  - `Terminal.Gui` controls - ListView, Button, TextField, etc.

## Configuration

### Dialog Dimensions

| Dialog | Width | Height |
|--------|-------|--------|
| `AboutDialog` | 70 | 22 |
| `ProfileSelectorDialog` | 60 | 19 |
| `ExportDialog` | 50 | 15 |
| `QueryHistoryDialog` | 80 | 22 |
| Varies | Adapted to content | |

### Export Formats

| Format Index | Name | Extension |
|--------------|------|-----------|
| 0 | CSV | .csv |
| 1 | TSV | .tsv |
| 2 | JSON | .json |
| 3 | Clipboard | N/A |

## Thread Safety

- Dialog instances are created and used on UI thread
- Async operations marshal back to UI via `Application.MainLoop.Invoke()`
- `ContinueWith` with `TaskScheduler.Default` for error handling
- State capture can be called from any thread (returns immutable record)

## Keyboard Shortcuts

### ProfileSelectorDialog

| Key | Action |
|-----|--------|
| F2 | Rename selected profile |
| Del | Delete selected profile |
| Ctrl+D | Show profile details |
| Enter | Select profile |
| Esc | Cancel |

### QueryHistoryDialog

| Key | Action |
|-----|--------|
| Enter | Run selected query |
| Esc | Cancel |

### ExportDialog

| Key | Action |
|-----|--------|
| Enter | Trigger export (on RadioGroup) |
| Esc | Cancel |

## Related

- [PPDS.TUI: Architecture](01-architecture.md) - Shell and dialog interaction
- [PPDS.TUI: Testing Harness](02-testing-harness.md) - State capture pattern

## Source Files

| File | Purpose |
|------|---------|
| `src/PPDS.Cli/Tui/Dialogs/TuiDialog.cs` | Base dialog class |
| `src/PPDS.Cli/Tui/Dialogs/AboutDialog.cs` | Product information |
| `src/PPDS.Cli/Tui/Dialogs/KeyboardShortcutsDialog.cs` | Shortcuts display |
| `src/PPDS.Cli/Tui/Dialogs/PreAuthenticationDialog.cs` | Pre-auth prompt |
| `src/PPDS.Cli/Tui/Dialogs/ReAuthenticationDialog.cs` | Re-auth prompt |
| `src/PPDS.Cli/Tui/Dialogs/ProfileSelectorDialog.cs` | Profile management |
| `src/PPDS.Cli/Tui/Dialogs/ProfileCreationDialog.cs` | New profile wizard |
| `src/PPDS.Cli/Tui/Dialogs/ProfileDetailsDialog.cs` | Profile info display |
| `src/PPDS.Cli/Tui/Dialogs/ClearAllProfilesDialog.cs` | Clear all confirmation |
| `src/PPDS.Cli/Tui/Dialogs/EnvironmentSelectorDialog.cs` | Environment picker |
| `src/PPDS.Cli/Tui/Dialogs/EnvironmentDetailsDialog.cs` | Environment info |
| `src/PPDS.Cli/Tui/Dialogs/ExportDialog.cs` | Export configuration |
| `src/PPDS.Cli/Tui/Dialogs/QueryHistoryDialog.cs` | History browser |
| `src/PPDS.Cli/Tui/Dialogs/FetchXmlPreviewDialog.cs` | FetchXML viewer |
| `src/PPDS.Cli/Tui/Dialogs/ErrorDetailsDialog.cs` | Error/debug display |
| `src/PPDS.Cli/Tui/Testing/States/*DialogState.cs` | State records for testing |
| `tests/PPDS.Cli.Tests/Tui/Dialogs/DialogStateCaptureTests.cs` | Dialog state tests |
