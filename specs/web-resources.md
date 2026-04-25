# Web Resources

**Status:** Implemented (pull/push: Draft)
**Last Updated:** 2026-04-25
**Code:** [src/PPDS.Dataverse/Services/IWebResourceService.cs](../src/PPDS.Dataverse/Services/IWebResourceService.cs) | [src/PPDS.Extension/src/panels/WebResourcesPanel.ts](../src/PPDS.Extension/src/panels/WebResourcesPanel.ts) | [src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs](../src/PPDS.Cli/Tui/Screens/WebResourcesScreen.cs) | [src/PPDS.Cli/Commands/WebResources/](../src/PPDS.Cli/Commands/WebResources/) | [src/PPDS.Cli/Services/WebResources/IWebResourceSyncService.cs](../src/PPDS.Cli/Services/WebResources/IWebResourceSyncService.cs)
**Surfaces:** CLI, TUI, Extension, MCP

---

## Overview

Browse, view, edit, and publish web resources. Features a FileSystemProvider for in-editor editing with auto-publish notification, conflict detection, and unpublished change detection. Pull/push workflow enables batch download to a local folder with hash tracking and batch upload with server conflict detection. The most complex panel at the VS Code layer due to the full save-conflict-diff-resolve-publish workflow.

### Goals

- **In-editor editing:** Full VS Code FileSystemProvider with save, conflict detection, diff, and publish coordination
- **Unpublished change detection:** Detect and surface differences between published and unpublished web resource content
- **Publish coordination:** Prevent concurrent publish operations per environment via semaphore
- **Multi-surface consistency:** Same data available via VS Code, TUI, MCP, and CLI (Constitution A1, A2)
- **Pull/push workflow:** Download web resources to local folder with tracking, push back with conflict detection (#161, #162)

### Non-Goals

- Web resource creation (managed through solutions)
- Binary content editing (PNG, JPG, GIF, ICO, XAP are view-only)
- CI/CD deployment pipeline automation (pull/push is for developer workflow, not automated deployment)

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
| `IWebResourceSyncService` | Application Service — pull (batch download + tracking), push (conflict check + batch upload) |
| `WebResourceTrackingFile` | Model — `.ppds/webresources.json` serialization, hash computation, read/write |
| `PullCommand.cs` | CLI command — `ppds webresources pull <folder>` |
| `PushCommand.cs` | CLI command — `ppds webresources push <path>` |

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

#### Pull/Push Workflow

##### Sync Service

`IWebResourceSyncService` in `src/PPDS.Cli/Services/WebResources/` — orchestrates pull and push using `IWebResourceService` for Dataverse operations and manages the local tracking file. Accepts `IProgressReporter` (Constitution A3). CancellationToken threaded through all parallel operations (Constitution R2).

All filtering (solution, type codes, name pattern) lives in the sync service, not in CLI command handlers (Constitution A1). The sync service calls `IWebResourceService.ListAsync` then applies type-code and name-pattern filters client-side (same logic currently in `ListCommand.cs`, moved to service layer).

| Method | Purpose |
|--------|---------|
| `PullAsync(options, progress, ct)` | List → filter → parallel download → write files → write tracking file |
| `PushAsync(options, progress, ct)` | Read tracking → detect local changes → conflict check → parallel upload → update tracking |

Progress is reported per-resource from the sync service orchestration level (not from individual `IWebResourceService` calls).

##### Tracking File

Path: `<folder>/.ppds/webresources.json` — co-located with pulled files. One folder = one pull context (typically one solution). Multi-solution support is achieved by pulling to separate folders.

```json
{
  "version": 1,
  "environmentUrl": "https://org.crm.dynamics.com",
  "solution": "core_solution",
  "stripPrefix": true,
  "pulledAt": "2026-04-25T10:30:00Z",
  "resources": {
    "new_/scripts/app.js": {
      "id": "a1b2c3d4-...",
      "modifiedOn": "2026-04-20T14:22:00Z",
      "hash": "sha256:e3b0c44298fc...",
      "localPath": "scripts/app.js",
      "webResourceType": 3
    }
  }
}
```

Fields:
- **version** — schema version for forward compatibility
- **environmentUrl** — the environment pulled from (used by push to validate target)
- **solution** — solution filter used during pull (null if unfiltered)
- **stripPrefix** — whether publisher prefix was stripped from local paths
- **pulledAt** — ISO 8601 timestamp of the pull operation
- **resources** — keyed by Dataverse web resource name (canonical key)
  - **id** — web resource GUID
  - **modifiedOn** — server modifiedOn at pull time (conflict detection baseline)
  - **hash** — SHA256 of local file content at pull time (local change detection)
  - **localPath** — relative path from folder root (may differ from name if prefix stripped)
  - **webResourceType** — type code (needed to map local file back to Dataverse on push)

##### Pull Command

**`ppds webresources pull <folder> [--solution <name>] [--type <type>] [--name <pattern>] [--strip-prefix] [--force]`**

Download web resources from Dataverse to a local folder with tracking metadata.

**Arguments:**
- `<folder>` — target directory (created if not exists)

**Options:**
- `--solution <name>` — filter by solution unique name
- `--type <type>` — filter by type (same shortcuts as `list`: text, image, js, css, etc.)
- `--name <pattern>` — filter by partial name match
- `--strip-prefix` — remove publisher prefix from local file paths (e.g., `new_/scripts/app.js` → `scripts/app.js`)
- `--force` — overwrite local files even if they have uncommitted changes (local hash differs from tracked hash)

**Flow:**
1. List web resources with solution/type/name filters (filtering in sync service per A1)
2. **Path validation:** For each resource, resolve the local file path and verify it is a descendant of `<folder>`. Reject any resource whose name contains path traversal segments (`../`) that would escape the target directory — log warning and skip.
3. If tracking file exists and `--force` not set, check for local modifications (hash mismatch). Warn and skip modified files.
4. Download text content in parallel with CancellationToken threading (R2). Binary types are recorded in the tracking file (metadata only) but no content file is written — `GetContentAsync` returns null for binary types. Push skips binary types.
5. Write files to folder preserving hierarchical path structure
6. **Merge tracking file:** Write/update `.ppds/webresources.json` — new and updated resources get fresh entries (modifiedOn + SHA256 hash), skipped resources retain their prior entries, resources no longer returned by the server query are removed.

**Output (text mode):** Progress to stderr, summary line: "Pulled N web resources to <folder> (M new, K updated, J skipped)"
**Output (JSON mode):** `{ pulled: [...], skipped: [...], errors: [...] }`

**Exit codes:** 0 = success, 2 = failure

##### Push Command

**`ppds webresources push <path> [--solution <name>] [--force] [--dry-run] [--publish]`**

Upload modified web resources from a local folder back to Dataverse with conflict detection.

**Arguments:**
- `<path>` — folder containing pulled web resources (must have `.ppds/webresources.json`)

**Options:**
- `--solution <name>` — override solution scope for `--publish` (default: solution from tracking file). Does not affect content upload — `UpdateContentAsync` is solution-independent.
- `--force` — skip conflict detection (push even if server has changed) and skip environment URL validation
- `--dry-run` — preview what would be pushed without making changes (per dry-run convention)
- `--publish` — publish all successfully pushed web resources after upload

**Flow:**
1. Read `.ppds/webresources.json` — error if missing. **Environment validation:** verify current connection's environment URL matches tracking file's `environmentUrl`. Error if mismatch unless `--force`.
2. For each tracked resource: if local file is missing from disk, warn and skip (does not delete from server). If resource is a binary type (`IsTextType` = false), skip (binary files are pulled for reference only). Otherwise compute local file SHA256. If hash matches tracked hash, skip (no local changes).
3. For each locally modified text resource, query server `modifiedOn` in parallel with CancellationToken threading (R2).
4. **Conflict detection:** If server modifiedOn differs from tracked modifiedOn and `--force` not set, the entire push is blocked — list all conflicting resources to stderr, exit code 10 (`PreconditionFailed`). Suggest: "Run `ppds webresources pull <path>` to fetch latest changes." All-or-nothing: partial push is not supported to avoid ambiguous state.
5. If `--dry-run`: report what would be pushed (resource names, change summary), then exit 0.
6. Upload modified content in parallel via `UpdateContentAsync`. On cancellation, in-flight uploads are cancelled and tracking file is not updated for incomplete uploads.
7. If `--publish`: publish only the IDs of resources successfully uploaded in step 6, via `PublishAsync`.
8. Update tracking file: for each successfully uploaded resource, record new modifiedOn from server + new SHA256 hash of the uploaded content.

**Conflict detection limitation:** The modifiedOn check is best-effort — a TOCTOU window exists between step 3 (query) and step 6 (upload). The Dataverse `Update` API does not support optimistic concurrency tokens for web resource content. For the developer workflow use case, the modifiedOn pre-check catches the common case (someone else edited while you were working). True concurrent-edit races are rare and acceptable.

**Output (text mode):** Progress to stderr, summary: "Pushed N web resources (M conflicts detected)" or "Dry run: would push N web resources"
**Output (JSON mode):** `{ pushed: [...], conflicts: [...], skipped: [...] }`

**Exit codes:** 0 = success, 10 = conflict (PreconditionFailed), 2 = failure

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
| AC-WR-34 | `pull` downloads web resources to target folder preserving hierarchical path structure | `WebResourceSyncServiceTests.PullCreatesDirectoryStructure` | 🔲 |
| AC-WR-35 | `pull` creates `.ppds/webresources.json` tracking file with version, environmentUrl, solution, resources | `WebResourceSyncServiceTests.PullCreatesTrackingFile` | 🔲 |
| AC-WR-36 | Tracking file records modifiedOn timestamp and SHA256 hash per resource | `WebResourceTrackingFileTests.TrackingFileContainsHashAndTimestamp` | 🔲 |
| AC-WR-37 | `pull --strip-prefix` removes publisher prefix from local file paths | `WebResourceSyncServiceTests.StripPrefixRemovesPublisherPrefix` | 🔲 |
| AC-WR-38 | `pull` downloads content in parallel with progress reporting via IProgressReporter | `WebResourceSyncServiceTests.PullDownloadsInParallel` | 🔲 |
| AC-WR-39 | `pull` without `--force` warns and skips files with local modifications (hash mismatch) | `WebResourceSyncServiceTests.PullSkipsLocallyModifiedFiles` | 🔲 |
| AC-WR-40 | `pull --force` overwrites locally modified files | `WebResourceSyncServiceTests.PullForceOverwritesModifiedFiles` | 🔲 |
| AC-WR-41 | `pull` supports `--solution`, `--type`, `--name` filters (filtering in sync service per A1) | `WebResourceSyncServiceTests.PullFiltersResources` | 🔲 |
| AC-WR-42 | `push` reads `.ppds/webresources.json` and errors if missing | `PushCommandTests.PushErrorsOnMissingTrackingFile` | 🔲 |
| AC-WR-43 | `push` detects locally modified files by comparing SHA256 hash, skips unchanged | `WebResourceSyncServiceTests.PushSkipsUnchangedFiles` | 🔲 |
| AC-WR-44 | `push` detects server conflicts by comparing modifiedOn, exits PreconditionFailed (10) | `PushCommandTests.PushConflictReturnsExitCode10` | 🔲 |
| AC-WR-45 | `push --force` skips conflict detection and uploads regardless | `WebResourceSyncServiceTests.PushForceSkipsConflictCheck` | 🔲 |
| AC-WR-46 | `push --dry-run` reports what would be pushed without making changes | `WebResourceSyncServiceTests.PushDryRunNoMutation` | 🔲 |
| AC-WR-47 | `push --publish` publishes only successfully uploaded resource IDs | `WebResourceSyncServiceTests.PushWithPublishCallsPublishAsync` | 🔲 |
| AC-WR-48 | `push` updates tracking file after successful upload (new modifiedOn, new hash) | `WebResourceSyncServiceTests.PushUpdatesTrackingFile` | 🔲 |
| AC-WR-49 | Pull/push business logic lives in `IWebResourceSyncService` (Constitution A1), including filtering | `WebResourceSyncServiceTests.*` | 🔲 |
| AC-WR-50 | Tracking file keyed by Dataverse resource name, supports round-trip pull→edit→push | `WebResourceSyncServiceTests.RoundTripPullEditPush` | 🔲 |
| AC-WR-51 | `pull` rejects resource names with path traversal segments that escape target folder | `WebResourceSyncServiceTests.PullRejectsPathTraversal` | 🔲 |
| AC-WR-52 | `push` validates environment URL matches tracking file, errors on mismatch unless `--force` | `PushCommandTests.PushErrorsOnEnvironmentMismatch` | 🔲 |
| AC-WR-53 | `push` skips binary types (only uploads text types) with warning | `WebResourceSyncServiceTests.PushSkipsBinaryTypes` | 🔲 |
| AC-WR-54 | `push` warns and skips tracked files that are missing from disk | `WebResourceSyncServiceTests.PushSkipsDeletedFiles` | 🔲 |
| AC-WR-55 | `pull` merges tracking file: skipped resources retain prior entries, removed resources are pruned | `WebResourceSyncServiceTests.PullMergesTrackingFile` | 🔲 |

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

### Why co-located tracking file per folder?

**Context:** Pull/push needs state to detect conflicts and track what was downloaded. Options: (A) tracking file inside the target folder, (B) single tracking file at repo root, (C) solution-named files in a central `.ppds/` directory.

**Decision:** Option A — `.ppds/webresources.json` co-located inside the target folder.

**Rationale:** One folder = one pull context (typically one solution). Multi-solution support is achieved naturally by pulling to different folders. No central registry to maintain, no cross-referencing. The tracking file is self-contained and portable — move the folder, tracking moves with it. The `.ppds/` directory is a well-known convention for tool metadata (similar to `.git/`, `.vscode/`).

**Alternatives considered:**
- Central `.ppds/webresources.json` at repo root — harder to reason about with multiple pull targets, single point of contention
- Solution-named files in `.ppds/webresources/` — requires push to discover which tracking file maps to which directory

### Why SHA256 hash + modifiedOn dual tracking?

**Context:** Need to detect both local changes (file edited after pull) and server changes (someone else edited in Dataverse).

**Decision:** Track both SHA256 hash of local file content and server modifiedOn timestamp.

**Rationale:** Hash detects local modifications without touching the server — fast, offline-capable. ModifiedOn detects server-side changes with a single lightweight query per resource. Together they enable the full conflict matrix: no changes (skip), local-only (safe to push), server-only (warn, suggest pull), both changed (conflict, block without --force).

### Why a separate IWebResourceSyncService?

**Context:** Could extend `IWebResourceService` with pull/push methods, or create a new service.

**Decision:** New `IWebResourceSyncService` that depends on `IWebResourceService`.

**Rationale:** Pull/push is a higher-level orchestration concern — file system I/O, tracking file management, parallel coordination, conflict detection. The existing `IWebResourceService` is a clean Dataverse CRUD interface. Mixing file system operations into it would violate single responsibility. The sync service composes the CRUD service rather than extending it.

---

## Changelog

| Date | Change |
|------|--------|
| 2026-04-25 | Added pull/push workflow specification (#161, #162): IWebResourceSyncService, tracking file, pull/push CLI commands, ACs 34–55. Post-review fixes: path traversal protection, TOCTOU documentation, binary type scope, environment URL validation, tracking file merge semantics, deleted file handling |
| 2026-03-23 | Added CLI surface (list, get, url), name resolution, publish alias; removed "offline editing" from non-goals (deferred to post-v1) |
| 2026-03-18 | Extracted from panel-parity.md per SL1 |

---

## Roadmap

- **Diff** — local file vs server comparison, depends on pull/push (#163)

---

## Related Specs

- [publish.md](./publish.md) — Cross-cutting publish command (`ppds publish`)
- [architecture.md](./architecture.md) — Application Service boundary
- [connection-pooling.md](./connection-pooling.md) — Dataverse connection management, publish coordination
- [CONSTITUTION.md](./CONSTITUTION.md) — Governing principles
