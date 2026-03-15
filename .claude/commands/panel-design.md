# Panel Design

Design a new panel or feature across all four PPDS surfaces: Daemon RPC, VS Code extension, TUI, and MCP.

## Input

$ARGUMENTS = feature domain name (e.g., `import-jobs`, `plugin-traces`, `web-resources`)

## Process

### Step 1: Load Foundation

Read before doing anything:
- `specs/CONSTITUTION.md` — A1, A2 govern multi-surface design (services are single code path, UI is thin wrapper)
- `specs/architecture.md` — cross-cutting patterns
- Domain-relevant specs via `specs/README.md`

### Step 2: Inventory Existing Infrastructure

For the target domain, check what exists at each layer:

| Layer | Location |
|-------|----------|
| Domain services | `src/PPDS.Dataverse/Services/` (Dataverse data access) |
| Application services | `src/PPDS.Cli/Services/` (orchestration, cross-cutting) |
| Entity classes | `src/PPDS.Dataverse/Generated/Entities/` |
| CLI commands | `src/PPDS.Cli/Commands/{Domain}/` |
| Daemon RPC methods | `src/PPDS.Cli/Commands/Serve/Handlers/RpcMethodHandler.cs` |
| MCP tools | `src/PPDS.Mcp/Tools/` |
| TUI screens/dialogs | `src/PPDS.Cli/Tui/Screens/`, `src/PPDS.Cli/Tui/Dialogs/` |
| VS Code panels | Extension `src/panels/` |

Classify each as: **Ready**, **Partial** (needs extension), or **Missing** (must create).

**Prerequisites before UI design:**
- If the service is Missing, design it first — A1/A2 mandate services as the single code path.
- If required entity classes don't exist in `Generated/Entities/`, entity generation is a prerequisite (Constitution I2 — never hand-edit generated entities).
- If the domain requires Power Platform APIs outside the Dataverse SDK (e.g., Connections API via `service.powerapps.com`), identify those endpoints and any auth scope differences (`IPowerPlatformTokenProvider`).

### Step 3: Design RPC Endpoints and Data Contract

For each user-facing operation, define an RPC method:

- Method name: `{domain}/{operation}` (e.g., `importJobs/list`, `importJobs/get`, `pluginTraces/timeline`)
- Request/Response DTOs with typed fields — these DTOs are the shared contract consumed by VS Code and MCP
- Error codes as domain-specific `PpdsException` ErrorCodes (D4)
- Thread `CancellationToken` through the entire async chain (R2)
- Accept `IProgressReporter` for operations expected to take >1 second (A3)

**RPC handler pattern:** Methods are decorated with `[JsonRpcMethod("domain/operation")]` in `RpcMethodHandler.cs`. Handlers use `IDaemonConnectionPoolManager` for Dataverse access. Read existing methods for the pattern — no business logic in the handler, just parameter mapping → service call → DTO mapping.

### Step 4: Design VS Code Panel

Reference the `@webview-panels` skill (`.claude/skills/webview-panels/SKILL.md`) for implementation patterns (panel anatomy, message protocol, CSS patterns, environment theming).

Define:
- Panel viewType and display name
- Message protocol (host ↔ webview discriminated union types)
- Data display format (table, tree, detail pane, split layout)
- User actions (toolbar buttons, context menus, keyboard shortcuts)
- Environment scoping behavior (per-panel environment selection)

### Step 5: Design TUI Screen

Reference existing screens in `src/PPDS.Cli/Tui/Screens/` for patterns.

Define:
- Screen class name and menu/tab integration
- Layout (single table, split pane, tabbed)
- Hotkey bindings via `HotkeyRegistry`
- Dialog inventory (detail views, filters, confirmations)
- Streaming vs batch data loading strategy

TUI screens call Application Services directly (no RPC indirection).

### Step 6: Design MCP Tools

Define tools for what AI agents need from this domain:

- Tool name: `ppds_{domain}_{operation}`
- Input schema with parameter descriptions
- Output format structured for AI reasoning

Not every panel action needs an MCP tool. Focus on read/query operations. Skip UI-only actions (open in browser, keyboard shortcuts, export to clipboard).

### Step 7: Define Acceptance Criteria

For each surface, define "complete." Use legacy panel behavior as the floor:

- **Data parity:** Same fields/columns displayed as legacy
- **Action parity:** Same operations available (refresh, filter, export, open in Maker, etc.)
- **UX parity:** Same drill-down navigation, solution filtering, detail views
- **Enhancements:** Improvements enabled by new architecture (better data from existing services, environment theming, per-panel environment scoping)

Number all criteria: AC-01, AC-02, etc. (Constitution I3).

### Step 8: Map to Work Items

- Map design to existing GitHub issues
- Flag stale issues that need updating
- Identify gaps requiring new issues
- Recommend worktree/branch strategy based on complexity and dependencies

## Rules

1. **Services first, UI second** — if the Application Service doesn't exist, design that before any UI surface.
2. **Reference existing panels** — the first panel designed establishes patterns. Subsequent panels follow them.
3. **Legacy as floor, not ceiling** — match what existed, improve where the new architecture enables it.
4. **Don't port code** — understand what the legacy did, then design the proper abstraction. No inheriting legacy patterns.
5. **One panel at a time** — complete design for one panel across all surfaces before starting the next.
6. **Cross-reference surface parity** — VS Code panel and TUI screen must expose equivalent functionality (same data, same filters, same actions). MCP tools must cover the same read/query operations for AI agent access.
7. **Update skills after pattern-setting work** — when the first panel in a batch establishes new patterns (e.g., virtual scrolling, complex state management), update referenced skills (`@webview-panels`, TUI equivalents) with the proven patterns before implementing subsequent panels. Include this as an explicit step in the implementation plan.
