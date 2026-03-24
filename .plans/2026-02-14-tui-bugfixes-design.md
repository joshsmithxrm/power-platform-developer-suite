# TUI Bug Fixes — Design Document

**Date:** 2026-02-14
**Branch:** query-engine-v3

## Summary

Five user-reported bugs plus one discovered bug, all in the TUI layer.

## Fix 1: Device Code Login — Selectable Code

**Problem:** `MessageBox.Query()` in `ProfileCreationDialog.cs:497-512` doesn't allow text selection. `ClipboardHelper` auto-copy fails in WSL/SSH (no X11 display for `xclip`/`xsel`). User sees the code but can't copy it.

**Solution:** New `DeviceCodeDialog` replaces `MessageBox.Query()`:
- Read-only `TextField` containing the user code (selectable, Ctrl+C works)
- Verification URL shown as a label
- Still attempts `ClipboardHelper.CopyToClipboard()` with `(copied!)` indicator
- "OK" button to dismiss manually

**Files:** New `src/PPDS.Cli/Tui/Dialogs/DeviceCodeDialog.cs`, modify `ProfileCreationDialog.cs`

## Fix 2: Device Code Dialog Auto-Close on Auth Success

**Problem:** `MessageBox.Query()` is synchronous/modal — blocks the UI thread. When MSAL background polling succeeds, nothing can close the dialog. User must click OK with no signal that auth succeeded.

**Solution:** The new `DeviceCodeDialog` (from Fix 1) supports auto-close:
- Accepts a `CancellationToken` representing auth completion
- Registers a callback: when token fires, marshals `Application.RequestStop()` to the main loop
- Calling code in `ProfileCreationDialog` passes a `CancellationTokenSource` cancelled when `CreateProfileAsync` returns
- Matches browser auth behavior: auth completes → dialog closes → flow continues

**Files:** `DeviceCodeDialog.cs` (from Fix 1), `ProfileCreationDialog.cs` (callback wiring)

## Fix 3: Linux Keybinding Alternatives

**Problem:** `Ctrl+Shift+E/H/F` (`SqlQueryScreen.cs:337-348`) are indistinguishable from `Ctrl+E/H/F` on Linux terminals (terminal encodes both the same way).

**Solution:** Add cross-platform F-key alternatives alongside existing bindings:
- **F7** → Show Execution Plan (existing: `Ctrl+Shift+E`)
- **F8** → Query History (existing: `Ctrl+Shift+H`)
- **F9** → Show FetchXML (existing: `Ctrl+Shift+F`)

Keep `Ctrl+Shift` bindings for Windows. Update menu labels to show both shortcuts.

**Files:** `SqlQueryScreen.cs`

## Fix 4: TextInput Focus Visibility

**Problem:** `TuiColorPalette.TextInput` (line 67-74) has identical `Normal` and `Focus` colors (`Color.White, Color.Black`). No visual feedback when a TextField receives focus. Affects all TextFields across the TUI.

**Solution:** Change `TextInput.Focus` to `MakeAttr(Color.White, Color.DarkGray)` — subtle dark gray background on focus. Readable, distinct from unfocused state, not as aggressive as the cyan background used by `Default`.

**Affected views:** EnvironmentConfigDialog, ProfileCreationDialog, ExportDialog, ClearAllProfilesDialog, SqlQueryScreen filter field.

**Files:** `TuiColorPalette.cs` (one line)

## Fix 5: Type Field — Dropdown + Enum-Based Architecture

**Problem:** Type field on EnvironmentConfigDialog is a free-text `TextField`. Types drive protection levels and default colors — they should be constrained. Additionally, `DmlSafetyGuard.DetectProtectionLevel` does fragile string matching that silently fails for normalized type names.

### Model Changes

**Move `EnvironmentType` to `PPDS.Auth.Profiles`** (currently in `PPDS.Cli/Tui/Infrastructure`):
```csharp
public enum EnvironmentType
{
    Unknown,       // Auto-detect / not configured
    Production,
    Sandbox,
    Development,
    Test,
    Trial
}
```

**Update `EnvironmentConfig`:**
- Change `Type` from `string?` to `EnvironmentType?` (null = auto-detect)
- Add `DiscoveredType` (`string?`) — raw Discovery API value, stored separately
- Keep existing `Color` and `Protection` override fields

### Protection Level Mapping

`DmlSafetyGuard.DetectProtectionLevel` takes `EnvironmentType` instead of `string?`:

| EnvironmentType | ProtectionLevel | Behavior |
|----------------|-----------------|----------|
| Production | Production | Block DML by default, require preview + confirmation |
| Sandbox | Development | Unrestricted |
| Development | Development | Unrestricted |
| Test | Development | Unrestricted |
| Trial | Development | Unrestricted |
| Unknown | Development | Unrestricted |

Only Production is locked down. Users opt into protection by classifying environments as Production.

### Service Changes

- `EnvironmentConfigService.ResolveTypeAsync` returns `EnvironmentType` instead of `string?`
- Resolution priority: user config type > parse DiscoveredType > URL heuristics > Unknown
- `BuiltInTypeDefaults` keyed by `EnvironmentType` instead of string
- `NormalizeDiscoveryType` maps Discovery API strings to `EnvironmentType` enum

### Dialog Changes

- Replace `TextField` with `ListView` for Type field
- Options: `(Auto-detect)`, Production, Sandbox, Development, Test, Trial
- `(Auto-detect)` stores `null` for Type, deferring to discovered type / URL heuristics
- Pre-select based on existing config or discovered type

### Files

`EnvironmentType.cs` (move), `EnvironmentConfig.cs`, `DmlSafetyGuard.cs`, `EnvironmentConfigService.cs`, `IEnvironmentConfigService.cs`, `EnvironmentConfigDialog.cs`, `EnvironmentConfigDialogState.cs`, `TuiColorPalette.cs`, callers of the old string-based APIs.

## Found Bug: DetectProtectionLevel String Mapping

**Problem:** `DmlSafetyGuard.DetectProtectionLevel` (line 59-66) only matches `"developer"` (Discovery API raw value), not `"Development"` (our normalized name). Also missing `"test"` mapping entirely. Result: Development and Test environments silently treated as Production.

**Impact:** DML is incorrectly blocked on non-Production environments that were configured through the TUI (which normalizes to "Development", not "developer").

**Fix:** Subsumed by Fix 5 — enum-based mapping eliminates all string matching.
