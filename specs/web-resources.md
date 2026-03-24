# Web Resources

**Status:** Implemented
**Last Updated:** 2026-03-23
**Code:** [src/PPDS.Dataverse/Services/IWebResourceService.cs](../src/PPDS.Dataverse/Services/IWebResourceService.cs) | [src/PPDS.Extension/src/panels/WebResourcesPanel.ts](../src/PPDS.Extension/src/panels/WebResourcesPanel.ts) | [src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs](../src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs) | [src/PPDS.Cli/Commands/WebResources/](../src/PPDS.Cli/Commands/WebResources/)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Browse, view, edit, and publish web resources. Features a FileSystemProvider for in-editor editing with auto-publish notification, conflict detection, and unpublished change detection. The most complex panel at the VS Code layer due to the full save-conflict-diff-resolve-publish workflow.

### Goals

- **In-editor editing:** Full VS Code FileSystemProvider with save, conflict detection, diff, and publish coordination
- **Unpublished change detection:** Detect and surface differences between published and unpublished web resource content
- **Publish coordination:** Prevent concurrent publish operations per environment via semaphore
- **Multi-surface consistency:** Same data available via VS Code, TUI, MCP, and CLI (Constitution A1, A2)

### Non-Goals

- Web resource creation (managed through solutions)
- Binary content editing (PNG, JPG, GIF, ICO, XAP are view-only)
- Bulk import/export of web resources (deployment pipeline concern)

---

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│                      VS Code Extension                        │
│  ┌───────────────────┐  ┌──────────────────────────────────┐ │
│  │ WebResourcesPanel │  │ WebResourceFileSystemProvider     │ │
│  │  (table, filter,  │  │  (ppds-webresource:// scheme,    │ │
│  │   text-only toggle)│  │   open/save/conflict/publish)   │ │
│  └────────┬──────────┘  └──────────────┬───────────────────┘ │
│           │                            │                      │
│      JSON-RPC                     JSON-RPC                    │
│           │                            │                      │
│  ┌────────▼────────────────────────────▼───────────────────┐ │
│  │              Daemon (RPC Handlers)                       │ │
│  └────────┬────────────────────────────────────────────────┘ │
│           │                                                   │
│  ┌────────▼────────────────────────────────────────────────┐ │
│  │              IWebResourceService                         │ │
│  │  ListAsync, GetAsync, GetContentAsync, GetModifiedOnAsync│ │
│  │  UpdateContentAsync, PublishAsync, PublishAllAsync        │ │
│  └────────┬────────────────────────────────────────────────┘ │
│           │                                                   │
│  ┌────────▼────────────────────────────────────────────────┐ │
│  │  IDataverseConnectionPool + Publish Coordination         │ │
│  │  Per-environment SemaphoreSlim (PooledClientExtensions)  │ │
│  └──────────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────────┘

┌───────────┐  ┌──────┐
│   TUI     │  │ MCP  │   (Direct service calls, no FSP)
│  Screen   │  │ Tool │
└─────┬─────┘  └──┬───┘
      │            │
      ▼            ▼
  IWebResourceService
