# Open in Maker Portal / Open in Dynamics Commands

**Date:** 2026-03-06
**Status:** Approved

## Summary

Add "Open in Maker Portal" and "Open in Dynamics" commands to the VS Code extension. These commands open the browser to the Power Apps Maker portal or Dynamics 365 app for a selected profile's environment, with cross-profile support.

## Design

### Commands

| Command ID | Title | Icon |
|---|---|---|
| `ppds.openInMaker` | PPDS: Open in Maker Portal | `$(link-external)` |
| `ppds.openInDynamics` | PPDS: Open in Dynamics 365 | `$(link-external)` |

### UX Behavior

**From command palette** (`Ctrl+Shift+P`):
- Show quick pick of all profiles that have an environment connected
- Active profile is pre-selected (user presses Enter for fast path)
- Arrow to select a different profile for cross-profile access

**From profile tree context menu** (right-click):
- Uses the right-clicked profile's environment directly, no picker

### URL Construction

- **Maker Portal:** `https://make.powerapps.com/environments/{environmentId}/solutions`
- **Dynamics 365:** `{environment.url}` (the Dataverse URL directly)
- **Maker fallback:** If `environmentId` is null (manual URL profiles), open `https://make.powerapps.com` with info message

### Data Flow Fix

The daemon's `EnvironmentSummary` RPC DTO currently only serializes `url` and `displayName`. The domain model already carries `environmentId`. Fix:

1. Add `environmentId` to RPC DTO in `RpcMethodHandler.cs`
2. Add `environmentId` to TypeScript `EnvironmentSummary` in `types.ts`

### Touch Points

1. **`RpcMethodHandler.cs`** — Add `EnvironmentId` to RPC `EnvironmentSummary` DTO and mapping
2. **`types.ts`** — Add `environmentId` to TypeScript `EnvironmentSummary` interface
3. **`package.json`** — Register commands, add to profile context menu
4. **`profileCommands.ts`** (or new `browserCommands.ts`) — Command implementations
5. **`extension.ts`** — Register commands

### Context Menu Placement

Add to existing `view/item/context` menu for profiles, in a new `open` group after `profile` group:

```json
{
  "command": "ppds.openInMaker",
  "when": "view == ppds.profiles && viewItem == profile",
  "group": "open@1"
},
{
  "command": "ppds.openInDynamics",
  "when": "view == ppds.profiles && viewItem == profile",
  "group": "open@2"
}
```

### Error Handling

- Profile has no environment → show info message "No environment selected for this profile"
- `environmentId` is null → open base Maker URL with info message
- Browser fails to open → show error message