```

### Components

| Component | Responsibility |
|-----------|----------------|
| `IWebResourceService` | Domain service — list, get, content (published/unpublished), update, publish, publishAll |
| `WebResourceNameResolver` | Shared name resolution — GUID detection, exact match, partial match, ambiguity handling |
| `WebResourcesCommandGroup.cs` | CLI command group — list, get, url subcommands + publish alias |
| `WebResourceFileSystemProvider` | VS Code FSP — ppds-webresource:// scheme, conflict detection, publish coordination |
| `WebResourcesPanel.ts` | VS Code webview panel — virtual table, solution filter, text-only toggle, FSP integration |
| `PublishCoordinator` | Per-environment `SemaphoreSlim` in `PooledClientExtensions.cs:22` — shared by PublishXml and PublishAllXml |
| `WebResourcesScreen.cs` | TUI screen — data table, content viewer, publish confirmation |
| `WebResourcesListTool.cs` | MCP tool — web resource listing with type and metadata |
| `WebResourcesGetTool.cs` | MCP tool — full detail with decoded content for text types |
| `WebResourcesPublishTool.cs` | MCP tool — publish result |

### Dependencies

- Depends on: [connection-pooling.md](./connection-pooling.md) for pooled Dataverse clients and publish coordination
- Depends on: [architecture.md](./architecture.md) for Application Service boundary
- Uses patterns from: [CONSTITUTION.md](./CONSTITUTION.md) — A1, A2, A3, D1, D4, S1

---

## Specification

### Service Layer

**Service design:** `IWebResourceService` in `src/PPDS.Dataverse/Services/`:

| Method | Purpose |
|--------|---------|
| `ListAsync(solutionId?, textOnly?, top?)` | Query web resources with solution and type filters |
| `GetAsync(id)` | Get web resource metadata |
| `GetContentAsync(id, published?)` | Get content — uses RetrieveUnpublished for unpublished, standard query for published |
| `GetModifiedOnAsync(id)` | Lightweight query for conflict detection (modifiedon only) |
| `UpdateContentAsync(id, content)` | Update content (base64 encoded) — does NOT publish |
| `PublishAsync(ids)` | Publish specific web resources via PublishXml (coordinated) |
| `PublishAllAsync()` | Publish all customizations via PublishAllXml (coordinated) |

**Web resource types:** HTML (1), CSS (2), JavaScript (3), XML (4), PNG (5), JPG (6), GIF (7), XAP (8), XSL (9), ICO (10), SVG (11), RESX (12)

**Text types (editable):** 1, 2, 3, 4, 9, 11, 12
**Binary types (view metadata only):** 5, 6, 7, 8, 10

**RPC Endpoints:**

| Method | Request | Response |
|--------|---------|----------|
| `webResources/list` | `{ solutionId?, textOnly?, environmentUrl? }` | `{ resources: WebResourceInfo[] }` |
| `webResources/get` | `{ id, published?, environmentUrl? }` | `{ resource: WebResourceDetail }` |
| `webResources/getModifiedOn` | `{ id, environmentUrl? }` | `{ modifiedOn: string }` |
| `webResources/update` | `{ id, content, environmentUrl? }` | `{ success: boolean }` |
| `webResources/publish` | `{ ids, environmentUrl? }` | `{ publishedCount: number }` |
| `webResources/publishAll` | `{ environmentUrl? }` | `{ success: boolean }` |

**WebResourceInfo fields:** id, name, displayName, type, typeName, isManaged, createdBy, createdOn, modifiedBy, modifiedOn

**WebResourceDetail fields:** all of WebResourceInfo + content (decoded string for text types, null for binary)

### CLI Surface

#### Name Resolution

All commands that accept a web resource identifier use shared resolution logic:

1. **GUID** — if the argument parses as a GUID, look up directly by ID
2. **Exact name** — query for a resource whose `name` matches exactly
3. **Partial match** — query for resources whose `name` ends with the argument (e.g., `app.js` matches `new_/scripts/app.js`)

On ambiguity (multiple matches):
- `list` — shows all matches (expected behavior)
- `get`, `url` — error with list of matches, exit code 1

#### Commands

**`ppds webresources list [name-pattern] [--solution <name>] [--type <type>] [--unpublished] [--top <n>]`**

List web resources with optional filters. Positional argument is a partial name filter.

Table columns (default): Name, Type, Managed, Modified On, Modified By. Full data in `--json` mode.

Type shortcuts: `--type text` (HTML/CSS/JS/XML/XSL/SVG/RESX), `--type image` (PNG/JPG/GIF/ICO/SVG), or specific type (`--type js`, `--type css`).

**`ppds webresources get <name|id> [--unpublished] [--output <path>]`**

Get web resource content. Defaults to published content (SDK convention). `--unpublished` fetches the latest saved draft via `RetrieveUnpublished`.

Output: content to stdout (pipeable per Constitution I1). `--output <path>` writes to file. Binary types error on stdout with message to use `--output`.

**`ppds webresources url <name|id>`**

Get the Maker portal URL for a web resource. Follows existing `UrlCommand` pattern (Solutions, Flows, etc.). URL to stdout in text mode, structured JSON in `--json` mode.

**`ppds webresources publish <name|id>... [--solution <name>]`**

Alias for `ppds publish --type webresource`. Auto-injects `--type webresource`. See [publish.md](./publish.md).

### Extension Surface

- **viewType:** `ppds.webResources`
- **Layout:** Three-zone with virtual table
- **Table columns:** Name (clickable link for text types), Display Name, Type (with icon), Managed, Created By, Created On, Modified By, Modified On
- **Default sort:** name ascending
- **Solution filter:** Dropdown (persisted). Smart strategy: small solutions (<=100 components) use OData filter, large solutions fetch all + client-side filter (URL length limits)
- **Type filter:** Toggle — "Text only" (default) vs "All"
- **Search:** Client-side substring match with server-side OData fallback
- **Request versioning:** Stale response protection during rapid solution changes

#### FileSystemProvider

- **URI scheme:** `ppds-webresource:///environmentId/webResourceId/filename.ext`
- **Content modes:** unpublished (default, editable), published (read-only for diff), conflict, server-current, local-pending
- **On open flow:**
  1. Fetch published + unpublished content in parallel
  2. If they differ: show diff with "Edit Unpublished" / "Edit Published" / "Cancel"
  3. Open chosen version; set language mode from web resource type
- **On save flow:**
  1. No-change detection — skip if content unchanged
  2. Conflict detection — compare cached modifiedOn with server's current
  3. If conflict: modal (Compare First / Overwrite / Discard My Work)
  4. If Compare First: open diff view (server-current left, local-pending right), then resolution modal (Save My Version / Use Server Version / Cancel)
  5. On success: fire change + save events, show non-modal "Saved: filename" notification with Publish button
  6. Cache refresh: fetch new modifiedOn from server
- **Caching:** serverState (modifiedOn + lastKnownContent), preFetchedContent, pendingFetches (deduplication), pendingSaveContent
- **Publish coordination:** PublishCoordinator prevents concurrent publish operations per environment
- **Auto-refresh:** Panel subscribes to onDidSaveWebResource event, updates row without full reload
- **Language detection:** Maps file extension to VS Code language ID (js->javascript, css->css, html->html, xml->xml, etc.)
- **Binary protection:** NonEditableWebResourceError for binary types (PNG/JPG/GIF/ICO/XAP)

### TUI Surface

- **Class:** `WebResourcesScreen` extending `TuiScreenBase`
- **Hotkeys:** Ctrl+R (refresh), Enter (view content dialog for text types), Ctrl+P (publish selected), Ctrl+F (filter by solution), Ctrl+T (toggle text-only/all), Ctrl+O (open in Maker)
- **Dialogs:** `WebResourceContentDialog` (read-only text view), `PublishConfirmDialog`

### MCP Surface

| Tool | Input | Output |
|------|-------|--------|
| `ppds_web_resources_list` | `{ solutionId?, textOnly?, environmentUrl? }` | Web resource list with type and metadata |
| `ppds_web_resources_get` | `{ id }` | Full detail with decoded content for text types |
| `ppds_web_resources_publish` | `{ ids }` | Publish result |

---

## Acceptance Criteria

| ID | Criterion | Test | Status |
|----|-----------|------|--------|
| AC-WR-01 | `IWebResourceService` created with list, get (unpublished + published), update, and publish | TBD | ✅ |
| AC-WR-02 | `webResources/list` returns web resources with solution and type filters | TBD | ✅ |
| AC-WR-03 | `webResources/get` returns decoded content using RetrieveUnpublished | TBD | ✅ |
| AC-WR-04 | `webResources/publish` calls PublishXml for single/batch; PublishAllXml for all | TBD | ✅ |
| AC-WR-05 | FileSystemProvider registers ppds-webresource scheme with environment-scoped URIs | TBD | ✅ |
| AC-WR-06 | On open: detects unpublished changes, shows diff if different | TBD | ✅ |
| AC-WR-07 | On save: conflict detection with full resolution flow (compare, diff, resolve) | TBD | ✅ |
| AC-WR-08 | On save: non-modal notification with Publish button | TBD | ✅ |
| AC-WR-09 | Auto-refresh: panel row updates on save event without full reload | TBD | ✅ |
| AC-WR-10 | Publish coordination prevents concurrent publishes per environment | TBD | ✅ |
| AC-WR-11 | Solution filter: OData for small solutions, client-side for large | TBD | ✅ |
| AC-WR-12 | Request versioning discards stale responses during rapid solution changes | TBD | ✅ |
| AC-WR-13 | VS Code panel displays virtual table with type icons, solution filter, text-only toggle | TBD | ✅ |
| AC-WR-14 | Language mode auto-detected from web resource type on document open | TBD | ✅ |
| AC-WR-15 | TUI WebResourcesScreen displays list; Enter opens content dialog for text types | TBD | ✅ |
| AC-WR-16 | MCP tools return decoded content for AI analysis and support publish | TBD | ✅ |
| AC-WR-17 | Virtual table handles 1000+ web resources with pagination | TBD | ✅ |
| AC-WR-18 | Binary types viewable in list but not editable — clear error on edit attempt | TBD | ✅ |
| AC-WR-19 | Search input in toolbar with debounced client-side substring filtering (300ms) | TBD | 🔲 |
| AC-WR-20 | `copyToClipboard` message handler copies selected row data | TBD | 🔲 |
| AC-WR-21 | Panel uses `SolutionFilter` shared component (not raw `<select>`) | TBD | 🔲 |
| AC-WR-22 | Publish All button in VS Code panel calls `webResources/publishAll` | TBD | 🔲 |
| AC-WR-23 | TUI Publish All hotkey with confirmation dialog | TBD | 🔲 |
| AC-WR-24 | CLI `list` displays Name, Type, Managed, Modified On, Modified By in table mode | TBD | 🔲 |
| AC-WR-25 | CLI `list` supports partial name matching as positional argument | TBD | 🔲 |
| AC-WR-26 | CLI `list` supports `--solution`, `--type` (with shortcuts), `--top` filters | TBD | 🔲 |
| AC-WR-27 | CLI `get` outputs published content to stdout by default | TBD | 🔲 |
| AC-WR-28 | CLI `get` with `--unpublished` returns latest draft via RetrieveUnpublished | TBD | 🔲 |
| AC-WR-29 | CLI `get` with `--output` writes content to file | TBD | 🔲 |
| AC-WR-30 | CLI `get` errors on binary type to stdout with message to use `--output` | TBD | 🔲 |
| AC-WR-31 | CLI `url` generates Maker portal URL, outputs to stdout | TBD | 🔲 |
| AC-WR-32 | Name resolution: GUID → exact name → partial match; error on ambiguity for get/url | TBD | 🔲 |
| AC-WR-33 | CLI `webresources publish` is alias for `ppds publish --type webresource` | TBD | 🔲 |

---

## Design Decisions

### Why FileSystemProvider for Web Resources?

**Context:** Could simplify to read-only preview + manual publish.

**Decision:** Full FileSystemProvider with conflict detection, unpublished change detection, auto-publish notification, and publish coordination.

**Rationale:** The save, conflict detect, diff, resolve, publish flow is the core developer experience. Simplifying would be a regression from the legacy extension. The daemon architecture changes transport (RPC) but preserves behavior.

### Why per-environment publish semaphore?

**Context:** Multiple web resources can be saved in rapid succession. PublishXml and PublishAllXml are heavy operations that conflict if concurrent.

**Decision:** Per-environment `SemaphoreSlim` shared by PublishXml and PublishAllXml, coordinated through `PooledClientExtensions`.

**Rationale:** Prevents race conditions where two publish calls overlap, potentially causing partial publishes or platform errors. Per-environment scoping allows concurrent work across different environments.

### Why smart solution filtering strategy?

**Context:** Solution filter uses OData `$filter` query parameter. Large solutions with many component IDs exceed URL length limits.

**Decision:** Two strategies: small solutions (<=100 components) use server-side OData filter; large solutions fetch all web resources and filter client-side.

**Rationale:** Server-side filtering is faster for small solutions. Client-side filtering avoids URL length errors for large solutions. The threshold (100) balances network efficiency against URL limits.

### Why request versioning for stale responses?

**Context:** Rapid solution switching in the dropdown can cause earlier (slower) responses to arrive after later (faster) ones, displaying stale data.

**Decision:** Each request gets an incrementing version number. Responses from older versions are discarded.

**Rationale:** Standard stale-response protection pattern. Without it, the panel can show web resources from a previously selected solution after the user has already switched to a different one.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-03-23 | Added CLI surface (list, get, url), name resolution, publish alias; removed "offline editing" from non-goals (deferred to post-v1) |
| 2026-03-18 | Extracted from panel-parity.md per SL1 |

---

## Roadmap

- **Pull/push workflow** — download web resources to local folder with hash tracking, push back with conflict detection (#161, #162)
- **Diff** — local file vs server comparison, depends on pull/push (#163)

---

## Related Specs

- [publish.md](./publish.md) — Cross-cutting publish command (`ppds publish`)
- [architecture.md](./architecture.md) — Application Service boundary
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection management, publish coordination
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles
